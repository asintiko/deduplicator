using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.App.ViewModels;

/// <summary>
/// Проводник носителя (экран 2). Для подключённого носителя показывает живое содержимое диска
/// (с ленивой подгрузкой папок), для отключённого — сохранённые слепки из базы (FR-39).
/// </summary>
public partial class ExplorerViewModel : PageViewModel
{
    private readonly IVolumeService _volumes;
    private readonly FileRepository _files;

    public override string Title => Strings.ExplorerTitle;

    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = new();
    public ObservableCollection<ExplorerNode> Root { get; } = new();

    [ObservableProperty] private VolumeItemViewModel? _selectedVolume;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _sourceHint;

    private long? _pendingSelectId;
    private int _treeLoadVersion;

    public ExplorerViewModel(IVolumeService volumes, FileRepository files)
    {
        _volumes = volumes;
        _files = files;
    }

    public void Select(long volumeId) => _pendingSelectId = volumeId;

    public override void OnNavigatedTo() => _ = LoadVolumesAsync();

    private async Task LoadVolumesAsync()
    {
        // Запомнить текущий выбор, чтобы не сбрасывать его на первый диск при перезаходе на экран.
        long? keepId = _pendingSelectId ?? SelectedVolume?.Id;

        var catalog = await Task.Run(() => _volumes.SyncAndGetCatalog());
        Volumes.Clear();
        foreach (var v in catalog) Volumes.Add(new VolumeItemViewModel(v));
        _pendingSelectId = null;

        var target = keepId is long id ? Volumes.FirstOrDefault(v => v.Id == id) : null;
        target ??= Volumes.FirstOrDefault(v => v.IsConnected) ?? Volumes.FirstOrDefault();

        // Всегда переустанавливаем (после Clear ссылка на прежний экземпляр уже не та) —
        // это перерисует дерево ровно один раз через OnSelectedVolumeChanged.
        if (ReferenceEquals(SelectedVolume, target))
            await LoadTreeAsync(target);
        else
            SelectedVolume = target;
    }

    partial void OnSelectedVolumeChanged(VolumeItemViewModel? value) => _ = LoadTreeAsync(value);

    private async Task LoadTreeAsync(VolumeItemViewModel? volume)
    {
        // Версия загрузки: при быстром переключении дисков «опоздавший» результат
        // не должен смешивать узлы и не должен гасить индикатор актуальной загрузки.
        int version = ++_treeLoadVersion;

        Root.Clear();
        if (volume is null) { Status = Strings.SelectVolume; SourceHint = null; IsBusy = false; return; }

        IsBusy = true;
        Status = Strings.Loading;

        if (volume.IsConnected && volume.MountPath is not null)
        {
            // Живое содержимое диска — доступно всегда, даже без сканирования.
            SourceHint = Strings.ExplorerLive;
            var mount = volume.MountPath;
            var rootNodes = await Task.Run(() => EnumerateChildren(mount));
            if (version != _treeLoadVersion) return; // нас обогнала более новая загрузка
            Root.Clear();
            foreach (var node in rootNodes) Root.Add(node);
            Status = rootNodes.Count == 0 ? Strings.Nothing : null;
        }
        else
        {
            // Отключённый носитель — из сохранённых слепков.
            SourceHint = Strings.ExplorerIndexed;
            var tree = await Task.Run(() =>
            {
                var files = _files.GetByVolume(volume.Id);
                return BuildTreeFromIndex(files);
            });
            if (version != _treeLoadVersion) return;
            Root.Clear();
            foreach (var node in tree) Root.Add(node);
            Status = tree.Count == 0 ? Strings.ExplorerNeedScan : null;
        }

        IsBusy = false;
    }

    // ---- Живой обход диска (ленивый) ----

    private async Task LoadChildrenAsync(ExplorerNode node)
    {
        if (node.AbsolutePath is null) return;
        var dir = node.AbsolutePath;
        var children = await Task.Run(() => EnumerateChildren(dir));
        foreach (var c in children) node.Children.Add(c);
    }

    private List<ExplorerNode> EnumerateChildren(string dir)
    {
        var result = new List<ExplorerNode>();
        try
        {
            var di = new DirectoryInfo(LongPath.Fix(dir));

            foreach (var sub in di.GetDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var node = new ExplorerNode
                {
                    Name = sub.Name,
                    IsFolder = true,
                    AbsolutePath = Path.Combine(dir, sub.Name)
                };
                node.EnableLazyLoading(LoadChildrenAsync);
                result.Add(node);
            }

            foreach (var fi in di.GetFiles())
            {
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                result.Add(new ExplorerNode
                {
                    Name = fi.Name,
                    IsFolder = false,
                    AbsolutePath = Path.Combine(dir, fi.Name),
                    Size = fi.Length,
                    Modified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
                    Created = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero),
                    Category = CategoryDetector.FromExt(fi.Extension)
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Нет доступа к папке — показываем пустую (NFR-1). Критические сбои не глушим.
        }

        result.Sort((a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // ---- Дерево из сохранённых слепков ----

    private static List<ExplorerNode> BuildTreeFromIndex(List<FileRecord> files)
    {
        var root = new ExplorerNode { IsFolder = true };
        var folderIndex = new Dictionary<string, ExplorerNode> { [""] = root };

        foreach (var f in files)
        {
            var parts = f.RelPath.Split('\\', '/').Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) continue;

            var parent = root;
            var pathAcc = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                pathAcc = pathAcc.Length == 0 ? parts[i] : pathAcc + "/" + parts[i];
                if (!folderIndex.TryGetValue(pathAcc, out var folder))
                {
                    folder = new ExplorerNode { Name = parts[i], IsFolder = true, RelPath = pathAcc };
                    folderIndex[pathAcc] = folder;
                    parent.Children.Add(folder);
                }
                parent = folder;
            }

            parent.Children.Add(new ExplorerNode
            {
                Name = parts[^1],
                IsFolder = false,
                Category = f.Category,
                Size = f.Size,
                Modified = f.Mtime,
                Created = f.Created,
                RelPath = f.RelPath
            });
        }

        SortRecursive(root);
        return root.Children.ToList();
    }

    private static void SortRecursive(ExplorerNode node)
    {
        var ordered = node.Children
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (var c in ordered)
        {
            node.Children.Add(c);
            if (c.IsFolder) SortRecursive(c);
        }
    }
}
