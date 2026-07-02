using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

public sealed record DepthOption(ScanDepth Value, string Label, bool Enabled);
public sealed record StrictnessOption(int Index, string Label);

/// <summary>Экран запуска сверки и отображения прогресса (экраны 3–4).</summary>
public partial class ScanViewModel : PageViewModel
{
    private readonly IScanService _scan;
    private readonly IVolumeService _volumes;
    private readonly ISettingsService _settings;
    private readonly IAudioMatchService _audioService;
    private readonly NavigationService _nav;

    private CancellationTokenSource? _cts;
    private long? _pendingPreselect;
    private bool _loadingVolumes;

    public override string Title => Strings.ScanTitle;
    public Func<Task<string?>>? PickFolder { get; set; }

    public ObservableCollection<VolumeCheckItem> Volumes { get; } = new();
    public ObservableCollection<DepthOption> DepthOptions { get; } = new();
    public ObservableCollection<StrictnessOption> StrictnessOptions { get; } = new();

    [ObservableProperty] private DepthOption? _selectedDepth;
    [ObservableProperty] private StrictnessOption? _selectedStrictness;

    // P0-A «Что искать»: какие категории файлов проверять. По умолчанию — все.
    [ObservableProperty] private bool _includeImages = true;
    [ObservableProperty] private bool _includeVideo = true;
    [ObservableProperty] private bool _includeAudio = true;
    [ObservableProperty] private bool _includeDocuments = true;
    [ObservableProperty] private bool _includeOther = true;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string? _failText;

    /// <summary>Подпись кнопки-переключателя выбора носителей (меняется по состоянию).</summary>
    public string SelectAllLabel =>
        Volumes.Count > 0 && Volumes.All(v => v.IsChecked) ? Strings.ScanSelectAllOff : Strings.ScanSelectAll;

    public string ScanModeHint => SelectedDepth?.Value switch
    {
        ScanDepth.WithSimilarImagesAndAudio => "Медленнее: ищет точные дубли, похожие фото и похожую музыку.",
        ScanDepth.WithSimilarImages => "Дольше: ищет точные дубли и похожие фотографии.",
        _ => "Быстро: ищет одинаковые файлы по размеру и надежному хэшу. Лучше для первого запуска."
    };

    [ObservableProperty] private string _stageTitle = "";
    [ObservableProperty] private string? _currentFile;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _filesSeenText = "0";
    [ObservableProperty] private string _filesHashedText = "0";
    [ObservableProperty] private string _skippedText = "0";
    [ObservableProperty] private string _groupsText = "0";
    [ObservableProperty] private string? _emptyHint;
    [ObservableProperty] private string? _infoText;

    public ScanViewModel(IScanService scan, IVolumeService volumes, ISettingsService settings,
        IAudioMatchService audioService, NavigationService nav)
    {
        _scan = scan;
        _volumes = volumes;
        _settings = settings;
        _audioService = audioService;
        _nav = nav;

        DepthOptions.Add(new DepthOption(ScanDepth.ExactOnly, Strings.DepthExact, true));
        DepthOptions.Add(new DepthOption(ScanDepth.WithSimilarImages, Strings.DepthImages, true));
        DepthOptions.Add(new DepthOption(ScanDepth.WithSimilarImagesAndAudio, Strings.DepthAudio, _audioService.IsAvailable));
        SelectedDepth = DepthOptions.First(o => o.Enabled);

        // P1.5: строгость поиска похожих (профиль).
        StrictnessOptions.Add(new StrictnessOption(0, Strings.StrictnessStrict));
        StrictnessOptions.Add(new StrictnessOption(1, Strings.StrictnessNormal));
        StrictnessOptions.Add(new StrictnessOption(2, Strings.StrictnessWide));
        SelectedStrictness = StrictnessOptions[1]; // Обычно
    }

    partial void OnSelectedDepthChanged(DepthOption? value) => OnPropertyChanged(nameof(ScanModeHint));

