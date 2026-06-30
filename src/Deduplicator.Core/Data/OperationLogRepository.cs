using Microsoft.Data.Sqlite;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Журнал операций — основа обратимости (FR-32).</summary>
public sealed class OperationLogRepository
{
    private readonly Database _db;
    public OperationLogRepository(Database db) => _db = db;

    public long Insert(OperationLogEntry e)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO operations_log
                (op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name)
            VALUES ($op, $fid, $gid, $src, $dst, $ts, $rev, $restored, $size, $name);
            SELECT last_insert_rowid();
            """;
        cmd.Add("$op", Mappers.OpTypeToDb(e.OpType));
        cmd.Add("$fid", e.FileId);
        cmd.Add("$gid", e.GroupId);
        cmd.Add("$src", e.SrcPath);
        cmd.Add("$dst", e.DstPath);
        cmd.Add("$ts", Db.Iso(e.PerformedAt == default ? DateTimeOffset.Now : e.PerformedAt));
        cmd.Add("$rev", e.Reversible ? 1 : 0);
        cmd.Add("$restored", e.Restored ? 1 : 0);
        cmd.Add("$size", e.SizeBytes);
        cmd.Add("$name", e.FileName);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<OperationLogEntry> GetAll(bool onlyReversible = false)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name " +
            "FROM operations_log " +
            (onlyReversible ? "WHERE reversible=1 AND restored=0 " : "") +
            "ORDER BY performed_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<OperationLogEntry>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public OperationLogEntry? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, op_type, file_id, group_id, src_path, dst_path, performed_at, reversible, restored, size_bytes, file_name " +
            "FROM operations_log WHERE id=$id";
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public void MarkRestored(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE operations_log SET restored=1 WHERE id=$id";
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Удалить запись журнала (откат упреждающей записи, если операция не состоялась).</summary>
    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM operations_log WHERE id=$id";
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static OperationLogEntry Map(SqliteDataReader r) => new()
    {
        Id = r.Long(0),
        OpType = Mappers.ParseOpType(r.Str(1)),
        FileId = r.LongN(2),
        GroupId = r.LongN(3),
        SrcPath = r.Str(4),
        DstPath = r.Str(5),
        PerformedAt = Db.ParseDate(r.Str(6)),
        Reversible = r.Bool(7),
        Restored = r.Bool(8),
        SizeBytes = r.LongN(9),
        FileName = r.Str(10)
    };
}
