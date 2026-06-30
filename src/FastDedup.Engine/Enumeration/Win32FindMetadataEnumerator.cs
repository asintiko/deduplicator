using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>Перечисление файлов с метаданными напрямую (без второго stat на каждый файл).</summary>
public interface IMetadataEnumerator
{
    IEnumerable<EnumeratedFile> EnumerateMetadata(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct);
}

/// <summary>
/// Универсальный быстрый перечислитель Win32: <c>FindFirstFileExW</c> отдаёт размер, время и атрибуты
/// прямо в <c>WIN32_FIND_DATA</c>, поэтому НЕ нужен отдельный <c>GetFileAttributesEx</c> на каждый файл
/// (в этом было основное замедление старого fallback на exFAT/USB/сети). Ручной стек каталогов (без
/// рекурсии и переполнения), без сортировки, без аллокаций FileInfo/DirectoryInfo. Работает на
/// NTFS/exFAT/FAT32/ReFS и сетевых путях. Длинные пути — через префикс \\?\; UNC — \\?\UNC\.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32FindMetadataEnumerator : IMetadataEnumerator
{
    private const int FindExInfoBasic = 1;          // не запрашивать cAlternateFileName — быстрее
    private const int FindExSearchNameMatch = 0;
    private const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;
    private const int ERROR_NO_MORE_FILES = 18;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    public IEnumerable<EnumeratedFile> EnumerateMetadata(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct)
    {
        var stack = new Stack<string>();
        // identity (том+индекс) уже посещённых reparse-каталогов — защита от циклов джанкшенов/симлинков.
        var visitedReparse = new HashSet<(uint, ulong)>();

        // Некорректный/слишком длинный корень не должен ронять весь скан (другие корни/тома) —
        // фиксируем как ошибку этого корня и выходим пусто.
        string rootFull;
        try { rootFull = Path.GetFullPath(root); }
        catch (Exception ex) { errors.Add(new ScanError(root, "Некорректный корень: " + ex.Message)); yield break; }
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            // Префикс каталога с разделителем на конце: и для поиска (\\?\dir\*), и для сборки путей.
            string prefix = dir.EndsWith('\\') || dir.EndsWith('/') ? dir : dir + "\\";

            IntPtr h = FindFirstFileExW(Native.LongPath(prefix) + "*", FindExInfoBasic,
                out var data, FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
            if (h == INVALID_HANDLE_VALUE)
            {
                // Недоступная папка — считаем ошибку, но скан целиком не валим.
                errors.Add(new ScanError(dir, "FindFirstFileEx error " + Marshal.GetLastWin32Error()));
                continue;
            }

            try
            {
                do
                {
                    ct.ThrowIfCancellationRequested();
                    string name = data.cFileName;
                    if (name is "." or "..") continue;

                    uint attrs = data.dwFileAttributes;
                    string full = prefix + name;

                    if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        bool isReparse = (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                        if (isReparse && !options.IncludeReparsePoints) continue;
                        if (options.SkipHiddenSystem && (attrs & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) continue;
                        if (ScanFilters.ShouldSkipDirectoryName(name, options)) continue;
                        // В reparse-каталог заходим лишь ОДИН раз по его identity (том+индекс): джанкшен на
                        // предка иначе зациклил бы обход навсегда. Не опознали identity — не рискуем, пропускаем.
                        if (isReparse)
                        {
                            if (!Native.TryGetFileIdentity(full, out var rid)) continue;
                            if (!visitedReparse.Add((rid.VolumeSerialNumber, rid.FileIndex))) continue;
                        }
                        stack.Push(full);
                    }
                    else
                    {
                        long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                        if (ScanFilters.ShouldSkipMetadata(full, size, attrs, options)) continue;
                        long cr = ((long)data.ftCreationTime.dwHighDateTime << 32) | data.ftCreationTime.dwLowDateTime;
                        long lw = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime;
                        yield return new EnumeratedFile(full, size, cr, lw, attrs);
                    }
                }
                while (FindNextFileW(h, out data));

                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_NO_MORE_FILES && err != 0)
                    errors.Add(new ScanError(dir, "FindNextFile error " + err));
            }
            finally
            {
                FindClose(h);
            }
        }
    }
}
