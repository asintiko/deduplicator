using System.IO.Hashing;

namespace Deduplicator.Core.Util;

/// <summary>
/// Безопасные файловые операции, устойчивые к сбоям/потере питания (NFR-2):
/// запись во временный файл с принудительным сбросом на диск и атомарным переименованием;
/// межтомовое перемещение — копирование + проверка размера + удаление источника.
/// Источник никогда не удаляется, пока копия не подтверждена.
/// </summary>
public static class SafeIo
{
    public const string TempSuffix = ".dedup-tmp";
    public const string HardLinkSuffix = ".dedup-hl";
    private const int Buffer = 1 << 20;

    /// <summary>
    /// Атомарно копирует src → dst: пишет во временный файл, сбрасывает на диск, затем
    /// атомарно переименовывает. При сбое остаётся либо старый файл, либо ничего (но не «половина»).
    /// </summary>
    public static void CopyAtomic(string src, string dst, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath.Fix(dir));

        var tmp = dst + TempSuffix;
        try
        {
            using (var input = new FileStream(LongPath.Fix(src), FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan))
            using (var output = new FileStream(LongPath.Fix(tmp), FileMode.Create, FileAccess.Write,
                       FileShare.None, Buffer, FileOptions.WriteThrough))
            {
                CopyStream(input, output, ct);
                output.Flush(flushToDisk: true);
            }

            // Публикация результата атомарным переименованием.
            File.Move(LongPath.Fix(tmp), LongPath.Fix(dst), overwrite: false);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Безопасно переносит src → dst. В пределах одного тома — атомарное переименование;
    /// между томами — копирование с проверкой и последующим удалением источника.
    /// </summary>
    public static void MoveSafe(string src, string dst, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(LongPath.Fix(dir));

        if (SameVolume(src, dst))
        {
            File.Move(LongPath.Fix(src), LongPath.Fix(dst), overwrite: false);
            return;
        }

        // Межтомовое перемещение: копируем, проверяем содержимое по хэшу, только потом удаляем
        // источник. Так даже «тихое» повреждение (тот же размер, другие байты) не приведёт к потере.
        var srcHash = HashFile(src, ct);
        CopyAtomic(src, dst, ct);
        var dstHash = HashFile(dst, ct);
        if (!srcHash.AsSpan().SequenceEqual(dstHash))
        {
            TryDelete(dst);
            throw new IOException($"Копия повреждена (хэш не совпал): {src}");
        }
        try
        {
            DeleteAllowReadOnly(src);
        }
        catch
        {
            // Источник удалить не удалось — подчищаем копию, иначе в назначении (карантине)
            // остаётся неучтённая копия-сирота, множащаяся при повторных попытках.
            TryDelete(dst);
            throw;
        }
    }

    /// <summary>
    /// Удаление файла, санкционированное пользователем: атрибут «только для чтения» не повод
    /// падать (файлы с CD/бэкапов). File.Move на одном томе переносит read-only без вопросов,
    /// а File.Delete в межтомовой ветке бросал UnauthorizedAccessException — и это ломало
    /// удаление в карантин для целого класса файлов на внешних носителях.
    /// </summary>
    private static void DeleteAllowReadOnly(string path)
    {
        var p = LongPath.Fix(path);
        try
        {
            File.Delete(p);
        }
        catch (UnauthorizedAccessException)
        {
            var attrs = File.GetAttributes(p);
            if ((attrs & FileAttributes.ReadOnly) == 0) throw; // не read-only — настоящая проблема прав
            File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
            File.Delete(p);
        }
    }

    private static byte[] HashFile(string path, CancellationToken ct)
    {
        using var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan);
        var hasher = new XxHash128();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Buffer);
        try
        {
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, n));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return hasher.GetCurrentHash();
    }

    /// <summary>Рекурсивно копирует дерево каталогов (каждый файл — атомарно).</summary>
    public static void CopyTree(string srcDir, string dstDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LongPath.Fix(dstDir));
        var di = new DirectoryInfo(LongPath.Fix(srcDir));
        foreach (var file in di.GetFiles())
        {
            ct.ThrowIfCancellationRequested();
            CopyAtomic(Path.Combine(srcDir, file.Name), Path.Combine(dstDir, file.Name), ct);
        }
        foreach (var sub in di.GetDirectories())
        {
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopyTree(Path.Combine(srcDir, sub.Name), Path.Combine(dstDir, sub.Name), ct);
        }
    }

    /// <summary>Безопасно переносит дерево каталогов: атомарно в пределах тома, иначе копия+удаление.</summary>
    public static void MoveTree(string srcDir, string dstDir, CancellationToken ct = default)
    {
        // Защита: нельзя переносить папку в саму себя или в свой подкаталог.
        var srcFull = Path.GetFullPath(srcDir).TrimEnd('\\', '/');
        var dstFull = Path.GetFullPath(dstDir).TrimEnd('\\', '/');
        if (dstFull.Equals(srcFull, StringComparison.OrdinalIgnoreCase) ||
            dstFull.StartsWith(srcFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Нельзя переместить папку внутрь самой себя");

        if (SameVolume(srcDir, dstDir))
        {
            Directory.CreateDirectory(LongPath.Fix(Path.GetDirectoryName(dstDir) ?? dstDir));
            Directory.Move(LongPath.Fix(srcDir), LongPath.Fix(dstDir));
            return;
        }
        CopyTree(srcDir, dstDir, ct);
        Directory.Delete(LongPath.Fix(srcDir), recursive: true);
    }

    public static bool SameVolume(string a, string b)
    {
        var ra = Path.GetPathRoot(Path.GetFullPath(a));
        var rb = Path.GetPathRoot(Path.GetFullPath(b));
        return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Удалить «осиротевшие» временные файлы (вызывать при старте — восстановление после сбоя).</summary>
    public static int CleanupTempFiles(string root)
    {
        int removed = 0;
        foreach (var suffix in new[] { TempSuffix, HardLinkSuffix })
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(LongPath.Fix(root), "*" + suffix,
                             SearchOption.AllDirectories))
                {
                    if (TryDelete(f)) removed++;
                }
            }
            catch { /* лучшее усилие */ }
        }
        return removed;
    }

    private static void CopyStream(Stream input, Stream output, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(Buffer);
        try
        {
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(LongPath.Fix(path))) { File.Delete(LongPath.Fix(path)); return true; }
        }
        catch { }
        return false;
    }
}
