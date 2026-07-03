using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Data;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

/// <summary>Файл-член кросс-дисковой группы.</summary>
public sealed class CrossDiskFileItem
{
    public required string Name { get; init; }
    public required string RelPath { get; init; }
    public required string VolumeName { get; init; }
    public bool VolumeConnected { get; init; }
    public long Size { get; init; }
    public string SizeText => Format.Bytes(Size);
    public string StatusText => VolumeConnected ? Strings.Connected : Strings.Disconnected;
}

/// <summary>Группа одинакового содержимого, встречающегося на нескольких носителях.</summary>
public sealed class CrossDiskGroupItem
{
    public required List<CrossDiskFileItem> Files { get; init; }
    public string Title => Files.Count > 0 ? Files[0].Name : "—";
    public string SizeText => Format.Bytes(Files.Count > 0 ? Files[0].Size : 0);
    public int VolumeCount => Files.Select(f => f.VolumeName).Distinct().Count();
    public string Summary =>
        $"{Format.Count(VolumeCount, "диск", "диска", "дисков")} · " +
        $"{Format.Count(Files.Count, "копия", "копии", "копий")}";
}

/// <summary>Экран «Память дисков»: дубликаты между носителями из кэша, с живым статусом подключения.</summary>
public partial class CrossDiskViewModel : PageViewModel
{
    private readonly FileRepository _files;
    private readonly IVolumeService _volumes;
    private readonly IDriveWatcher _watcher;
    private readonly NavigationService _nav;
    private bool _loading;
    private bool _active;
    private bool _reloadPending;

    public override string Title => Strings.CrossDiskTitle;

    public ObservableCollection<CrossDiskGroupItem> Groups { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _empty;
    [ObservableProperty] private string _summary = "";

    public CrossDiskViewModel(FileRepository files, IVolumeService volumes, IDriveWatcher watcher, NavigationService nav)
    {
        _files = files;
        _volumes = volumes;
        _watcher = watcher;
        _nav = nav;
        // Реальное время: при подключении/извлечении носителя обновляем статусы.
        _watcher.DrivesChanged += () => Dispatcher.UIThread.Post(OnDrivesChanged);
    }

    private void OnDrivesChanged()
    {
        if (!_active) return;            // #12: не гоняем WMI/БД в фоне, когда экран не открыт
        if (_loading) { _reloadPending = true; return; } // #11: не теряем событие, поступившее во время загрузки
        _ = LoadAsync();
    }

    public override void OnNavigatedTo() { _active = true; _ = LoadAsync(); }
    public override void OnNavigatedFrom() => _active = false;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        IsBusy = true;
        try
        {
            var items = await Task.Run(() =>
            {
                var catalog = _volumes.SyncAndGetCatalog().ToDictionary(v => v.Id);
                var raw = _files.GetCrossVolumeDuplicates();
                return raw.Select(files => new CrossDiskGroupItem
                {
                    Files = files.Select(f =>
                    {
                        catalog.TryGetValue(f.VolumeId, out var v);
                        return new CrossDiskFileItem
                        {
                            Name = f.Name,
                            RelPath = f.RelPath,
                            VolumeName = v?.DisplayName ?? $"Диск №{f.VolumeId}",
                            VolumeConnected = v?.IsConnected ?? false,
                            Size = f.Size
                        };
                    }).OrderBy(f => f.VolumeName).ToList()
                })
                .OrderByDescending(g => g.Files.Count)
                .ToList();
            });

            Groups.Clear();
            foreach (var g in items) Groups.Add(g);
            Summary = items.Count == 0 ? "" :
                $"{Format.Count(items.Count, "группа", "группы", "групп")} · " +
                $"{Format.Count(items.Sum(g => g.Files.Count), "файл", "файла", "файлов")}";
            Empty = items.Count == 0 ? Strings.CrossDiskNothing : null;
        }
        finally
        {
            IsBusy = false;
            _loading = false;
            // #11: событие, пришедшее во время загрузки, обрабатываем сразу после неё.
            if (_active && _reloadPending) { _reloadPending = false; _ = LoadAsync(); }
        }
    }

    [RelayCommand]
    private void OpenScan() => _nav.To<ScanViewModel>();
}
