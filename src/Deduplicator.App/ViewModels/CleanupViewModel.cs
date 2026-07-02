using System;
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
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Очистка диска от мусора (новый экран).</summary>
public partial class CleanupViewModel : PageViewModel
{
    private readonly IJunkScanService _junk;
    private readonly IVolumeService _volumes;
    private readonly IFileOpsService _fileOps;
    private CancellationTokenSource? _cts;

    public override string Title => Strings.CleanupTitle;

    public ObservableCollection<VolumeCheckItem> Roots { get; } = new();
    public ObservableCollection<JunkItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _emptyFolders = true;
    [ObservableProperty] private bool _zeroByte = true;
    [ObservableProperty] private bool _temp = true;
    [ObservableProperty] private bool _broken = true;
    [ObservableProperty] private bool _large;
    [ObservableProperty] private int _largeThresholdMb = 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isScanning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showDeleteConfirm;
    [ObservableProperty] private string _deleteConfirmText = "";

    /// <summary>Идёт любая длительная операция (скан или удаление) — для блокировки кнопок.</summary>
    public bool Busy => IsScanning || IsBusy;

    public CleanupViewModel(IJunkScanService junk, IVolumeService volumes, IFileOpsService fileOps)
    {
        _junk = junk;
        _volumes = volumes;
        _fileOps = fileOps;
        // Доступность «Удалить» зависит от наличия выделенных элементов.
        Items.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (JunkItemViewModel i in e.OldItems) i.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems is not null)
            foreach (JunkItemViewModel i in e.NewItems) i.PropertyChanged += OnItemPropertyChanged;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JunkItemViewModel.IsSelected))
        {
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            UpdateSelectedSummary();
        }
    }

    public override void OnNavigatedTo()
    {
        if (!Busy) _ = LoadRootsAsync();
    }

    private async Task LoadRootsAsync()
    {
        var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
        var connected = catalog.Where(v => v.IsConnected).ToList();
        Roots.Clear();
        foreach (var v in connected)
            Roots.Add(new VolumeCheckItem(v) { IsChecked = ShouldSelectByDefault(v) });
        if (Roots.Count > 0 && !Roots.Any(r => r.IsChecked))
        {
            foreach (var root in Roots) root.IsChecked = true;
        }
        Status = connected.Count == 0 ? Strings.ScanNoVolumes : null;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        var roots = Roots.Where(r => r.IsChecked && r.Model.MountPath is not null)
            .Select(r => r.Model.MountPath!).ToList();
        if (roots.Count == 0) return;

        var options = new JunkScanOptions
        {
            Roots = roots,
            EmptyFolders = EmptyFolders,
            ZeroByteFiles = ZeroByte,
            TempFiles = Temp,
            BrokenShortcuts = Broken,
            LargeFiles = Large,
            LargeThresholdBytes = (long)LargeThresholdMb * 1024 * 1024
        };

        _cts = new CancellationTokenSource();
        IsScanning = true;
        Status = Strings.Loading;
        Items.Clear();

        try
        {
            var found = await Task.Run(() => _junk.Scan(options,
                n => Dispatcher.UIThread.Post(() => Status = $"{Strings.CleanupFound}: {n}"),
                _cts.Token));

            foreach (var j in found.OrderByDescending(j => j.Size)) Items.Add(new JunkItemViewModel(j));
            UpdateSummary();
            Status = Items.Count == 0 ? Strings.CleanupNothing : null;
        }
        catch (OperationCanceledException)
        {
            Status = Strings.Cancel;
        }
        catch (Exception ex)
        {
            Status = $"{Strings.CleanupError}: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        // UI-аудит 2026-07-02 (must №4): «Большие файлы» — это ЛИЧНЫЕ данные (видео, архивы),
        // а не мусор; в массовое выделение они не попадают — только осознанная ручная отметка.
        foreach (var i in Items) i.IsSelected = i.Model.Kind != JunkKind.LargeFile;
        UpdateSelectedSummary();
    }

    [RelayCommand]
    private void ClearSelection() { foreach (var i in Items) i.IsSelected = false; UpdateSelectedSummary(); }

    [ObservableProperty] private string? _selectedSummaryText;

    /// <summary>«Выбрано: N (X ГБ)» рядом с кнопкой удаления — видно масштаб до подтверждения.</summary>
    internal void UpdateSelectedSummary()
    {
        int n = 0; long bytes = 0;
        foreach (var i in Items) if (i.IsSelected) { n++; bytes += i.Model.Size; }
        SelectedSummaryText = n == 0 ? null : $"Выбрано: {n} ({Format.Bytes(bytes)})";
    }

    // #16: удаление теперь через подтверждение (как в Результатах) — один клик не сносит всё сразу.
    private bool CanDelete() => !Busy && Items.Any(i => i.IsSelected);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        int count = Items.Count(i => i.IsSelected);
        if (count == 0) return;
        DeleteConfirmText = $"{Strings.CleanupConfirmBody}\n\n{Strings.FmSelected}: {count}";
        ShowDeleteConfirm = true;
    }

    [RelayCommand]
    private void CancelDelete() => ShowDeleteConfirm = false;

    /// <summary>Уход со страницы гасит «вооружённый» диалог — иначе он всплывает при возврате
    /// поверх обновлённого списка с устаревшим текстом (аудит 2026-07-02).</summary>
    public override void OnNavigatedFrom() => ShowDeleteConfirm = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        ShowDeleteConfirm = false;
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        try
        {
            // #15: учитываем результат — из списка убираем только реально удалённое, об остальном сообщаем.
            var failed = await Task.Run(() =>
            {
                var bad = new System.Collections.Generic.List<JunkItemViewModel>();
                foreach (var i in selected)
                    if (!_fileOps.RecyclePath(i.Path)) bad.Add(i);
                return bad;
            });

            foreach (var i in selected)
                if (!failed.Contains(i)) Items.Remove(i);

            UpdateSummary();
            Status = failed.Count > 0 ? $"{Strings.CleanupDeleteFailed}: {failed.Count}" : null;
        }
        finally
        {
            // #14: даже при исключении кнопки не остаются заблокированными навсегда.
            IsBusy = false;
        }
    }

    private void UpdateSummary()
    {
        long bytes = Items.Sum(i => i.Model.Size);
        Summary = $"{Strings.CleanupFound}: {Items.Count} · {Format.Bytes(bytes)}";
    }

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
}
