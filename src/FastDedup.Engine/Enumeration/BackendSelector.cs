using System.IO;

namespace FastDedup.Engine;

/// <summary>Какой бэкенд перечисления выбран.</summary>
public enum EnumerationBackend
{
    /// <summary>Турбо-режим: прямое чтение MFT/USN (только NTFS под админом, целый том).</summary>
    NtfsMft,
    /// <summary>Совместимый быстрый режим: FindFirstFileExW. Работает на любой локальной ФС.</summary>
    Win32Find,
    /// <summary>Совместимый режим для сетевых/UNC путей (скорость зависит от сети).</summary>
    Win32FindNetwork
}

/// <summary>Почему НЕ выбран турбо MFT-режим (для честного сообщения пользователю).</summary>
public enum BackendFallbackReason
{
    None,
    NonWindows,
    NonNtfs,
    NotElevated,
    NotWholeVolume,
    NetworkPath,
    VolumeNotReady,
    AccessDenied,
    UnsupportedUsnRecord,
    ApiError
}

public sealed record BackendDecision(
    string Root,
    string? VolumeRoot,
    string? FileSystem,
    DriveType? DriveType,
    bool IsElevated,
    bool IsWholeVolume,
    EnumerationBackend Backend,
    BackendFallbackReason Reason,
    string? TechnicalDetails);

/// <summary>
/// Чистый выбор бэкенда перечисления по матрице (раздел «Backend decision matrix»). MFT — ТОЛЬКО для
/// целого тома NTFS под админом; всё остальное (exFAT/FAT32/ReFS/сеть/папка/без админа) — совместимый
/// быстрый режим Win32Find. Главное: для НЕ-NTFS мы НЕ предлагаем запуск от администратора (это не
/// создаст MFT на exFAT/FAT32). Функция без побочных эффектов — полностью тестируема.
/// </summary>
public static class BackendSelector
{
    public static BackendDecision Decide(string root, string? volumeRoot, string? fileSystem,
        DriveType? driveType, bool isWindows, bool isElevated, bool isWholeVolume)
    {
        BackendDecision Mk(EnumerationBackend b, BackendFallbackReason r) =>
            new(root, volumeRoot, fileSystem, driveType, isElevated, isWholeVolume, b, r, null);

        if (!isWindows)
            return Mk(EnumerationBackend.Win32Find, BackendFallbackReason.NonWindows);

        // Сеть/UNC — отдельный совместимый режим (скорость зависит от сети).
        if (driveType == System.IO.DriveType.Network || IsUncPath(root))
            return Mk(EnumerationBackend.Win32FindNetwork, BackendFallbackReason.NetworkPath);

        bool ntfs = string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
        if (!ntfs)
            return Mk(EnumerationBackend.Win32Find, BackendFallbackReason.NonNtfs);   // exFAT/FAT32/ReFS/…

        if (!isWholeVolume)
            return Mk(EnumerationBackend.Win32Find, BackendFallbackReason.NotWholeVolume); // папка → быстрый обход папки

        if (!isElevated)
            return Mk(EnumerationBackend.Win32Find, BackendFallbackReason.NotElevated);     // NTFS, но без админа

        return Mk(EnumerationBackend.NtfsMft, BackendFallbackReason.None);                  // NTFS + админ + целый том
    }

    public static bool IsUncPath(string path) =>
        !string.IsNullOrEmpty(path) && (path.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>Является ли путь корнем тома (например, «C:\»), а не вложенной папкой.</summary>
    public static bool IsWholeVolumeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || IsUncPath(root)) return false;
        try
        {
            var full = Path.GetFullPath(root).TrimEnd('\\', '/');
            var volumeRoot = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
            return !string.IsNullOrEmpty(volumeRoot) &&
                   string.Equals(full, volumeRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>Человеко-понятные сообщения о бэкенде. Для НЕ-NTFS НИКОГДА не упоминаем админа.</summary>
public static class BackendMessages
{
    public static string BuildUserNotice(BackendDecision d) => d.Reason switch
    {
        BackendFallbackReason.None when d.Backend == EnumerationBackend.NtfsMft =>
            $"{Vol(d)} — турбо-режим NTFS/MFT включён.",

        BackendFallbackReason.NonNtfs =>
            $"{Vol(d)} — файловая система {d.FileSystem ?? "неизвестна"}. " +
            "MFT-режим доступен только для NTFS. Используется совместимый быстрый режим.",

        BackendFallbackReason.NotElevated =>
            $"{Vol(d)} — NTFS. Используется совместимый быстрый режим. " +
            "Для турбо-режима (MFT) запустите программу от имени администратора.",

        BackendFallbackReason.NotWholeVolume =>
            $"{d.Root} — выбрана папка, используется быстрый обход выбранной папки.",

        BackendFallbackReason.NetworkPath =>
            "Сетевой путь: используется совместимый режим, скорость зависит от сети.",

        BackendFallbackReason.AccessDenied =>
            $"{Vol(d)} — MFT недоступен: {d.TechnicalDetails}. Используется совместимый быстрый режим.",

        _ => $"{Vol(d)} — используется совместимый быстрый режим."
    };

    private static string Vol(BackendDecision d) => d.VolumeRoot ?? d.Root;
}
