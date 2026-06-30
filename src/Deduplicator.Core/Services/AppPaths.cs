namespace Deduplicator.Core.Services;

/// <summary>Расположение файлов приложения (БД, карантин, кэш миниатюр, настройки, лог).</summary>
public sealed class AppPaths
{
    public string RootDir { get; }
    public string DbPath { get; }
    public string QuarantineDir { get; }
    public string ThumbnailDir { get; }
    public string SettingsPath { get; }
    public string LogDir { get; }
    public string LogPath { get; }

    public AppPaths(string? rootDir = null)
    {
        RootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deduplicator");

        DbPath = Path.Combine(RootDir, "deduplicator.db");
        QuarantineDir = Path.Combine(RootDir, "Карантин");
        ThumbnailDir = Path.Combine(RootDir, "cache", "thumbnails");
        SettingsPath = Path.Combine(RootDir, "settings.json");
        // Лог в подпапке logs\ (по ТЗ MVP): чтобы пользователь/тестировщик легко его находил.
        LogDir = Path.Combine(RootDir, "logs");
        LogPath = Path.Combine(LogDir, "deduplicator.log");

        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(QuarantineDir);
        Directory.CreateDirectory(ThumbnailDir);
        Directory.CreateDirectory(LogDir);
    }
}
