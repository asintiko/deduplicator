using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Hashing;

public interface IHashService
{
    /// <summary>Частичный хэш: голова + хвост (~64 КБ с каждого края). Дешёвый фильтр.</summary>
    /// <remarks>Проверяет стабильность размера файла до и после чтения: если файл изменился
    /// между сбором метаданных и чтением, бросает <see cref="FileChangedDuringScanException"/>,
    /// чтобы старый размер не смешивался с новым содержимым.</remarks>
    byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default);

    /// <summary>Полный хэш всего содержимого файла (читается целиком).</summary>
    byte[] ComputeFullHash(string fullPath, CancellationToken ct = default);

    /// <summary>Полный хэш с проверкой стабильности размера: если файл вырос/сжался между
    /// metadata-этапом и чтением, бросает <see cref="FileChangedDuringScanException"/> —
    /// такой файл нельзя считать доказанным дублем в этом проходе.</summary>
    byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default);

    /// <summary>Финальная побайтовая сверка двух файлов (защита от коллизий хэша, FR-11).</summary>
    bool FilesAreByteEqual(string pathA, string pathB, CancellationToken ct = default);
}

/// <summary>
/// Файл изменился между сбором метаданных и чтением содержимого. Безопаснее пропустить его в
/// этом проходе и взять на следующем инкрементальном скане, чем смешать старый размер с новым хэшем.
/// </summary>
public sealed class FileChangedDuringScanException : IOException
{
    public string FilePath { get; }
    public long ExpectedSize { get; }
    public long ActualSize { get; }

    public FileChangedDuringScanException(string path, long expectedSize, long actualSize)
        : base($"Файл изменился во время сверки: ожидался размер {expectedSize}, фактический {actualSize}: {path}")
    {
        FilePath = path;
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
    }
}

/// <summary>
/// Хэширование на XxHash128 (System.IO.Hashing). Криптостойкость не требуется —
/// итоговую идентичность подтверждает побайтовая сверка.
/// </summary>
public sealed class HashService : IHashService
{
    private const int EdgeBytes = 64 * 1024;   // 64 КБ с каждого края для частичного хэша
    private const int StreamBuffer = 1 << 20;  // 1 МБ буфер чтения

    public byte[] ComputePartialHash(string fullPath, long size, CancellationToken ct = default)
    {
        // P1.3: голову/хвост читаем по СМЕЩЕНИЮ через RandomAccess (без перемотки позиции потока) —
        // меньше seek-нагрузка на HDD/USB. Порядок добавления (голова + размер + хвост) сохранён,
        // поэтому хэши совпадают со старой реализацией и дедуп не меняется.
        using var handle = OpenHandle(fullPath);
        long actual = RandomAccess.GetLength(handle);
        if (actual != size) throw new FileChangedDuringScanException(fullPath, size, actual);
        var hasher = new XxHash128();

        if (size <= EdgeBytes * 2L)
        {
            // Маленький файл — хэшируем целиком, это и есть «частичный».
            AppendWhole(handle, size, hasher, ct);
            VerifyStableSize(handle, fullPath, size);
            return hasher.GetCurrentHash();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(EdgeBytes);
        try
        {
            int headRead = ReadAtOffset(handle, buffer.AsSpan(0, EdgeBytes), 0, ct);
            hasher.Append(buffer.AsSpan(0, headRead));

            // Включаем размер в хэш, чтобы файлы разной длины не сталкивались по краям.
            Span<byte> sizeBytes = stackalloc byte[8];
            BitConverter.TryWriteBytes(sizeBytes, size);
            hasher.Append(sizeBytes);

            // Хвост — читаем по абсолютному смещению size - EdgeBytes.
            int tailRead = ReadAtOffset(handle, buffer.AsSpan(0, EdgeBytes), size - EdgeBytes, ct);
            hasher.Append(buffer.AsSpan(0, tailRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        // Размер менялся во время чтения — голова/хвост могли быть прочитаны из разных версий.
        VerifyStableSize(handle, fullPath, size);
        return hasher.GetCurrentHash();
    }

    public byte[] ComputeFullHash(string fullPath, CancellationToken ct = default)
    {
        using var fs = OpenRead(fullPath);
        var hasher = new XxHash128();
        AppendStream(fs, hasher, ct);
        return hasher.GetCurrentHash();
    }

    public byte[] ComputeFullHash(string fullPath, long expectedSize, CancellationToken ct = default)
    {
        using var fs = OpenRead(fullPath);
        VerifyStableSize(fs, fullPath, expectedSize);
        var hasher = new XxHash128();
        AppendStream(fs, hasher, ct);
        // Файл рос во время полного хэша (например, активно пишущийся лог) — не считаем доказанным.
        VerifyStableSize(fs, fullPath, expectedSize);
        return hasher.GetCurrentHash();
    }

    /// <summary>
    /// Сверяет фактическую длину открытого файла с ожидаемой. Длину берём свежим syscall через
    /// <see cref="RandomAccess.GetLength"/> по дескриптору потока (а не кэшируемый FileStream.Length),
    /// иначе внешняя дозапись не была бы видна.
    /// </summary>
    private static void VerifyStableSize(FileStream fs, string path, long expectedSize) =>
        VerifyStableSize(fs.SafeFileHandle, path, expectedSize);

    private static void VerifyStableSize(SafeFileHandle handle, string path, long expectedSize)
    {
        long actual = RandomAccess.GetLength(handle);
        if (actual != expectedSize)
            throw new FileChangedDuringScanException(path, expectedSize, actual);
    }

    /// <summary>Дескриптор только для чтения (offset-доступ через RandomAccess), без перемотки позиции.</summary>
    private static SafeFileHandle OpenHandle(string path) =>
        File.OpenHandle(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);

    /// <summary>Прочитать до buffer.Length байт по абсолютному смещению; вернуть фактически прочитанное.</summary>
    private static int ReadAtOffset(SafeFileHandle handle, Span<byte> buffer, long offset, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = RandomAccess.Read(handle, buffer.Slice(total), offset + total);
            if (n == 0) break; // EOF
            total += n;
        }
        return total;
    }

    /// <summary>Хэшировать весь файл по дескриптору (для маленьких файлов в частичном хэше).</summary>
    private static void AppendWhole(SafeFileHandle handle, long size, XxHash128 hasher, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBuffer);
        try
        {
            long offset = 0;
            while (offset < size)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, size - offset);
                int n = RandomAccess.Read(handle, buffer.AsSpan(0, want), offset);
                if (n == 0) break;
                hasher.Append(buffer.AsSpan(0, n));
                offset += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool FilesAreByteEqual(string pathA, string pathB, CancellationToken ct = default)
    {
        using var a = OpenRead(pathA);
        using var b = OpenRead(pathB);
        if (a.Length != b.Length) return false;

        var bufA = ArrayPool<byte>.Shared.Rent(StreamBuffer);
        var bufB = ArrayPool<byte>.Shared.Rent(StreamBuffer);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int nA = ReadBlock(a, bufA);
                int nB = ReadBlock(b, bufB);
                if (nA != nB) return false;
                if (nA == 0) return true;
                if (!bufA.AsSpan(0, nA).SequenceEqual(bufB.AsSpan(0, nB))) return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufA);
            ArrayPool<byte>.Shared.Return(bufB);
        }
    }

    private static FileStream OpenRead(string path) =>
        new(LongPath.Fix(path), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Разрешаем доступ к файлам, открытым другими процессами на чтение/запись.
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = StreamBuffer
        });

    private static void AppendStream(FileStream fs, XxHash128 hasher, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBuffer);
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
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadBlock(FileStream fs, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
