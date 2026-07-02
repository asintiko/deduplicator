using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FastDedup.Engine;

/// <summary>
/// Тонкий слой нативных вызовов Windows + утилиты для работы с путями и правами.
/// Всё, что специфично для ОС, собрано здесь, чтобы остальной код оставался чистым.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Native
{
    // --- Атрибуты файлов ---
    public const uint FILE_ATTRIBUTE_READONLY = 0x00000001;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x00000002;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x00000004;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // --- Доступ / шаринг / создание ---
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;

    // --- IOCTL / FSCTL коды ---
    public const uint FSCTL_ENUM_USN_DATA = 0x000900b3;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002d1400;

    // --- Win32 ошибки ---
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    private enum GET_FILEEX_INFO_LEVELS { GetFileExInfoStandard = 0 }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_FILE_ATTRIBUTE_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileAttributesExW(
        string lpFileName, GET_FILEEX_INFO_LEVELS fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

    /// <summary>Открыть том на чтение (нужны права администратора). Для FSCTL_ENUM_USN_DATA.</summary>
    public static SafeFileHandle OpenVolumeRead(string devicePath)
    {
        var h = CreateFileW(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile({devicePath})");
        return h;
    }

    /// <summary>Открыть том только для запроса свойств (права администратора НЕ нужны).</summary>
    public static SafeFileHandle OpenVolumeQuery(string devicePath)
        => CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>
    /// Метаданные файла одним системным вызовом: размер, атрибуты, время создания и время
    /// последней записи (raw FILETIME). Дешевле, чем открывать FileInfo. false, если файл недоступен.
    /// LastWrite нужен продукту для инкрементальности (Mtime), которого USN-записи не дают.
    /// </summary>
    public static bool TryGetFileInfo(string path, out long size, out uint attrs,
        out long creationRaw, out long lastWriteRaw)
    {
        size = 0; attrs = 0; creationRaw = 0; lastWriteRaw = 0;
        if (!GetFileAttributesExW(LongPath(path), GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard, out var d))
            return false;
        attrs = d.dwFileAttributes;
        size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
        creationRaw = ((long)d.ftCreationTime.dwHighDateTime << 32) | d.ftCreationTime.dwLowDateTime;
        lastWriteRaw = ((long)d.ftLastWriteTime.dwHighDateTime << 32) | d.ftLastWriteTime.dwLowDateTime;
        return true;
    }

    /// <summary>Старая 4-арг сигнатура — обёртка, чтобы существующие вызовы не ломались.</summary>
    public static bool TryGetFileInfo(string path, out long size, out uint attrs, out long creationRaw)
        => TryGetFileInfo(path, out size, out attrs, out creationRaw, out _);

    /// <summary>
    /// Получить физическую идентичность файла. Используется на финальном этапе,
    /// чтобы hardlink-алиасы не считались отдельными физическими копиями.
    /// </summary>
    public static bool TryGetFileIdentity(string path, out FileIdentity identity)
    {
        identity = default;
        try
        {
            using var handle = File.OpenHandle(LongPath(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.None);

            if (!GetFileInformationByHandle(handle, out var info)) return false;
            ulong fileIndex = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            if (fileIndex == 0) return false;

            identity = new FileIdentity(info.dwVolumeSerialNumber, fileIndex, info.nNumberOfLinks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Первый LCN (logical cluster number) файла через FSCTL_GET_RETRIEVAL_POINTERS — физическое
    /// начало файла на диске. Права администратора НЕ нужны: хэндл открывается с нулевым доступом
    /// (только метаданные, как у defrag-API). null — при любой ошибке, для резидентных в MFT
    /// файлов (нет экстентов) и для виртуальных LCN &lt; 0 (сжатые/разрежённые). Используется для
    /// сортировки очереди чтений на HDD, поэтому обязан деградировать молча.
    /// </summary>
    public static long? TryGetFirstLcn(string path)
    {
        SafeFileHandle? handle = null;
        IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
        try
        {
            // dwDesiredAccess = 0: запрашиваем только расположение, право чтения содержимого не нужно.
            handle = CreateFileW(LongPath(path), 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            // STARTING_VCN_INPUT_BUFFER: с какого VCN начинать — нам нужен самый первый (0).
            inBuf = Marshal.AllocHGlobal(8);
            Marshal.WriteInt64(inBuf, 0, 0L);

            // RETRIEVAL_POINTERS_BUFFER на ОДИН экстент:
            // ExtentCount(4) + выравнивание(4) + StartingVcn(8) + Extents[0].NextVcn(8) + Extents[0].Lcn(8).
            const int outSize = 32;
            outBuf = Marshal.AllocHGlobal(outSize);

            bool ok = DeviceIoControl(handle, FSCTL_GET_RETRIEVAL_POINTERS,
                inBuf, 8, outBuf, outSize, out _, IntPtr.Zero);
            // ERROR_MORE_DATA — экстентов больше, чем вместил буфер, но ПЕРВЫЙ уже записан: его и хватает.
            if (!ok && Marshal.GetLastWin32Error() != ERROR_MORE_DATA) return null;

            if (Marshal.ReadInt32(outBuf, 0) < 1) return null; // экстентов нет (резидентный файл)
            long lcn = Marshal.ReadInt64(outBuf, 24);
            return lcn < 0 ? null : lcn; // виртуальный кластер позиции головки не даёт
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
            if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            handle?.Dispose();
        }
    }

    /// <summary>
    /// Валидный размер $MFT тома (байты) через FSCTL_GET_NTFS_VOLUME_DATA — для прогноза памяти
    /// MFT-режима ДО его чтения. NTFS_VOLUME_DATA_BUFFER: MftValidDataLength — LARGE_INTEGER по
    /// смещению 56 (после 5×LARGE_INTEGER и 4×DWORD). Права администратора не нужны (хэндл с
    /// нулевым доступом, как у defrag-API). null — при любой ошибке: оценка деградирует молча,
    /// и решение о MFT принимается как раньше.
    /// </summary>
    public static long? TryGetMftValidDataLength(string drive) // "C:"
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            using var h = OpenVolumeQuery(@"\\.\" + drive);
            if (h.IsInvalid) return null;

            const int Size = 96; // sizeof(NTFS_VOLUME_DATA_BUFFER)
            buf = Marshal.AllocHGlobal(Size);
            if (!DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, buf, Size,
                    out uint got, IntPtr.Zero) || got < 64)
                return null;

            long len = Marshal.ReadInt64(buf, 56);
            return len > 0 ? len : null;
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Префикс \\?\ снимает лимит MAX_PATH (260) — иначе длинные пути молча теряются.</summary>
    public static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    /// <summary>Запущены ли мы с правами администратора (нужно для MFT-режима).</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
