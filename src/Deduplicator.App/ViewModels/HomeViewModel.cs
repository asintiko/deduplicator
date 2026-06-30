using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;

namespace Deduplicator.App.ViewModels;

public sealed record HomeDiskItem(
    string Name,
    string StatusText,
    string FilesText,
    string LastScanText,
    string CapacityText,
    bool IsConnected);

/// <summary>Простой стартовый экран для основных действий без технической навигации.</summary>
public partial class HomeViewModel : PageViewModel
{
    private readonly NavigationService _nav;
    private readonly IVolumeService _volumes;
    private readonly IStatisticsService _statistics;

    public override string Title => "Главная";

    public ObservableCollection<HomeDiskItem> RecentDisks { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusTitle = "Готово к проверке";
    [ObservableProperty] private string _statusText = "Подключите диск и запустите поиск дубликатов.";
    [ObservableProperty] private string _safetyText = "Безопасно: удаление только после подтверждения.";
    [ObservableProperty] private string _connectedDisksText = "0";
    [ObservableProperty] private string _catalogDisksText = "0";
    [ObservableProperty] private string _indexedFilesText = "0";
    [ObservableProperty] private string _duplicateGroupsText = "0";
    [ObservableProperty] private string _reclaimableText = "0 Б";
    [ObservableProperty] private string _lastSnapshotText = "Снимков пока нет";
    [ObservableProperty] private string _noDisksText = "";

    public HomeViewModel(NavigationService nav, IVolumeService volumes, IStatisticsService statistics)
    {
        _nav = nav;
        _volumes = volumes;
        _statistics = statistics;
    }

    public override void OnNavigatedTo() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var summary = await Task.Run(() =>
            {
                var catalog = _volumes.SyncAndGetCatalog();
                var stats = _statistics.Compute();
                return new
                {
                    Catalog = catalog,
                    Stats = stats,
                    Connected = catalog.Count(v => v.IsConnected),
                    LastScan = catalog
                        .Where(v => v.LastScanned is not null)
                        .Select(v => v.LastScanned!.Value)
                        .DefaultIfEmpty()
                        .Max()
                };
            });

            ConnectedDisksText = $"{summary.Connected:N0}";
            CatalogDisksText = $"{summary.Catalog.Count:N0}";
            IndexedFilesText = $"{summary.Stats.TotalIndexedFiles:N0}";
            DuplicateGroupsText = $"{summary.Stats.TotalGroups:N0}";
            ReclaimableText = Format.Bytes(summary.Stats.TotalReclaimableBytes);
            LastSnapshotText = summary.LastScan == default
                ? "Снимков пока нет"
                : $"Последний снимок: {Format.DateShort(summary.LastScan)}";

            StatusTitle = BuildStatusTitle(summary.Connected, summary.Stats);
            StatusText = BuildStatusText(summary.Connected, summary.Stats);

            RecentDisks.Clear();
            foreach (var disk in summary.Catalog
                         .OrderByDescending(v => v.IsConnected)
                         .ThenByDescending(v => v.LastScanned ?? v.LastSeen)
                         .Take(5))
            {
                RecentDisks.Add(new HomeDiskItem(
                    disk.DisplayName,
                    disk.IsConnected ? "Подключён" : "Сохранён в архиве",
                    Format.Count(disk.IndexedFileCount, "файл", "файла", "файлов"),
                    disk.LastScanned is null ? "Не сканировался" : Format.DateShort(disk.LastScanned.Value),
                    Format.Bytes(disk.TotalBytes),
                    disk.IsConnected));
            }

            NoDisksText = RecentDisks.Count == 0
                ? "Подключите первый диск, чтобы начать."
                : "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDuplicateSearch() => _nav.To<ScanViewModel>();

    [RelayCommand]
    private void OpenDuplicateResults() => _nav.To<ResultsViewModel>();

    [RelayCommand]
    private void OpenCleanup() => _nav.To<CleanupViewModel>();

    [RelayCommand]
    private void OpenDiskHealth() => _nav.To<DiskHealthViewModel>();

    [RelayCommand]
    private void OpenDiskArchive() => _nav.To<VolumesViewModel>();

    [RelayCommand]
    private void OpenSavedFiles() => _nav.To<ExplorerViewModel>();

    [RelayCommand]
    private void OpenCrossDisk() => _nav.To<CrossDiskViewModel>();

    [RelayCommand]
    private void OpenCleanArchive() => _nav.To<ConsolidationViewModel>();

    [RelayCommand]
    private void OpenStatistics() => _nav.To<StatisticsViewModel>();

    [RelayCommand]
    private void OpenJournal() => _nav.To<JournalViewModel>();

    private static string BuildStatusTitle(int connected, DuplicateStatistics stats)
    {
        if (connected == 0) return "Жду подключённый диск";
        if (stats.TotalGroups > 0) return "Дубликаты уже найдены";
        if (stats.TotalIndexedFiles > 0) return "Архив дисков сохранён";
        return "Можно начинать проверку";
    }

    private static string BuildStatusText(int connected, DuplicateStatistics stats)
    {
        if (connected == 0)
            return "Подключите внешний диск или хранилище. Старые снимки файлов останутся доступны в архиве.";

        if (stats.TotalGroups > 0)
            return $"Найдено групп дублей: {stats.TotalGroups:N0}. Можно открыть результаты и выбрать, что убрать.";

        if (stats.TotalIndexedFiles > 0)
            return "Файлы сохранены в каталоге. Можно искать дубликаты между подключёнными и уже знакомыми дисками.";

        return "Выберите поиск дубликатов. Программа запомнит файлы, чтобы диск было видно даже после отключения.";
    }
}
