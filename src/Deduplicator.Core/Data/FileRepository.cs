using Microsoft.Data.Sqlite;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Доступ к слепкам файлов и выборкам для воронки дедупликации.</summary>
public readonly record struct ExistingFileMeta(long Id, long Size, DateTimeOffset Mtime, DateTimeOffset Created);

public sealed class FileRepository
{
    private readonly Database _db;
    public FileRepository(Database db) => _db = db;

    private const string Cols =
        "f.id, f.volume_id, f.rel_path, f.name, f.ext, f.size, f.mtime, f.created, f.category, " +
        "f.partial_hash, f.full_hash, f.phash, f.pixel_hash, f.audio_fp, f.scan_state, f.last_indexed, " +
        "f.partial_ver, f.phash_ver, f.verified_full_hash, f.sharpness";

    // ---- Чтение ----

    public FileRecord? GetByVolumeAndPath(long volumeId, string relPath)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.volume_id=$v AND f.rel_path=$p LIMIT 1";
        cmd.Add("$v", volumeId);
        cmd.Add("$p", relPath);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>
    /// Существующие метаданные ПАЧКИ путей одним SELECT на чанк (2.7): инкрементальный скан больше
    /// не грузит весь индекс тома в словарь (250–500 МБ на 1–2 млн файлов), а решает судьбу файлов
    /// пакетами по нескольку сотен (паттерн AddScope). Сравнение rel_path в SQL — байтовое, как и
    /// UNIQUE-индекс files: путь с изменённым регистром (после переименования) не совпадёт и будет
    /// переиндексирован заново — это безопасно (лишний перехэш, а не пропуск изменения).
    /// </summary>
    public Dictionary<string, ExistingFileMeta> GetExistingMetaByPaths(long volumeId, IReadOnlyList<string> relPaths)
    {
        var map = new Dictionary<string, ExistingFileMeta>(StringComparer.OrdinalIgnoreCase);
        if (relPaths.Count == 0) return map;

        using var conn = _db.Open();
        foreach (var chunk in relPaths.Chunk(400))
        {
            using var cmd = conn.CreateCommand();
            var names = new List<string>(chunk.Length);
            for (int i = 0; i < chunk.Length; i++) { var n = "$p" + i; names.Add(n); cmd.Add(n, chunk[i]); }
            cmd.Add("$v", volumeId);
            cmd.CommandText = "SELECT id, rel_path, size, mtime, created FROM files " +
                              $"WHERE volume_id=$v AND rel_path IN ({string.Join(",", names)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var relPath = r.Str(1);
                if (string.IsNullOrWhiteSpace(relPath)) continue;
                map[relPath] = new ExistingFileMeta(r.Long(0), r.Long(2), Db.ParseDate(r.Str(3)), Db.ParseDate(r.Str(4)));
            }
        }
        return map;
    }

    public FileRecord? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.id=$id";
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>
    /// После физического переименования (P0.1) обновляем путь/имя/расширение/метаданные строки, чтобы
    /// UI и удаление работали по НОВОМУ пути, а не по устаревшему. Содержимое не менялось → хэши не трогаем.
    /// </summary>
    public void UpdatePathAfterRename(long fileId, long volumeId, string newRelPath, string newName, string? newExt,
        DateTimeOffset newMtime, DateTimeOffset newCreated, long newSize)
        => UpdatePathsAfterRename(new[]
        {
            new RenameDbUpdate(fileId, volumeId, newRelPath, newName, newExt, newMtime, newCreated, newSize)
        });

    /// <summary>
    /// Пакетное обновление путей после группового переименования — одна транзакция, ДВА прохода.
    /// Прежняя по-файловая версия удаляла строку ЖИВОГО файла при пересечении имён внутри пакета
    /// (обычный сдвиг нумерации: новое имя одного = старое имя другого) и маскировала это как успех
    /// (UPDATE затрагивал 0 строк). Теперь: (1) все строки пакета уводятся на уникальные временные
    /// rel_path ("ren:id" - двоеточие в именах файлов Windows недопустимо, коллизий нет), (2) устаревшие
    /// строки ЧУЖИХ записей на целевых путях снимаются (пакетные уже на временных путях и под
    /// удар не попадают), (3) финальные значения. 0 затронутых строк — фиксируется как сбой.
    /// Возвращает file_id строк, которые обновить не удалось.
    /// </summary>
    public List<long> UpdatePathsAfterRename(IReadOnlyList<RenameDbUpdate> updates)
    {
        var failed = new List<long>();
        if (updates.Count == 0) return failed;

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var ok = new HashSet<long>();

        // Проход 1: на временные уникальные пути (разрывает пересечения имён внутри пакета).
        foreach (var u in updates)
        {
            using var tmp = conn.CreateCommand();
            tmp.Transaction = tx;
            tmp.CommandText = "UPDATE files SET rel_path=$tmp WHERE id=$id AND volume_id=$v";
            tmp.Add("$tmp", "ren:" + u.FileId);
            tmp.Add("$id", u.FileId);
            tmp.Add("$v", u.VolumeId);
            if (tmp.ExecuteNonQuery() == 1) ok.Add(u.FileId);
            else failed.Add(u.FileId); // строки нет в БД — честно сообщаем, а не молчим
        }

        foreach (var u in updates)
        {
            if (!ok.Contains(u.FileId)) continue;

            // Устаревшая ЧУЖАЯ строка на целевом пути (её файл физически заменён нашим переименованием).
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM files WHERE volume_id=$v AND rel_path=$rel AND id<>$id";
                del.Add("$v", u.VolumeId);
                del.Add("$rel", u.NewRelPath);
                del.Add("$id", u.FileId);
                del.ExecuteNonQuery();
            }

            // Проход 2: финальные значения.
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE files
                SET rel_path=$rel, name=$name, ext=$ext, mtime=$mtime, created=$created, size=$size, last_indexed=$idx
                WHERE id=$id;
                """;
            cmd.Add("$rel", u.NewRelPath);
            cmd.Add("$name", u.NewName);
            cmd.Add("$ext", u.NewExt);
            cmd.Add("$mtime", Db.Iso(u.NewMtime));
            cmd.Add("$created", Db.Iso(u.NewCreated));
            cmd.Add("$size", u.NewSize);
            cmd.Add("$idx", Db.IsoNow());
            cmd.Add("$id", u.FileId);
            if (cmd.ExecuteNonQuery() != 1) failed.Add(u.FileId);
        }
        tx.Commit();
        return failed;
    }

    public List<FileRecord> GetByVolume(long volumeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.volume_id=$v ORDER BY f.rel_path";
        cmd.Add("$v", volumeId);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    // 3.4: строки «с хоть одним хэшем/признаком» — кандидаты на выгрузку в переносимый кэш.
    private const string AnyHashCondition =
        "(f.partial_hash IS NOT NULL OR f.full_hash IS NOT NULL OR f.phash IS NOT NULL " +
        "OR f.pixel_hash IS NOT NULL OR f.audio_fp IS NOT NULL)";

    /// <summary>3.4: сколько строк тома имеют хоть один хэш (для капа экспорта .dedupindex).</summary>
    public long CountHashedByVolume(long volumeId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM files f WHERE f.volume_id=$v AND {AnyHashCondition}";
        cmd.Add("$v", volumeId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 3.4: страница строк тома с хоть одним хэшем (keyset по id, паттерн GetNeedingPerceptualPage) —
    /// потоковая выгрузка в переносимый кэш без материализации всего тома в памяти.
    /// </summary>
    public List<FileRecord> GetHashedByVolumePage(long volumeId, long afterId, int limit)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.volume_id=$v AND f.id>$a AND {AnyHashCondition} " +
                          "ORDER BY f.id LIMIT $lim";
        cmd.Add("$v", volumeId);
        cmd.Add("$a", afterId);
        cmd.Add("$lim", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    // --- P1.2: область скана (scan_scope_files) ---

    /// <summary>Старт приложения: чистим ВСЕ области сканов. Они нужны только во время активного
    /// скана; после краха процесса строки оставались навсегда и таблица росла неограниченно.</summary>
    public void ClearAllScopes()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scan_scope_files";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Очистить область текущей сессии (перед заполнением).</summary>
    public void ClearScope(long sessionId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scan_scope_files WHERE scan_session_id=$sid";
        cmd.Add("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Добавить файлы в область сессии (size/category берём из самих files). Идемпотентно.</summary>
    public void AddScope(long sessionId, IReadOnlyCollection<long> fileIds)
    {
        if (fileIds.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var chunk in fileIds.Chunk(400))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var names = new List<string>(chunk.Length);
            for (int i = 0; i < chunk.Length; i++) { var n = "$i" + i; names.Add(n); cmd.Add(n, chunk[i]); }
            cmd.Add("$sid", sessionId);
            cmd.CommandText =
                "INSERT OR IGNORE INTO scan_scope_files (scan_session_id, file_id, size, category) " +
                $"SELECT $sid, id, size, category FROM files WHERE id IN ({string.Join(",", names)})";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static bool HasScope(Microsoft.Data.Sqlite.SqliteConnection conn, long sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM scan_scope_files WHERE scan_session_id=$sid)";
        cmd.Add("$sid", sessionId);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>Размеры, встречающиеся более чем у одного файла — кандидаты на точные дубликаты.</summary>
    public List<long> GetDuplicateSizeCandidates(long sessionId = 0)
    {
        using var conn = _db.Open();
        bool scoped = sessionId > 0 && HasScope(conn, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = scoped
            ? "SELECT size FROM scan_scope_files WHERE scan_session_id=$sid AND size>0 GROUP BY size HAVING COUNT(*)>1"
            : "SELECT size FROM files WHERE size > 0 GROUP BY size HAVING COUNT(*) > 1";
        if (scoped) cmd.Add("$sid", sessionId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.Long(0));
        return list;
    }

    /// <summary>Лёгкий неблокирующий чекпоинт WAL — движок зовёт между пачками размеров.</summary>
    public void PassiveCheckpoint() => _db.PassiveCheckpoint();

    /// <summary>Сколько всего файлов имеют размер, встречающийся более одного раза (для прогресса).</summary>
    public long CountFilesWithDuplicateSizes(long sessionId = 0)
    {
        using var conn = _db.Open();
        bool scoped = sessionId > 0 && HasScope(conn, sessionId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = scoped
            ? "SELECT COUNT(*) FROM scan_scope_files WHERE scan_session_id=$sid AND size>0 AND size IN " +
              "(SELECT size FROM scan_scope_files WHERE scan_session_id=$sid AND size>0 GROUP BY size HAVING COUNT(*)>1)"
            : "SELECT COUNT(*) FROM files WHERE size > 0 AND size IN " +
              "(SELECT size FROM files WHERE size > 0 GROUP BY size HAVING COUNT(*) > 1)";
        if (scoped) cmd.Add("$sid", sessionId);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Сколько строк индекса имеют ровно такой размер (сторожевой режим, Sentinel-lite):
    /// дешёвый первичный отсев — если 0, новый файл заведомо уникален и хэшировать его не нужно.
    /// </summary>
    public long CountBySize(long size)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE size=$s";
        cmd.Add("$s", size);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<FileRecord> GetBySize(long size)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.size=$s";
        cmd.Add("$s", size);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>
    /// Файлы заданных размеров одним запросом, упорядоченные по размеру (затем id). Используется
    /// движком пачками размеров: вместо N+1 (`GetBySize` на каждый размер) — один запрос на пачку,
    /// и при этом соединение короткоживущее (закрывается до записи хэшей), поэтому read-mark НЕ
    /// удерживает WAL от чекпоинта во время долгой воронки.
    /// </summary>
    public List<FileRecord> GetBySizesOrdered(IReadOnlyList<long> sizes, long sessionId = 0)
    {
        var list = new List<FileRecord>();
        if (sizes.Count == 0) return list;

        using var conn = _db.Open();
        bool scoped = sessionId > 0 && HasScope(conn, sessionId);
        using var cmd = conn.CreateCommand();
        var names = new List<string>(sizes.Count);
        for (int i = 0; i < sizes.Count; i++)
        {
            var n = "$s" + i;
            names.Add(n);
            cmd.Add(n, sizes[i]);
        }
        // Для скана папки берём только файлы из области сессии — иначе на большой БД пришлось бы тянуть
        // все файлы этих размеров со всего тома. Корректность не страдает: downstream всё равно inScope.
        cmd.CommandText = scoped
            ? $"SELECT {Cols} FROM files f JOIN scan_scope_files s ON s.file_id=f.id AND s.scan_session_id=$sid " +
              $"WHERE f.size IN ({string.Join(",", names)}) ORDER BY f.size, f.id"
            : $"SELECT {Cols} FROM files f WHERE f.size IN ({string.Join(",", names)}) ORDER BY f.size, f.id";
        if (scoped) cmd.Add("$sid", sessionId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>Значения полного хэша, встречающиеся более чем у одного файла (кандидаты в точные дубли).</summary>
    public List<byte[]> GetSharedFullHashes()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT full_hash FROM files WHERE full_hash IS NOT NULL GROUP BY full_hash HAVING COUNT(*) > 1";
        using var r = cmd.ExecuteReader();
        var list = new List<byte[]>();
        while (r.Read()) { var b = r.Blob(0); if (b is not null) list.Add(b); }
        return list;
    }

    /// <summary>Группы байт-идентичных файлов, встречающихся более чем на одном носителе (FR-40/41).</summary>
    public List<List<FileRecord>> GetCrossVolumeDuplicates()
    {
        var hashes = new List<byte[]>();
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT full_hash FROM files WHERE full_hash IS NOT NULL " +
                "GROUP BY full_hash HAVING COUNT(DISTINCT volume_id) > 1";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var b = r.Blob(0); if (b is not null) hashes.Add(b); }
        }

        var result = new List<List<FileRecord>>();
        foreach (var h in hashes)
        {
            var files = GetByFullHash(h);
            if (files.Select(f => f.VolumeId).Distinct().Count() > 1)
                result.Add(files);
        }
        return result;
    }

    public List<FileRecord> GetByFullHash(byte[] fullHash)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.full_hash=$h";
        cmd.Add("$h", fullHash);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>
    /// Пачка файлов по НЕСКОЛЬКИМ полным хэшам одним запросом (этап 1.5): группировка делала
    /// N+1 GetByFullHash — десятки тысяч соединений/запросов на большой библиотеке превращали
    /// стадию Grouping в SQL-bound. Паттерн отработан на GetBySizesOrdered (короткоживущее
    /// соединение, порядок по full_hash — потребитель группирует соседние строки).
    /// </summary>
    public List<FileRecord> GetByFullHashes(IReadOnlyList<byte[]> hashes)
    {
        var list = new List<FileRecord>();
        if (hashes.Count == 0) return list;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var names = new List<string>(hashes.Count);
        for (int i = 0; i < hashes.Count; i++)
        {
            var n = "$h" + i;
            names.Add(n);
            cmd.Add(n, hashes[i]);
        }
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.full_hash IN ({string.Join(",", names)}) ORDER BY f.full_hash, f.id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>
    /// Этап 2.3: перцептивный признак (phash/pixel_hash или audio_fp) переносится SQL-запросом
    /// с любого файла с тем же full_hash, у которого он уже посчитан — байт-идентичные копии не
    /// декодируются повторно. Коллизия XxHash128 в худшем случае даёт неточную «похожесть», не
    /// удаление (его защищают побайтовая сверка и ревалидация). Возвращает число заполненных строк.
    /// </summary>
    public int PropagatePerceptualByFullHash(FileCategory category)
    {
        using var conn = _db.Open();
        int filled = 0;

        if (category == FileCategory.Image)
        {
            // pHash версионируется (этап 2.2): переносим ТОЛЬКО значения текущей версии и ставим
            // получателю ту же версию — иначе старый (v1) pHash «заражал» бы новые строки и они
            // выпадали бы из пересчёта, смешивая версии в одной группировке. Получатели — строки
            // без pHash или со старой версией (им всё равно предстоял пересчёт, экономим декод).
            using (var cmd = conn.CreateCommand())
            {
                // 3.7: резкость переносится вместе с pHash — байт-идентичное содержимое даёт ту же
                // резкость (детерминированная функция от байтов). Без этого получатель pHash навсегда
                // остался бы с sharpness NULL: он больше не «ждёт перцептива» и не пересчитывается.
                // COALESCE: уже посчитанную резкость не затираем NULL-ом донора без резкости.
                cmd.CommandText = """
                    UPDATE files SET phash = (
                        SELECT f2.phash FROM files f2
                        WHERE f2.full_hash = files.full_hash AND f2.phash IS NOT NULL
                          AND f2.phash_ver = $ver AND f2.id <> files.id
                        LIMIT 1), phash_ver = $ver,
                        sharpness = COALESCE(sharpness, (
                            SELECT f4.sharpness FROM files f4
                            WHERE f4.full_hash = files.full_hash AND f4.sharpness IS NOT NULL
                              AND f4.id <> files.id
                            LIMIT 1))
                    WHERE (phash IS NULL OR phash_ver < $ver) AND full_hash IS NOT NULL AND category = $cat
                      AND EXISTS (SELECT 1 FROM files f3
                                  WHERE f3.full_hash = files.full_hash AND f3.phash IS NOT NULL
                                    AND f3.phash_ver = $ver AND f3.id <> files.id)
                    """;
                cmd.Add("$ver", Imaging.ImageMatchService.CurrentPHashVersion);
                cmd.Add("$cat", Mappers.CategoryToDb(category));
                filled += cmd.ExecuteNonQuery();
            }
        }

        // Имена колонок — из фиксированного списка (не пользовательский ввод).
        string[] cols = category == FileCategory.Image ? new[] { "pixel_hash" } : new[] { "audio_fp" };
        foreach (var col in cols)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                UPDATE files SET {col} = (
                    SELECT f2.{col} FROM files f2
                    WHERE f2.full_hash = files.full_hash AND f2.{col} IS NOT NULL AND f2.id <> files.id
                    LIMIT 1)
                WHERE {col} IS NULL AND full_hash IS NOT NULL AND category = $cat
                  AND EXISTS (SELECT 1 FROM files f3
                              WHERE f3.full_hash = files.full_hash AND f3.{col} IS NOT NULL AND f3.id <> files.id)
                """;
            cmd.Add("$cat", Mappers.CategoryToDb(category));
            filled += cmd.ExecuteNonQuery();
        }
        return filled;
    }

    /// <summary>
    /// SQL-условие «перцептивный признак ещё нужен» для категории. Для изображений учитывает
    /// версию алгоритма pHash: строки со старой версией пересчитываются (этап 2.2), см.
    /// ImageMatchService.CurrentPHashVersion. Параметр $phv добавляет <see cref="AddPerceptualParams"/>.
    /// </summary>
    private static string NeedingPerceptualCond(FileCategory category) =>
        category == FileCategory.Image
            ? "(f.phash IS NULL OR f.phash_ver < $phv)"
            : "f.audio_fp IS NULL";

    private static void AddPerceptualParams(SqliteCommand cmd, FileCategory category)
    {
        cmd.Add("$c", Mappers.CategoryToDb(category));
        if (category == FileCategory.Image)
            cmd.Add("$phv", Imaging.ImageMatchService.CurrentPHashVersion);
    }

    /// <summary>Файлы заданной категории, для которых ещё не посчитан перцептивный признак.</summary>
    public List<FileRecord> GetNeedingPerceptual(FileCategory category)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM files f WHERE f.category=$c AND {NeedingPerceptualCond(category)}";
        AddPerceptualParams(cmd, category);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>
    /// Сколько файлов категории ещё ждут перцептивного признака (для прогресса). При scope-скане
    /// (<paramref name="sessionId"/>&gt;0 и есть область) считаем только файлы области — иначе на большой
    /// старой БД счёт шёл бы по всему тому (P1 scope-aware perceptual). Пустая область → глобальный счёт.
    /// </summary>
    public long CountNeedingPerceptual(FileCategory category, long sessionId = 0)
    {
        using var conn = _db.Open();
        bool scoped = sessionId > 0 && HasScope(conn, sessionId);
        using var cmd = conn.CreateCommand();
        var cond = NeedingPerceptualCond(category);
        cmd.CommandText = scoped
            ? $"SELECT COUNT(*) FROM files f JOIN scan_scope_files s ON s.file_id=f.id AND s.scan_session_id=$sid " +
              $"WHERE f.category=$c AND {cond}"
            : $"SELECT COUNT(*) FROM files f WHERE f.category=$c AND {cond}";
        AddPerceptualParams(cmd, category);
        if (scoped) cmd.Add("$sid", sessionId);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Страница файлов категории без перцептивного признака (keyset-пагинация по id). Позволяет
    /// считать перцептив большими библиотеками, не загружая все изображения/треки в память (раздел 6.1).
    /// Возвращает записи с id &gt; <paramref name="afterId"/>, упорядоченные по id; пустой список — конец.
    /// </summary>
    public List<FileRecord> GetNeedingPerceptualPage(FileCategory category, long afterId, int limit, long sessionId = 0)
    {
        using var conn = _db.Open();
        bool scoped = sessionId > 0 && HasScope(conn, sessionId);
        using var cmd = conn.CreateCommand();
        var cond = NeedingPerceptualCond(category);
        // При scope-скане джойним область сессии (file_id, session_id), иначе тянули бы всю библиотеку тома.
        cmd.CommandText = scoped
            ? $"SELECT {Cols} FROM files f JOIN scan_scope_files s ON s.file_id=f.id AND s.scan_session_id=$sid " +
              $"WHERE f.category=$c AND {cond} AND f.id > $after ORDER BY f.id LIMIT $lim"
            : $"SELECT {Cols} FROM files f WHERE f.category=$c AND {cond} AND f.id > $after " +
              "ORDER BY f.id LIMIT $lim";
        AddPerceptualParams(cmd, category);
        cmd.Add("$after", afterId);
        cmd.Add("$lim", limit);
        if (scoped) cmd.Add("$sid", sessionId);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public List<FileRecord> GetByCategoryWithPerceptual(FileCategory category, long sessionId = 0)
    {
        using var conn = _db.Open();
        bool scoped = sessionId > 0 && HasScope(conn, sessionId);
        using var cmd = conn.CreateCommand();
        var col = category == FileCategory.Image ? "phash" : "audio_fp";
        // Scope-aware: группируем похожие только среди файлов области сессии (не по всей старой БД).
        cmd.CommandText = scoped
            ? $"SELECT {Cols} FROM files f JOIN scan_scope_files s ON s.file_id=f.id AND s.scan_session_id=$sid " +
              $"WHERE f.category=$c AND f.{col} IS NOT NULL"
            : $"SELECT {Cols} FROM files f WHERE f.category=$c AND f.{col} IS NOT NULL";
        cmd.Add("$c", Mappers.CategoryToDb(category));
        if (scoped) cmd.Add("$sid", sessionId);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>Привязать к файлам метку/точку монтирования их томов (для отображения).</summary>
    public void AttachVolumeInfo(IEnumerable<FileRecord> files, IReadOnlyDictionary<long, VolumeRecord> volumes)
    {
        foreach (var f in files)
        {
            if (volumes.TryGetValue(f.VolumeId, out var v))
            {
                f.VolumeLabel = v.DisplayName;
                f.VolumeMountPath = v.IsConnected ? v.MountPath : null;
            }
        }
    }

    // ---- Запись ----

    /// <summary>
    /// Вставка/обновление метаданных пачкой в одной транзакции; проставляет Id в записи.
    /// Хэши берутся из записи (для новых/изменившихся — null).
    /// 2.5: verified_full_hash при этом сбрасывается ВСЕГДА (INSERT даёт NULL по умолчанию,
    /// DO UPDATE пишет NULL явно): в ScanService.Enumerate строка попадает в UpsertMetaBatch
    /// ИМЕННО когда строгое реиспользование метаданных (size+mtime+created) не сработало —
    /// файл новый, изменён или скан неинкрементальный, — и прежняя побайтовая сверка больше
    /// ничего не доказывает. VerifiedFullHash из самой записи сознательно игнорируется.
    /// </summary>
    public void UpsertMetaBatch(IReadOnlyList<FileRecord> batch)
    {
        if (batch.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 3.7: sharpness пишется из записи, как и хэши, — у нового/изменившегося файла она null,
        // поэтому DO UPDATE сбрасывает устаревшую резкость вместе с pHash (пересчитается заодно с ним).
        cmd.CommandText = """
            INSERT INTO files (volume_id, rel_path, name, ext, size, mtime, created, category,
                               partial_hash, full_hash, phash, pixel_hash, audio_fp, scan_state, last_indexed,
                               partial_ver, phash_ver, sharpness)
            VALUES ($vol, $rel, $name, $ext, $size, $mtime, $created, $cat,
                    $ph, $fh, $phash, $pix, $afp, $state, $idx, $pver, $phver, $sharp)
            ON CONFLICT(volume_id, rel_path) DO UPDATE SET
                name=excluded.name, ext=excluded.ext, size=excluded.size, mtime=excluded.mtime,
                created=excluded.created, category=excluded.category,
                partial_hash=excluded.partial_hash, full_hash=excluded.full_hash,
                phash=excluded.phash, pixel_hash=excluded.pixel_hash, audio_fp=excluded.audio_fp,
                scan_state=excluded.scan_state, last_indexed=excluded.last_indexed,
                partial_ver=excluded.partial_ver, phash_ver=excluded.phash_ver,
                sharpness=excluded.sharpness,
                verified_full_hash=NULL
            RETURNING id;
            """;
        var pVol = cmd.CreateParameter(); pVol.ParameterName = "$vol"; cmd.Parameters.Add(pVol);
        var pRel = cmd.CreateParameter(); pRel.ParameterName = "$rel"; cmd.Parameters.Add(pRel);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pExt = cmd.CreateParameter(); pExt.ParameterName = "$ext"; cmd.Parameters.Add(pExt);
        var pSize = cmd.CreateParameter(); pSize.ParameterName = "$size"; cmd.Parameters.Add(pSize);
        var pMtime = cmd.CreateParameter(); pMtime.ParameterName = "$mtime"; cmd.Parameters.Add(pMtime);
        var pCreated = cmd.CreateParameter(); pCreated.ParameterName = "$created"; cmd.Parameters.Add(pCreated);
        var pCat = cmd.CreateParameter(); pCat.ParameterName = "$cat"; cmd.Parameters.Add(pCat);
        var pPh = cmd.CreateParameter(); pPh.ParameterName = "$ph"; cmd.Parameters.Add(pPh);
        var pFh = cmd.CreateParameter(); pFh.ParameterName = "$fh"; cmd.Parameters.Add(pFh);
        var pPhash = cmd.CreateParameter(); pPhash.ParameterName = "$phash"; cmd.Parameters.Add(pPhash);
        var pPix = cmd.CreateParameter(); pPix.ParameterName = "$pix"; cmd.Parameters.Add(pPix);
        var pAfp = cmd.CreateParameter(); pAfp.ParameterName = "$afp"; cmd.Parameters.Add(pAfp);
        var pState = cmd.CreateParameter(); pState.ParameterName = "$state"; cmd.Parameters.Add(pState);
        var pIdx = cmd.CreateParameter(); pIdx.ParameterName = "$idx"; cmd.Parameters.Add(pIdx);
        var pVer = cmd.CreateParameter(); pVer.ParameterName = "$pver"; cmd.Parameters.Add(pVer);
        var pPhVer = cmd.CreateParameter(); pPhVer.ParameterName = "$phver"; cmd.Parameters.Add(pPhVer);
        var pSharp = cmd.CreateParameter(); pSharp.ParameterName = "$sharp"; cmd.Parameters.Add(pSharp);

        foreach (var f in batch)
        {
            pVol.Value = f.VolumeId;
            pRel.Value = f.RelPath;
            pName.Value = f.Name;
            pExt.Value = (object?)f.Ext ?? DBNull.Value;
            pSize.Value = f.Size;
            pMtime.Value = Db.Iso(f.Mtime);
            pCreated.Value = (object?)Db.IsoOrNull(f.Created == default ? null : f.Created) ?? DBNull.Value;
            pCat.Value = Mappers.CategoryToDb(f.Category);
            pPh.Value = (object?)f.PartialHash ?? DBNull.Value;
            pFh.Value = (object?)f.FullHash ?? DBNull.Value;
            pPhash.Value = (object?)f.PHash ?? DBNull.Value;
            pPix.Value = (object?)f.PixelHash ?? DBNull.Value;
            pAfp.Value = (object?)f.AudioFp ?? DBNull.Value;
            pState.Value = Mappers.StateToDb(f.ScanState);
            pIdx.Value = (object?)Db.IsoOrNull(f.LastIndexed) ?? DBNull.Value;
            pVer.Value = f.PartialVer;
            pPhVer.Value = f.PHashVer;
            pSharp.Value = (object?)f.Sharpness ?? DBNull.Value;
            f.Id = (long)cmd.ExecuteScalar()!;
        }
        tx.Commit();
    }

    /// <summary>Обновить вычисленные хэши/признаки пачкой.
    /// 2.5: verified_full_hash сохраняется ТОЛЬКО пока байтово равен записываемому full_hash;
    /// если движок пересчитал full_hash (или обнулил его), старая побайтовая сверка относится
    /// к другому содержимому — кэш пишется как NULL.</summary>
    public void UpdateComputedBatch(IReadOnlyList<FileRecord> batch)
    {
        if (batch.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE files SET partial_hash=$ph, full_hash=$fh, phash=$phash, pixel_hash=$pix,
                             audio_fp=$afp, scan_state=$state, last_indexed=$idx, partial_ver=$pver,
                             phash_ver=$phver, verified_full_hash=$vfh, sharpness=$sharp
            WHERE id=$id;
            """;
        var pPh = cmd.CreateParameter(); pPh.ParameterName = "$ph"; cmd.Parameters.Add(pPh);
        var pFh = cmd.CreateParameter(); pFh.ParameterName = "$fh"; cmd.Parameters.Add(pFh);
        var pPhash = cmd.CreateParameter(); pPhash.ParameterName = "$phash"; cmd.Parameters.Add(pPhash);
        var pPix = cmd.CreateParameter(); pPix.ParameterName = "$pix"; cmd.Parameters.Add(pPix);
        var pAfp = cmd.CreateParameter(); pAfp.ParameterName = "$afp"; cmd.Parameters.Add(pAfp);
        var pState = cmd.CreateParameter(); pState.ParameterName = "$state"; cmd.Parameters.Add(pState);
        var pIdx = cmd.CreateParameter(); pIdx.ParameterName = "$idx"; cmd.Parameters.Add(pIdx);
        var pVer2 = cmd.CreateParameter(); pVer2.ParameterName = "$pver"; cmd.Parameters.Add(pVer2);
        var pPhVer2 = cmd.CreateParameter(); pPhVer2.ParameterName = "$phver"; cmd.Parameters.Add(pPhVer2);
        var pVfh = cmd.CreateParameter(); pVfh.ParameterName = "$vfh"; cmd.Parameters.Add(pVfh);
        var pSharp2 = cmd.CreateParameter(); pSharp2.ParameterName = "$sharp"; cmd.Parameters.Add(pSharp2);
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);

        foreach (var f in batch)
        {
            // Инвариант кэша сверки: в БД verified_full_hash либо NULL, либо байтово равен full_hash.
            bool verifiedStillValid = f.FullHash is not null && f.VerifiedFullHash is not null &&
                                      f.VerifiedFullHash.AsSpan().SequenceEqual(f.FullHash);
            pPh.Value = (object?)f.PartialHash ?? DBNull.Value;
            pFh.Value = (object?)f.FullHash ?? DBNull.Value;
            pPhash.Value = (object?)f.PHash ?? DBNull.Value;
            pPix.Value = (object?)f.PixelHash ?? DBNull.Value;
            pAfp.Value = (object?)f.AudioFp ?? DBNull.Value;
            pState.Value = Mappers.StateToDb(f.ScanState);
            pIdx.Value = (object?)Db.IsoOrNull(f.LastIndexed) ?? DBNull.Value;
            pVer2.Value = f.PartialVer;
            pPhVer2.Value = f.PHashVer;
            pVfh.Value = verifiedStillValid ? f.VerifiedFullHash! : (object)DBNull.Value;
            pSharp2.Value = (object?)f.Sharpness ?? DBNull.Value;
            pId.Value = f.Id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 2.5: записать кэш побайтовой сверки пачкой (после успешной кластеризации в
    /// DuplicateGrouper.ByteVerifyClusters). Страховка от гонки с перехэшированием: значение
    /// проставляется ТОЛЬКО строкам, у которых full_hash в БД байтово равен записываемому —
    /// инвариант «verified_full_hash равен full_hash или NULL» не нарушается никогда.
    /// </summary>
    public void UpdateVerifiedFullHashBatch(IReadOnlyList<(long FileId, byte[] Hash)> items)
    {
        if (items.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE files SET verified_full_hash=$h WHERE id=$id AND full_hash=$h";
        var pH = cmd.CreateParameter(); pH.ParameterName = "$h"; cmd.Parameters.Add(pH);
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        foreach (var (fileId, hash) in items)
        {
            pH.Value = hash;
            pId.Value = fileId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE id=$id";
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Удалить строку по (volume_id, rel_path) — дельта-реконсайл USN (3.1): файл удалён или уехал
    /// (rename). Сравнение rel_path байтовое, как и UNIQUE-индекс files (см. GetExistingMetaByPaths):
    /// несовпадение регистра оставит устаревшую строку, которую уберёт ближайший полный скан с
    /// прунингом, а preflight удаления (P0.3) не даст по ней действовать. 0 удалённых строк — норма
    /// (файл появился и исчез между сканами, в индексе его не было).
    /// </summary>
    public int DeleteByVolumeAndPath(long volumeId, string relPath)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE volume_id=$v AND rel_path=$p";
        cmd.Add("$v", volumeId);
        cmd.Add("$p", relPath);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Удалить из БД записи тома, не встреченные при последнем сканировании.</summary>
    public int PruneVolume(long volumeId, IReadOnlyCollection<long> keptIds)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _seen(id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
            create.CommandText = "DELETE FROM _seen";
            create.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO _seen(id) VALUES($id)";
            var p = ins.CreateParameter(); p.ParameterName = "$id"; ins.Parameters.Add(p);
            foreach (var id in keptIds) { p.Value = id; ins.ExecuteNonQuery(); }
        }
        int removed;
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM files WHERE volume_id=$v AND id NOT IN (SELECT id FROM _seen)";
            del.Add("$v", volumeId);
            removed = del.ExecuteNonQuery();
        }
        tx.Commit();
        return removed;
    }

    // ---- Статистика ----

    public (long files, long bytes) TotalIndexed()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size),0) FROM files";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.Long(0), r.Long(1));
    }

    public (Dictionary<FileCategory, long> byCount, Dictionary<FileCategory, long> byBytes) CategoryBreakdown()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category, COUNT(*), COALESCE(SUM(size),0) FROM files GROUP BY category";
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<FileCategory, long>();
        var bytes = new Dictionary<FileCategory, long>();
        while (r.Read())
        {
            var cat = Mappers.ParseCategory(r.Str(0));
            counts[cat] = counts.GetValueOrDefault(cat) + r.Long(1);
            bytes[cat] = bytes.GetValueOrDefault(cat) + r.Long(2);
        }
        return (counts, bytes);
    }

    internal static FileRecord Map(SqliteDataReader r) => new()
    {
        Id = r.Long(0),
        VolumeId = r.Long(1),
        RelPath = r.Str(2) ?? string.Empty,
        Name = r.Str(3) ?? string.Empty,
        Ext = r.Str(4),
        Size = r.Long(5),
        Mtime = Db.ParseDate(r.Str(6)),
        Created = Db.ParseDate(r.Str(7)),
        Category = Mappers.ParseCategory(r.Str(8)),
        PartialHash = r.Blob(9),
        FullHash = r.Blob(10),
        PHash = r.Blob(11),
        PixelHash = r.Blob(12),
        AudioFp = r.Blob(13),
        ScanState = Mappers.ParseState(r.Str(14)),
        LastIndexed = Db.ParseDateOrNull(r.Str(15)),
        PartialVer = (int)r.Long(16),
        PHashVer = (int)r.Long(17),
        VerifiedFullHash = r.Blob(18),
        Sharpness = r.DblN(19)
    };
}

/// <summary>Преобразования enum &lt;-&gt; строковые значения БД (как в ТЗ).</summary>
internal static class Mappers
{
    public static string CategoryToDb(FileCategory c) => c switch
    {
        FileCategory.Image => "image",
        FileCategory.Audio => "audio",
        FileCategory.Video => "video",
        FileCategory.Document => "document",
        _ => "other"
    };

    public static FileCategory ParseCategory(string? s) => s switch
    {
        "image" => FileCategory.Image,
        "audio" => FileCategory.Audio,
        "video" => FileCategory.Video,
        "document" => FileCategory.Document,
        _ => FileCategory.Other
    };

    public static string StateToDb(ScanState s) => s switch
    {
        ScanState.Partial => "partial",
        ScanState.Full => "full",
        ScanState.Perceptual => "perceptual",
        _ => "meta"
    };

    public static ScanState ParseState(string? s) => s switch
    {
        "partial" => ScanState.Partial,
        "full" => ScanState.Full,
        "perceptual" => ScanState.Perceptual,
        _ => ScanState.Meta
    };

    public static string MatchTypeToDb(MatchType m) => m switch
    {
        MatchType.SimilarImage => "similar_image",
        MatchType.SimilarAudio => "similar_audio",
        MatchType.ExactVideo => "exact_video",
        _ => "exact"
    };

    public static MatchType ParseMatchType(string? s) => s switch
    {
        "similar_image" => MatchType.SimilarImage,
        "similar_audio" => MatchType.SimilarAudio,
        "exact_video" => MatchType.ExactVideo,
        _ => MatchType.Exact
    };

    public static string OpTypeToDb(OperationType o) => o switch
    {
        OperationType.DeleteToRecycle => "delete_to_recycle",
        OperationType.MoveToQuarantine => "move_to_quarantine",
        OperationType.Hardlink => "hardlink",
        OperationType.Restore => "restore",
        _ => "delete_to_recycle"
    };

    public static OperationType ParseOpType(string? s) => s switch
    {
        "move_to_quarantine" => OperationType.MoveToQuarantine,
        "hardlink" => OperationType.Hardlink,
        "restore" => OperationType.Restore,
        _ => OperationType.DeleteToRecycle
    };
}