    public void Preselect(long volumeId) => _pendingPreselect = volumeId;

    public override void OnNavigatedTo()
    {
        if (!IsRunning) { LoadCategoriesFromSettings(); _ = LoadVolumesAsync(); }
    }

    /// <summary>Загрузить чекбоксы «что искать» из настроек (пустой список = все включены).</summary>
    private void LoadCategoriesFromSettings()
    {
        var saved = _settings.Current.IncludedCategories;
        if (saved is null || saved.Count == 0)
        {
            IncludeImages = IncludeVideo = IncludeAudio = IncludeDocuments = IncludeOther = true;
            return;
        }
        bool Has(FileCategory c) => saved.Contains(c.ToString());
        IncludeImages = Has(FileCategory.Image);
        IncludeVideo = Has(FileCategory.Video);
        IncludeAudio = Has(FileCategory.Audio);
        IncludeDocuments = Has(FileCategory.Document);
        IncludeOther = Has(FileCategory.Other);
    }

    /// <summary>Собрать выбранные категории (P0-A). Пустой набор означает ошибку валидации.</summary>
    private HashSet<FileCategory> BuildIncludedCategories()
    {
        var set = new HashSet<FileCategory>();
        if (IncludeImages) set.Add(FileCategory.Image);
        if (IncludeVideo) set.Add(FileCategory.Video);
        if (IncludeAudio) set.Add(FileCategory.Audio);
        if (IncludeDocuments) set.Add(FileCategory.Document);
        if (IncludeOther) set.Add(FileCategory.Other);
        return set;
    }

    /// <summary>Запомнить выбор категорий в настройках, чтобы он сохранялся между запусками.</summary>
    private void SaveCategoriesToSettings(HashSet<FileCategory> categories)
    {
        try
        {
            _settings.Current.IncludedCategories = categories.Select(c => c.ToString()).ToList();
            _settings.Save(_settings.Current);
        }
        catch { /* настройки — не критично для самого скана */ }
    }

    private async Task LoadVolumesAsync()
    {
        if (_loadingVolumes) return;
        _loadingVolumes = true;
        try
        {
            var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
            var connected = catalog.Where(v => v.IsConnected).ToList();

            foreach (var item in Volumes) item.PropertyChanged -= OnCheckChanged;
            Volumes.Clear();
            bool hasPreselect = _pendingPreselect is not null;
            foreach (var v in connected)
            {
                var item = new VolumeCheckItem(v);
                item.PropertyChanged += OnCheckChanged;
                item.IsChecked = hasPreselect ? _pendingPreselect == v.Id : ShouldSelectByDefault(v);
                Volumes.Add(item);
            }

            if (!hasPreselect && Volumes.Count > 0 && !Volumes.Any(v => v.IsChecked))
            {
                foreach (var item in Volumes) item.IsChecked = true;
            }

            _pendingPreselect = null;
            EmptyHint = connected.Count == 0 ? Strings.ScanNoVolumes : null;
            OnPropertyChanged(nameof(SelectAllLabel));
            StartCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _loadingVolumes = false;
        }
    }

