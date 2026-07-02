using Microsoft.Data.Sqlite;

namespace Deduplicator.Core.Data;

/// <summary>
/// Управление подключениями к SQLite (режим WAL для краш-устойчивости и параллельного чтения, NFR-2).
/// Каждое подключение короткоживущее; запись — через транзакции.
/// </summary>
public sealed class Database
{
    public string Path { get; }
    private readonly string _connectionString;

    public Database(string dbPath)
    {
        Path = dbPath;
        var dir = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>Открывает подготовленное подключение (с применёнными PRAGMA).</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 30000;
                PRAGMA temp_store = MEMORY;
                """;
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>Создаёт схему, если её ещё нет. Идемпотентно.</summary>
    public void Initialize()
    {
        using var conn = Open();

        // Миграции — ДО основной схемы. Schema.Sql создаёт индексы по колонкам, которые на СТАРОЙ
        // базе появляются только миграцией (idx_groups_session_type → scan_session_id): прежний
        // порядок ронял CREATE INDEX ещё до миграций, и приложение после обновления версии падало
        // на старте (найдено пробой композиции на реальной пользовательской БД, 2026-07-02).
        // На свежей БД таблиц ещё нет — миграция пропускается, колонки создаст CREATE TABLE.
        foreach (var (table, column, ddl) in Schema.ColumnMigrations)
        {
            if (!TableExists(conn, table)) continue;
            if (ColumnExists(conn, table, column)) continue;
            using var m = conn.CreateCommand();
            m.CommandText = ddl;
            m.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema.Sql;
        cmd.ExecuteNonQuery();

        // Носители: устранить дубли по серийнику и навесить UNIQUE-индекс,
        // чтобы гонка при параллельной синхронизации больше не плодила копии одного диска.
        DeduplicateVolumesAndEnforceUniqueSerial(conn);
    }

    /// <summary>
    /// Одноразовая миграция: схлопывает дублирующиеся строки <c>volumes</c> с одинаковым серийником
    /// (файлы перенаправляются на «выжившую» запись), затем создаёт UNIQUE-индекс по serial.
    /// Дедуп выполняется в C# (сравнение строк надёжно и не зависит от особенностей сравнения в БД).
    /// Полностью защищена try/catch — сбой чистки НИКОГДА не должен ронять запуск приложения.
    /// </summary>
    private static void DeduplicateVolumesAndEnforceUniqueSerial(SqliteConnection conn)
    {
        try
        {
            if (IndexExists(conn, "ux_volumes_serial")) return;

            // 1) Найти дубли по нормализованному серийнику (в C#, не средствами SQL).
            var keepBySerial = new Dictionary<string, long>(StringComparer.Ordinal);
            var losers = new List<(long Loser, long Keep)>();
            using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT id, serial FROM volumes WHERE serial IS NOT NULL ORDER BY id";
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    string serial = (r.IsDBNull(1) ? "" : r.GetString(1)).Trim();
                    if (serial.Length == 0) continue;
                    if (keepBySerial.TryGetValue(serial, out var keep)) losers.Add((id, keep));
                    else keepBySerial[serial] = id;
                }
            }

            // 2) Перенаправить файлы дублей на выжившую запись и удалить лишние строки.
            if (losers.Count > 0)
            {
                using var tx = conn.BeginTransaction();
                foreach (var (loser, keep) in losers)
                {
                    Exec(conn, tx,
                        "DELETE FROM files WHERE volume_id=$l AND rel_path IN (SELECT rel_path FROM files WHERE volume_id=$k)",
                        ("$l", loser), ("$k", keep));
                    Exec(conn, tx, "UPDATE files SET volume_id=$k WHERE volume_id=$l", ("$k", keep), ("$l", loser));
                    Exec(conn, tx, "DELETE FROM volumes WHERE id=$l", ("$l", loser));
                }
                tx.Commit();
            }

            // 3) UNIQUE-индекс (после дедупа). Best-effort: если вдруг не создастся —
            //    атомарность Upsert обеспечивается транзакцией BEGIN IMMEDIATE и без него.
            using var ix = conn.CreateCommand();
            ix.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_volumes_serial ON volumes(serial)";
            ix.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try { Deduplicator.Core.Util.AppLog.Error("VolumeDedupMigration", ex); } catch { /* лог недоступен — не критично */ }
        }
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static bool IndexExists(SqliteConnection conn, string indexName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", indexName);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // PRAGMA table_info: cid(0), name(1), type(2)...
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Сжать WAL в основной файл (вызывать при простое/закрытии).</summary>
    public void Checkpoint()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { /* checkpoint — оптимизация, не критично */ }
    }

    /// <summary>
    /// Лёгкий неблокирующий чекпоинт (PASSIVE) — вызывать между пачками во время долгой воронки,
    /// когда нет открытых reader-ов, чтобы WAL не рос на весь скан. В отличие от TRUNCATE не ждёт
    /// читателей и не копирует весь WAL целиком.
    /// </summary>
    public void PassiveCheckpoint()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
        catch { /* чекпоинт — оптимизация, не критично */ }
    }
}
