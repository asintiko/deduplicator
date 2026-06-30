using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

public sealed record ThemeOption(string Value, string Label);

/// <summary>Настройки приложения (экран 10).</summary>
public partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IAudioMatchService _audio;
    private readonly IUpdateService _update;
    private readonly AppPaths _paths;
    private string? _releaseUrl;

    public override string Title => Strings.SettingsTitle;

    public ObservableCollection<RuleOption> Rules { get; } = new();
    public ObservableCollection<ThemeOption> Themes { get; } = new();

    /// <summary>Устанавливается представлением — выбор папки карантина.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Устанавливается представлением — копирование текста в буфер обмена.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [ObservableProperty] private string _diagnosticsStatus = "";

    [ObservableProperty] private double _imageThreshold;
    [ObservableProperty] private double _audioThreshold;
    [ObservableProperty] private bool _useRecycleBin;
    [ObservableProperty] private int _maxParallelism;
    [ObservableProperty] private bool _computePixelHash;
    [ObservableProperty] private RuleOption? _selectedRule;
    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private string? _quarantinePath;
    [ObservableProperty] private string? _fpcalcPath;
    [ObservableProperty] private string _fpcalcStatus = "";
    [ObservableProperty] private bool _fpcalcAvailable;
    [ObservableProperty] private bool _saved;
    [ObservableProperty] private bool _rotationInvariant;
    [ObservableProperty] private int _minFileSizeMb;
    [ObservableProperty] private bool _skipHiddenSystem;
    [ObservableProperty] private string _excludedExtensions = "";
    [ObservableProperty] private bool _checkUpdatesEnabled;
    [ObservableProperty] private string _updateRepository = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _canOpenRelease;
    [ObservableProperty] private string _currentVersionText = "";

    public ObservableCollection<string> ReferenceFolders { get; } = new();

    public string ImagePercentText => Format.Percent(ImageThreshold);
    public string AudioPercentText => Format.Percent(AudioThreshold);

    public SettingsViewModel(ISettingsService settings, IAudioMatchService audio, IUpdateService update, AppPaths paths)
    {
        _settings = settings;
        _audio = audio;
        _update = update;
        _paths = paths;
        CurrentVersionText = $"{Strings.CurrentVersionLabel}: {update.CurrentVersion}";

        Rules.Add(new RuleOption(RepresentativeRule.Oldest, Strings.RuleOldest));
        Rules.Add(new RuleOption(RepresentativeRule.Newest, Strings.RuleNewest));
        Rules.Add(new RuleOption(RepresentativeRule.ShortestPath, Strings.RuleShortestPath));
        Rules.Add(new RuleOption(RepresentativeRule.LongestName, Strings.RuleLongestName));

        Themes.Add(new ThemeOption("Default", Strings.ThemeSystem));
        Themes.Add(new ThemeOption("Light", Strings.ThemeLight));
        Themes.Add(new ThemeOption("Dark", Strings.ThemeDark));

        LoadFromSettings();
    }

    public override void OnNavigatedTo()
    {
        LoadFromSettings();
        DiagnosticsText = BuildDiagnostics();
        Saved = false;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        ImageThreshold = s.DefaultImageThreshold;
        AudioThreshold = s.DefaultAudioThreshold;
        UseRecycleBin = s.UseRecycleBin;
        MaxParallelism = s.MaxParallelism;
        ComputePixelHash = s.ComputePixelHash;
        SelectedRule = Rules.FirstOrDefault(r => r.Value == s.DefaultRepresentativeRule) ?? Rules[0];
        SelectedTheme = Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Themes[0];
        QuarantinePath = s.QuarantinePath;
        FpcalcPath = s.FpcalcPath;
        FpcalcAvailable = _audio.IsAvailable;
        FpcalcStatus = _audio.IsAvailable ? Strings.FpcalcAvailable : Strings.FpcalcMissing;
        RotationInvariant = s.RotationInvariantImages;
        MinFileSizeMb = s.MinFileSizeMb;
        SkipHiddenSystem = s.SkipHiddenSystem;
        ExcludedExtensions = s.ExcludedExtensions;
        ReferenceFolders.Clear();
        foreach (var f in s.ReferenceFolders) ReferenceFolders.Add(f);
        CheckUpdatesEnabled = s.CheckUpdatesEnabled;
        UpdateRepository = s.UpdateRepository;
    }

    /// <summary>Текст диагностики, показываемый на экране настроек (P2.2).</summary>
    [ObservableProperty] private string _diagnosticsText = "";

    private string BuildDiagnostics() =>
        $"Deduplicator {_update.CurrentVersion}\n" +
        $"{Deduplicator.Core.Engine.EngineBuildInfo.Summary}\n" +
        $"IsAdmin={FastDedup.Engine.EngineEnvironment.IsElevated()} OS={Environment.OSVersion}\n" +
        $"LogDir={_paths.LogDir}\n" +
        $"DbPath={_paths.DbPath}";

    /// <summary>Собрать диагностику (версия/режим/права/пути) и скопировать в буфер — для поддержки (ТЗ MVP).</summary>
    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        var text = BuildDiagnostics();
        DiagnosticsText = text;
        try { if (CopyToClipboard is not null) await CopyToClipboard(text); } catch { /* буфер недоступен */ }
        DiagnosticsStatus = Strings.DiagnosticsCopied;
    }

    /// <summary>Открыть папку логов в проводнике (P2.2) — ускоряет диагностику обращений в поддержку.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_paths.LogDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = _paths.LogDir, UseShellExecute = true });
        }
        catch { /* не критично */ }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        CanOpenRelease = false;
        if (string.IsNullOrWhiteSpace(UpdateRepository) || !UpdateRepository.Contains('/'))
        {
            UpdateStatus = Strings.UpdateError;
            return;
        }
        UpdateStatus = Strings.UpdateChecking;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await _update.CheckAsync(UpdateRepository, cts.Token);
            if (info is null) { UpdateStatus = Strings.UpdateError; return; }
            if (info.IsNewer)
            {
                UpdateStatus = $"{Strings.UpdateAvailable}: {info.LatestVersion}";
                _releaseUrl = info.Url;
                CanOpenRelease = !string.IsNullOrEmpty(info.Url);
            }
            else
            {
                UpdateStatus = Strings.UpdateUpToDate;
            }
        }
        catch
        {
            UpdateStatus = Strings.UpdateError;
        }
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (string.IsNullOrEmpty(_releaseUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_releaseUrl) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task AddReferenceFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (string.IsNullOrEmpty(folder)) return;
        // Нормализуем путь, как при проверке защиты, и сравниваем без учёта регистра.
        try { folder = System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'); } catch { }
        if (!ReferenceFolders.Contains(folder, System.StringComparer.OrdinalIgnoreCase))
        {
            ReferenceFolders.Add(folder);
            Saved = false; // изменение списка папок-эталонов — тоже несохранённая правка
        }
    }

    [RelayCommand]
    private void RemoveReferenceFolder(string? folder)
    {
        if (folder is not null && ReferenceFolders.Remove(folder)) Saved = false;
    }

    partial void OnImageThresholdChanged(double value) => OnPropertyChanged(nameof(ImagePercentText));
    partial void OnAudioThresholdChanged(double value) => OnPropertyChanged(nameof(AudioPercentText));

    [RelayCommand]
    private async Task BrowseQuarantineAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (!string.IsNullOrEmpty(folder)) QuarantinePath = folder;
    }

    [RelayCommand]
    private void Save()
    {
        var s = new AppSettings
        {
            DefaultImageThreshold = Math.Round(ImageThreshold, 2),
            DefaultAudioThreshold = Math.Round(AudioThreshold, 2),
            UseRecycleBin = UseRecycleBin,
            MaxParallelism = Math.Max(1, MaxParallelism),
            ComputePixelHash = ComputePixelHash,
            DefaultRepresentativeRule = SelectedRule?.Value ?? RepresentativeRule.Oldest,
            Theme = SelectedTheme?.Value ?? "Default",
            QuarantinePath = QuarantinePath,
            FpcalcPath = FpcalcPath,
            RotationInvariantImages = RotationInvariant,
            MinFileSizeMb = Math.Max(0, MinFileSizeMb),
            SkipHiddenSystem = SkipHiddenSystem,
            ExcludedExtensions = ExcludedExtensions ?? "",
            ReferenceFolders = ReferenceFolders.ToList(),
            CheckUpdatesEnabled = CheckUpdatesEnabled,
            UpdateRepository = UpdateRepository ?? "",
            OnboardingCompleted = _settings.Current.OnboardingCompleted
        };
        _settings.Save(s);

        if (Application.Current is App app)
            app.ApplyTheme(s.Theme);

        // #25: путь к fpcalc применяется сразу, без перезапуска — и статус доступности обновляется.
        _audio.Reconfigure(s.FpcalcPath);
        FpcalcAvailable = _audio.IsAvailable;
        FpcalcStatus = _audio.IsAvailable ? Strings.FpcalcAvailable : Strings.FpcalcMissing;

        Saved = true; // ставим последним, чтобы обновления статусов выше не сбросили бейдж
    }

    // #26: любое изменение настройки гасит бейдж «Сохранено», чтобы он не вводил в заблуждение.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Saved && e.PropertyName is not (
            nameof(Saved) or nameof(ImagePercentText) or nameof(AudioPercentText) or
            nameof(FpcalcStatus) or nameof(FpcalcAvailable) or nameof(UpdateStatus) or
            nameof(CanOpenRelease) or nameof(CurrentVersionText)))
            Saved = false;
    }
}