    private void OnCheckChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VolumeCheckItem.IsChecked))
        {
            OnPropertyChanged(nameof(SelectAllLabel));
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Переключатель «выбрать все / снять все» носители (для сверки сразу по нескольким дискам).</summary>
    [RelayCommand]
    private async Task ChooseFolderAsync(VolumeCheckItem? item)
    {
        if (item is null || PickFolder is null) return;

        var folder = await PickFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;

        folder = NormalizeDirectory(folder);
        if (!Directory.Exists(folder))
        {
            EmptyHint = "Выбранная папка недоступна. Подключите диск или выберите другую папку.";
            return;
        }

        if (!IsPathInsideRoot(folder, item.MountPath))
        {
            EmptyHint = "Выберите папку внутри выбранного диска.";
            return;
        }

        item.ScanFolder = folder;
        item.IsChecked = true;
        EmptyHint = null;
        OnPropertyChanged(nameof(SelectAllLabel));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearFolder(VolumeCheckItem? item)
    {
        if (item is null) return;
        item.ScanFolder = null;
    }

    [RelayCommand]
    private void SelectAllVolumes()
    {
        bool allChecked = Volumes.Count > 0 && Volumes.All(v => v.IsChecked);
        foreach (var v in Volumes) v.IsChecked = !allChecked;
    }

    private bool CanStart() => !IsRunning && Volumes.Any(v => v.IsChecked);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var selected = Volumes.Where(v => v.IsChecked).ToList();
        if (selected.Count == 0) return;

        // #8: носитель мог быть извлечён после загрузки списка — не запускаем «пустую» сверку молча.
        var connectedById = (await Task.Run(() => _volumes.SyncAndGetCatalog()))
            .Where(v => v.IsConnected && !string.IsNullOrWhiteSpace(v.MountPath))
            .ToDictionary(v => v.Id);
        if (!selected.Any(v => connectedById.ContainsKey(v.Id)))
        {
            EmptyHint = Strings.ScanVolumeUnavailable;
            _ = LoadVolumesAsync();
            return;
        }

        // B6 (аудит 2026-07-02): раньше блокировался старт, только если недоступны ВСЕ выбранные тома,
        // а частично отвалившийся (выдернутая флешка) молча выпадал — скан завершался «успехом» и
        // затирал прежнюю полную сессию, включая группы по отсутствующему диску. Теперь честно
        // останавливаемся и называем недоступные тома; пользователь сам решит — переподключить
        // или снять галочку.
        var missing = selected.Where(v => !connectedById.ContainsKey(v.Id)).ToList();
        if (missing.Count > 0)
        {
            EmptyHint = "Недоступны выбранные носители: " +
                        string.Join(", ", missing.Select(m => m.DisplayName)) +
                        ". Подключите их или снимите отметку — иначе результаты будут неполными.";
            _ = LoadVolumesAsync();
            return;
        }

        var ids = new List<long>();
        var folders = new List<string>();
        foreach (var item in selected)
        {
            if (!connectedById.TryGetValue(item.Id, out var volume)) continue;

            if (string.IsNullOrWhiteSpace(item.ScanFolder))
            {
                ids.Add(item.Id);
                continue;
            }

            var folder = NormalizeDirectory(item.ScanFolder);
            if (!Directory.Exists(folder) || !IsPathInsideRoot(folder, volume.MountPath))
            {
                EmptyHint = $"Папка для диска {item.DisplayName} недоступна. Выберите папку заново или сбросьте выбор.";
                return;
            }

            folders.Add(folder);
        }

        if (ids.Count == 0 && folders.Count == 0)
        {
            EmptyHint = Strings.ScanVolumeUnavailable;
            _ = LoadVolumesAsync();
            return;
        }

        var categories = BuildIncludedCategories();
        if (categories.Count == 0)
        {
            EmptyHint = Strings.PickAtLeastOneCategory; // P0-A: нельзя сканировать «ничего»
            return;
        }
        SaveCategoriesToSettings(categories); // запомнить выбор между запусками

        IsFailed = false;
        FailText = null;
        var s = _settings.Current;
        var options = new ScanOptions
        {
            VolumeIds = ids,
            Folders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Depth = SelectedDepth?.Value ?? ScanDepth.ExactOnly,
            Parallelism = s.MaxParallelism,
            Incremental = true,
            ImageThreshold = s.DefaultImageThreshold,
            AudioThreshold = s.DefaultAudioThreshold,
            MinFileSize = (long)System.Math.Max(0, s.MinFileSizeMb) * 1024 * 1024,
            SkipHiddenSystem = s.SkipHiddenSystem,
            ExcludedExtensions = ParseExtensions(s.ExcludedExtensions),
            IncludedCategories = categories
        };

        // P1.5: профиль строгости похожих (порог) — применяется только в режимах поиска похожих.
        if (options.Depth >= ScanDepth.WithSimilarImages)
        {
            var preset = (SelectedStrictness?.Index ?? 1) switch
            {
                0 => SimilarityProfileService.Strict(),
                2 => SimilarityProfileService.Wide(),
                _ => SimilarityProfileService.Normal()
            };
            options.ImageProfile = preset;
            options.AudioProfile = preset;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsDone = false;
        IsPaused = false;
        InfoText = null;
        ProgressIndeterminate = true;
        StartCommand.NotifyCanExecuteChanged();

        _scan.ProgressChanged += OnProgress;
        try
        {
            await _scan.RunAsync(options, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Отмена до фактического старта задачи (Task.Run с ct) — не сбой.
        }
        catch (Exception ex)
        {
            // RunCore переводит сбои в состояние Failed сам, но исключение может прилететь и вне
            // его try (например, БД недоступна при создании сессии). Без catch оно уходило из
            // async-команды и роняло приложение «в рабочий стол» (аудит 2026-07-02).
            Core.Util.AppLog.Error("ScanViewModel.StartAsync", ex);
            IsFailed = true;
            FailText = ex.Message;
        }
        finally
        {
            _scan.ProgressChanged -= OnProgress;
            IsRunning = false;
            IsPaused = false;
            ProgressIndeterminate = false;
            IsDone = _scan.Progress.Stage == ScanStage.Completed;
            HasResults = IsDone;
            // #6: ошибка сверки больше не молчит — показываем понятное состояние.
            IsFailed = _scan.Progress.Stage == ScanStage.Failed;
            if (IsFailed)
                FailText = string.IsNullOrWhiteSpace(_scan.Progress.Message)
                    ? Strings.ScanFailed : _scan.Progress.Message;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnProgress(ScanProgress p) => Dispatcher.UIThread.Post(() => Apply(p));

    private void Apply(ScanProgress p)
    {
        StageTitle = p.StageTitle;
        CurrentFile = p.CurrentFile;
        FilesSeenText = $"{p.FilesSeen:N0}";
        FilesHashedText = $"{p.FilesHashed:N0}";
        SkippedText = $"{p.FilesSkipped:N0}";
        GroupsText = $"{p.GroupsFound:N0}";
        ProgressIndeterminate = p.Fraction is null && p.Stage is not (ScanStage.Completed or ScanStage.Cancelled or ScanStage.Failed);
        ProgressValue = (p.Fraction ?? 0) * 100;
        // Информационное сообщение во время скана (напр. «быстрый режим MFT недоступен»). На ошибке
        // его место занимает FailText, поэтому здесь показываем только не-ошибочные сообщения.
        if (p.Stage != ScanStage.Failed) InfoText = p.Message;
    }

    [RelayCommand]
    private void Pause()
    {
        if (!IsRunning) return;
        if (IsPaused) { _scan.Resume(); IsPaused = false; }
        else { _scan.Pause(); IsPaused = true; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ViewResults() => _nav.To<ResultsViewModel>();

    private static bool ShouldSelectByDefault(VolumeRecord volume)
    {
        if (string.IsNullOrWhiteSpace(volume.MountPath)) return true;

        try
        {
            string? systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            string? mountRoot = Path.GetPathRoot(volume.MountPath);
            if (string.IsNullOrWhiteSpace(systemRoot) || string.IsNullOrWhiteSpace(mountRoot)) return true;

            return !string.Equals(systemRoot, mountRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static System.Collections.Generic.HashSet<string> ParseExtensions(string? csv)
    {
        var set = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var raw in csv.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            var e = raw.Trim();
            if (!e.StartsWith('.')) e = "." + e;
            set.Add(e);
        }
        return set;
    }

    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathInsideRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            var fullPath = NormalizeDirectory(path);
            var fullRoot = Path.GetFullPath(root);
            var rootWithoutSlash = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootWithSlash = fullRoot.EndsWith(Path.DirectorySeparatorChar) ||
                                fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            return string.Equals(fullPath, rootWithoutSlash, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
