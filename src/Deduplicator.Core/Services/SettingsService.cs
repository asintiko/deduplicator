using System.Text.Json;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save(AppSettings settings);
    event Action<AppSettings>? Changed;
}

/// <summary>Загрузка/сохранение настроек в JSON (полностью офлайн).</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;

    public SettingsService(AppPaths paths)
    {
        _path = paths.SettingsPath;
        Current = Load();
        // Карантин по умолчанию — из AppPaths, если не задан явно.
        Current.QuarantinePath ??= paths.QuarantineDir;
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    // Валидация диапазонов на случай ручной правки settings.json.
                    loaded.DefaultImageThreshold = Math.Clamp(loaded.DefaultImageThreshold, 0.5, 1.0);
                    loaded.DefaultAudioThreshold = Math.Clamp(loaded.DefaultAudioThreshold, 0.5, 1.0);
                    if (loaded.MaxParallelism < 1) loaded.MaxParallelism = 1;
                    if (loaded.MaxParallelism > 64) loaded.MaxParallelism = 64;
                    return loaded;
                }
            }
        }
        catch { /* повреждённые настройки — используем значения по умолчанию */ }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch { /* не критично для работы */ }
        Changed?.Invoke(settings);
    }
}
