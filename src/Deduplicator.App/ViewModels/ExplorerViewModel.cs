using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.App.ViewModels;

/// <summary>
/// Проводник носителя (экран 2). Слева — дерево навигации (живое для подключённого диска,
/// слепок из базы для отключённого, FR-39); справа — содержимое выбранной папки в трёх видах
/// (ночь-2): крупные значки, список с сортировкой, столбцы (как в Finder). Для подключённых
/// томов доступны копирование и безопасный перенос на другой диск (копия → проверка → удаление
/// источника только при переносе), с подтверждением, прогрессом и отменой.
/// </summary>
public partial class ExplorerViewModel : PageViewModel
{
    private readonly IVolumeService _volumes;
    private readonly FileRepository _files;
    private readonly ISettingsService _settings;
    private readonly IFileTransferService _transfer;

    public override string Title => Strings.ExplorerTitle;

    public ObservableCollection<VolumeItemViewModel> Volumes { get; } = new();
    public ObservableCollection<ExplorerNode> Root { get; } = new();

    [ObservableProperty] private VolumeItemViewModel? _selectedVolume;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _sourceHint;

    private long? _pendingSelectId;
    private int _treeLoadVersion;

    public ExplorerViewModel(IVolumeService volumes, FileRepository files,
        ISettingsService settings, IFileTransferService transfer)
    {
        _volumes = volumes;
        _files = files;
        _settings = settings;
        _transfer = transfer;
        _viewMode = NormalizeViewMode(settings.Current.ExplorerViewMode);
    }

    public void Select(long volumeId) => _pendingSelectId = volumeId;

    public override void OnNavigatedTo() => _ = LoadVolumesAsync();

    public override void OnNavigatedFrom()
    {
        // Незапущенный диалог подтверждения при уходе со страницы закрываем; идущий перенос
        // не трогаем — он продолжится, и по возвращении прогресс останется на месте.
        ShowTransferConfirm = false;
    }

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

    partial void OnSelectedVolumeChanged(VolumeItemViewModel? value)
    {
        OnPropertyChanged(nameof(OperationsHint));
        NotifyOps();
        _ = LoadTreeAsync(value);
    }

    private async Task LoadTreeAsync(VolumeItemViewModel? volume)
    {
        // Версия загрузки: при быстром переключении дисков «опоздавший» результат
        // не должен смешивать узлы и не должен гасить индикатор актуальной загрузки.
        int version = ++_treeLoadVersion;

        Root.Clear();
        ResetRightPanel(volume);
        if (volume is null) { Status = Strings.SelectVolume; SourceHint = null; IsBusy = false; return; }

        IsBusy = true;
        Status = Strings.Loading;

        if (volume.IsConnected && volume.MountPath is not null)
        {
            // Живое содержимое диска — доступно всегда, даже без сканирования.
            SourceHint = Strings.ExplorerLive;
            var mount = volume.MountPath;
            // IsConnected — снапшот на момент загрузки каталога: диск могли выдернуть только что.
            // Тогда честно говорим «отключён», а не показываем загадочно пустой список (ночь-1).
            var (rootNodes, mountGone) = await Task.Run(() =>
            {
                var nodes = EnumerateChildren(mount);
                bool gone = false;
                if (nodes.Count == 0)
                    try { gone = !Directory.Exists(LongPath.Fix(mount)); } catch { gone = true; }
                return (nodes, gone);
            });
            if (version != _treeLoadVersion) return; // нас обогнала более новая загрузка
            Root.Clear();
            foreach (var node in rootNodes) Root.Add(node);
            Status = mountGone
                ? "Диск отключён. Всё в порядке: сохранённый список файлов появится после обновления списка дисков."
                : rootNodes.Count == 0 ? Strings.Nothing : null;
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
        await RefreshRightPanelAsync();
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
                    AbsolutePath = Path.Combine(dir, sub.Name),
                    Modified = new DateTimeOffset(sub.LastWriteTimeUtc, TimeSpan.Zero)
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

    // ==================================================================================
    //  Правая панель: крошки, три вида, выделение (ночь-2)
    // ==================================================================================

    /// <summary>Вид правой панели: "icons" | "list" | "columns". Сохраняется в настройках.</summary>
    [ObservableProperty] private string _viewMode;

    public bool IsIconsMode => ViewMode == "icons";
    public bool IsListMode => ViewMode == "list";
    public bool IsColumnsMode => ViewMode == "columns";

    public ObservableCollection<ExplorerCrumb> Crumbs { get; } = new();
    public ObservableCollection<ExplorerNode> Entries { get; } = new();
    public ObservableCollection<ExplorerColumnViewModel> Columns { get; } = new();

    [ObservableProperty] private ExplorerNode? _previewFile;
    [ObservableProperty] private string? _rightStatus;
    [ObservableProperty] private bool _isRightBusy;

    // Сортировка вида «список» (применяется и к «значкам»): name | size | date.
    [ObservableProperty] private string _sortField = "name";
    [ObservableProperty] private bool _sortAsc = true;

    public string NameHeader => Strings.ColumnName + SortMark("name");
    public string SizeHeader => Strings.ColumnSize + SortMark("size");
    public string DateHeader => Strings.ColumnModified + SortMark("date");
    private string SortMark(string field) => SortField != field ? "" : SortAsc ? " ↑" : " ↓";

    private int _rightVersion;
    private bool _columnsDirty;   // столбцы не пересчитываются, пока вид «столбцы» не открыт
    private bool _entriesDirty;   // и наоборот: навигация в столбцах не грузит список
    private bool _suppressColumnEvents;

    private static string NormalizeViewMode(string? mode) =>
        mode is "icons" or "columns" ? mode : "list";

    partial void OnViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsIconsMode));
        OnPropertyChanged(nameof(IsListMode));
        OnPropertyChanged(nameof(IsColumnsMode));

        // Запоминаем выбор пользователя между запусками.
        var s = _settings.Current;
        if (s.ExplorerViewMode != value) { s.ExplorerViewMode = value; _settings.Save(s); }

        // При смене вида выделение начинается заново (в разных видах разные механики выбора).
        ClearSelection();
        PreviewFile = null;

        if (IsColumnsMode && _columnsDirty) _ = RebuildColumnsAsync();
        if (!IsColumnsMode && _entriesDirty) _ = LoadRightAsync();
    }

    [RelayCommand]
    private void SetViewMode(string mode) => ViewMode = NormalizeViewMode(mode);

    [RelayCommand]
    private void SortBy(string field)
    {
        if (SortField == field) SortAsc = !SortAsc;
        else { SortField = field; SortAsc = true; }
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(SizeHeader));
        OnPropertyChanged(nameof(DateHeader));
        ResortEntries();
    }

    /// <summary>Полный сброс правой панели на корень тома (вызывается при смене/перезагрузке тома).</summary>
    private void ResetRightPanel(VolumeItemViewModel? volume)
    {
        Crumbs.Clear();
        Entries.Clear();
        Columns.Clear();
        PreviewFile = null;
        RightStatus = null;
        OperationError = null;
        SuccessText = null;
        ShowRescanHint = false;
        ClearSelection();
        _columnsDirty = true;
        _entriesDirty = true;
        if (volume is null) return;

        Crumbs.Add(volume.IsConnected && volume.MountPath is not null
            ? new ExplorerCrumb(volume.DisplayName, volume.MountPath, null)
            : new ExplorerCrumb(volume.DisplayName, null, null));
    }

    /// <summary>Перечитать содержимое текущей папки (и столбцы, если открыт вид «столбцы»).</summary>
    private async Task RefreshRightPanelAsync()
    {
        if (IsColumnsMode)
        {
            _entriesDirty = true;
            await RebuildColumnsAsync();
        }
        else
        {
            _columnsDirty = true;
            await LoadRightAsync();
        }
    }

    /// <summary>Дети папки-крошки: с диска для живого тома, из памяти для слепка.</summary>
    private async Task<List<ExplorerNode>> ChildrenOfAsync(ExplorerCrumb crumb)
    {
        if (crumb.LivePath is { } path)
            return await Task.Run(() => EnumerateChildren(path));
        var source = crumb.SnapNode?.Children ?? Root;
        // Слепок уже отсортирован «папки, затем имя»; пересортировка — по текущему полю.
        return source.Where(n => n.Name != "…").ToList();
    }

    private async Task LoadRightAsync()
    {
        _entriesDirty = false;
        int v = ++_rightVersion;
        var crumb = Crumbs.LastOrDefault();
        if (crumb is null) { Entries.Clear(); RightStatus = null; return; }

        IsRightBusy = true;
        var children = await ChildrenOfAsync(crumb);
        if (v != _rightVersion) return;

        SortEntries(children);
        Entries.Clear();
        foreach (var c in children) Entries.Add(c);
        RightStatus = Entries.Count == 0 ? Strings.ExplorerEmptyFolder : null;
        ClearSelection();
        IsRightBusy = false;
    }

    private void ResortEntries()
    {
        var list = Entries.ToList();
        SortEntries(list);
        Entries.Clear();
        foreach (var c in list) Entries.Add(c);
    }

    private void SortEntries(List<ExplorerNode> items)
    {
        Comparison<ExplorerNode> byField = SortField switch
        {
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "date" => (a, b) => a.Modified.CompareTo(b.Modified),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        };
        items.Sort((a, b) =>
        {
            // Папки всегда сверху, независимо от направления сортировки.
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int r = byField(a, b);
            if (r == 0) r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return SortAsc ? r : -r;
        });
    }

    // ---- Навигация ----

    /// <summary>Двойной клик по элементу: папка — провалиться, файл — открыть в системном приложении.</summary>
    public void OpenEntry(ExplorerNode node)
    {
        if (node.IsFolder) NavigateInto(node);
        else OpenFileInShell(node);
    }

    private void NavigateInto(ExplorerNode folder)
    {
        var parent = Crumbs.LastOrDefault();
        if (parent is null) return;
        Crumbs.Add(MakeChildCrumb(parent, folder));
        PreviewFile = null;
        if (IsColumnsMode) { _entriesDirty = true; _ = AppendColumnAsync(Crumbs[^1]); }
        else { _columnsDirty = true; _ = LoadRightAsync(); }
    }

    private static ExplorerCrumb MakeChildCrumb(ExplorerCrumb parent, ExplorerNode folder) =>
        parent.LivePath is { } p
            ? new ExplorerCrumb(folder.Name, folder.AbsolutePath ?? Path.Combine(p, folder.Name), null)
            : new ExplorerCrumb(folder.Name, null, folder);

    [RelayCommand]
    private void NavigateToCrumb(ExplorerCrumb crumb)
    {
        int idx = Crumbs.IndexOf(crumb);
        if (idx < 0) return;
        while (Crumbs.Count > idx + 1) Crumbs.RemoveAt(Crumbs.Count - 1);
        PreviewFile = null;
        if (IsColumnsMode)
        {
            _entriesDirty = true;
            _suppressColumnEvents = true;
            while (Columns.Count > idx + 1) Columns.RemoveAt(Columns.Count - 1);
            if (Columns.Count > 0) Columns[^1].Selected = null;
            _suppressColumnEvents = false;
            ClearSelection();
        }
        else
        {
            _columnsDirty = true;
            _ = LoadRightAsync();
        }
    }

    /// <summary>Клик по папке в дереве слева — открыть её в правой панели.</summary>
    public void NavigateToTreeNode(ExplorerNode node)
    {
        if (!node.IsFolder || Crumbs.Count == 0) return;
        var root = Crumbs[0];
        var chain = new List<ExplorerCrumb> { root };

        if (root.LivePath is { } mount && node.AbsolutePath is { } abs)
        {
            string rel;
            try { rel = Path.GetRelativePath(mount, abs); }
            catch { return; }
            if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return;
            var acc = mount;
            foreach (var seg in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                acc = Path.Combine(acc, seg);
                chain.Add(new ExplorerCrumb(seg, acc, null));
            }
        }
        else if (root.LivePath is null)
        {
            // Слепок: идём по сегментам RelPath от корня.
            IEnumerable<ExplorerNode> pool = Root;
            foreach (var seg in node.RelPath.Split('/', '\\').Where(s => s.Length > 0))
            {
                var cur = pool.FirstOrDefault(c => c.IsFolder &&
                    c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
                if (cur is null) return;
                chain.Add(new ExplorerCrumb(cur.Name, null, cur));
                pool = cur.Children;
            }
        }
        else return;

        Crumbs.Clear();
        foreach (var c in chain) Crumbs.Add(c);
        PreviewFile = null;
        _ = RefreshRightPanelAsync();
    }

    private void OpenFileInShell(ExplorerNode node)
    {
        if (node.AbsolutePath is null) return; // слепок отключённого диска — открывать нечего
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = node.AbsolutePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ToastRequested?.Invoke("Не удалось открыть файл", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenPreview()
    {
        if (PreviewFile is { } f) OpenFileInShell(f);
    }

    // ---- Столбцы (Miller) ----

    private async Task RebuildColumnsAsync()
    {
        _columnsDirty = false;
        int v = ++_rightVersion;
        IsRightBusy = true;
        _suppressColumnEvents = true;
        Columns.Clear();

        var crumbs = Crumbs.ToList();
        var built = new List<ExplorerColumnViewModel>();
        foreach (var crumb in crumbs)
        {
            var col = new ExplorerColumnViewModel(crumb) { SelectionCallback = OnColumnSelected };
            var children = await ChildrenOfAsync(crumb);
            if (v != _rightVersion) { _suppressColumnEvents = false; return; }
            SortEntries(children);
            foreach (var c in children) col.Items.Add(c);
            built.Add(col);
        }

        foreach (var col in built) Columns.Add(col);

        // Отразить текущий путь: в колонке i выбрана папка, соответствующая крошке i+1.
        for (int i = 0; i + 1 < Columns.Count && i + 1 < crumbs.Count; i++)
        {
            var next = crumbs[i + 1];
            Columns[i].Selected = Columns[i].Items.FirstOrDefault(n => n.IsFolder && NodeMatchesCrumb(n, next));
        }
        _suppressColumnEvents = false;
        RightStatus = Columns.Count == 1 && Columns[0].Items.Count == 0 ? Strings.ExplorerEmptyFolder : null;
        IsRightBusy = false;
    }

    private static bool NodeMatchesCrumb(ExplorerNode node, ExplorerCrumb crumb) =>
        crumb.SnapNode is not null
            ? ReferenceEquals(node, crumb.SnapNode)
            : node.AbsolutePath is not null &&
              string.Equals(node.AbsolutePath, crumb.LivePath, StringComparison.OrdinalIgnoreCase);

    private async Task AppendColumnAsync(ExplorerCrumb crumb)
    {
        int v = ++_rightVersion;
        IsRightBusy = true;
        var col = new ExplorerColumnViewModel(crumb) { SelectionCallback = OnColumnSelected };
        var children = await ChildrenOfAsync(crumb);
        if (v != _rightVersion) return;
        SortEntries(children);
        foreach (var c in children) col.Items.Add(c);
        Columns.Add(col);
        IsRightBusy = false;
    }

    private void OnColumnSelected(ExplorerColumnViewModel col, ExplorerNode? node)
    {
        if (_suppressColumnEvents) return;
        int idx = Columns.IndexOf(col);
        if (idx < 0 || node is null) return;

        // Всё правее этой колонки закрывается.
        _suppressColumnEvents = true;
        while (Columns.Count > idx + 1) Columns.RemoveAt(Columns.Count - 1);
        while (Crumbs.Count > idx + 1) Crumbs.RemoveAt(Crumbs.Count - 1);
        _suppressColumnEvents = false;

        if (node.IsFolder)
        {
            PreviewFile = null;
            var crumb = MakeChildCrumb(Crumbs[^1], node);
            Crumbs.Add(crumb);
            _entriesDirty = true;
            _ = AppendColumnAsync(crumb);
        }
        else
        {
            PreviewFile = node; // мини-карточка сведений в последней колонке
        }
        UpdateSelection(new[] { node });
    }

    // ---- Выделение ----

    private readonly List<ExplorerNode> _selection = new();

    [ObservableProperty] private int _selectionCount;
    [ObservableProperty] private string? _selectionText;

    /// <summary>Подсказка над кнопками операций (для отключённого диска объясняет, почему нельзя).</summary>
    public string? OperationsHint =>
        SelectedVolume is { IsConnected: false } ? Strings.ExplorerVolumeOffline : null;

    public void UpdateSelection(IEnumerable<ExplorerNode> nodes)
    {
        _selection.Clear();
        _selection.AddRange(nodes.Where(n => n is not null && n.Name != "…"));
        SelectionCount = _selection.Count;
        long bytes = _selection.Where(n => !n.IsFolder).Sum(n => n.Size);
        SelectionText = SelectionCount == 0
            ? null
            : $"Выбрано: {Format.Count(SelectionCount, "объект", "объекта", "объектов")}" +
              (bytes > 0 ? $" · {Format.Bytes(bytes)}" : "");
        NotifyOps();
    }

    private void ClearSelection() => UpdateSelection(Array.Empty<ExplorerNode>());

    private void NotifyOps()
    {
        CopySelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedCommand.NotifyCanExecuteChanged();
    }

    // ==================================================================================
    //  Операции: копирование и безопасный перенос (ночь-2)
    // ==================================================================================

    /// <summary>Выбор папки назначения — системный диалог, задаётся представлением.</summary>
    public Func<Task<string?>>? PickDestinationFolder { get; set; }

    /// <summary>Тост в правом верхнем углу — показывает представление (MainWindow).</summary>
    public event Action<string, string>? ToastRequested;

    [ObservableProperty] private bool _showTransferConfirm;
    [ObservableProperty] private string? _transferConfirmTitle;
    [ObservableProperty] private string? _transferConfirmText;
    [ObservableProperty] private string? _transferItemsPreview;
    [ObservableProperty] private bool _transferIsMove;
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private string? _transferStatus;
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private bool _cancelRequested;
    [ObservableProperty] private string? _operationError;
    [ObservableProperty] private string? _successText;
    [ObservableProperty] private bool _showRescanHint;

    public string TransferConfirmAction => TransferIsMove ? "Перенести" : "Скопировать";

    private CancellationTokenSource? _transferCts;
    private List<TransferItem>? _pendingItems;
    private string? _pendingDest;

    private bool CanOperate() =>
        SelectionCount > 0 && SelectedVolume is { IsConnected: true } && !IsTransferring;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task CopySelectedAsync() => BeginTransferAsync(move: false);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task MoveSelectedAsync() => BeginTransferAsync(move: true);

    private async Task BeginTransferAsync(bool move)
    {
        OperationError = null;
        SuccessText = null;
        ShowRescanHint = false;
        if (PickDestinationFolder is null) return;

        var selected = _selection.Where(n => n.AbsolutePath is not null).ToList();
        if (selected.Count == 0) return;

        var dest = await PickDestinationFolder();
        if (string.IsNullOrWhiteSpace(dest)) return;

        _pendingItems = selected.Select(n => new TransferItem(n.AbsolutePath!, n.IsFolder)).ToList();
        _pendingDest = dest;
        TransferIsMove = move;
        OnPropertyChanged(nameof(TransferConfirmAction));

        // Оценка объёма может занять время на больших папках — считаем вне UI-потока.
        IsRightBusy = true;
        var itemsForEstimate = _pendingItems;
        var est = await Task.Run(() => _transfer.Estimate(itemsForEstimate));
        IsRightBusy = false;

        TransferConfirmTitle = move ? "Перенос на другой диск" : "Копирование на другой диск";
        TransferConfirmText = BuildConfirmText(move, est, dest);
        TransferItemsPreview = string.Join("\n",
            selected.Take(8).Select(n => (n.IsFolder ? "[папка] " : "") + n.Name)) +
            (selected.Count > 8 ? $"\nи ещё {Format.Count(selected.Count - 8, "объект", "объекта", "объектов")}…" : "");
        ShowTransferConfirm = true;
    }

    private string BuildConfirmText(bool move, TransferEstimate est, string dest)
    {
        string what = est.Folders == 0
            ? Format.Count(est.Files, "файл", "файла", "файлов")
            : est.Files == 0
                ? Format.Count(est.Folders, "папку", "папки", "папок")
                : Format.Count(est.Files, "файл", "файла", "файлов") + " и " +
                  Format.Count(est.Folders, "папку", "папки", "папок");

        var vol = Volumes.FirstOrDefault(v => v.IsConnected && v.MountPath is not null &&
            dest.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase));
        string where = vol is null ? "" : $"на диск «{vol.DisplayName}» ";

        return $"{(move ? "Перенести" : "Скопировать")} {what} ({Format.Bytes(est.TotalBytes)}) {where}в папку {dest}?";
    }

    [RelayCommand]
    private void CancelTransferDialog()
    {
        ShowTransferConfirm = false;
        _pendingItems = null;
        _pendingDest = null;
    }

    [RelayCommand]
    private async Task ConfirmTransferAsync()
    {
        ShowTransferConfirm = false;
        var items = _pendingItems;
        var dest = _pendingDest;
        _pendingItems = null;
        _pendingDest = null;
        if (items is null || dest is null) return;

        IsTransferring = true;
        CancelRequested = false;
        TransferPercent = 0;
        TransferStatus = Strings.Loading;
        _transferCts = new CancellationTokenSource();
        NotifyOps();

        bool move = TransferIsMove;
        var progress = new Progress<TransferProgress>(p =>
        {
            TransferPercent = p.Total == 0 ? 100 : Math.Min(100.0, p.Done * 100.0 / p.Total);
            if (!CancelRequested)
                TransferStatus = $"{(move ? "Переносим" : "Копируем")} {Math.Min(p.Done + 1, Math.Max(1, p.Total))} из {Math.Max(1, p.Total)}: {p.CurrentName}";
        });

        TransferResult result;
        try
        {
            result = await _transfer.TransferAsync(items, dest, move, progress, _transferCts.Token);
        }
        catch (Exception ex)
        {
            // Сервис ошибки по элементам не бросает; сюда попадает только неожиданное.
            result = new TransferResult();
            result.Failed.Add(new TransferError("", ex.Message));
        }
        finally
        {
            IsTransferring = false;
            _transferCts.Dispose();
            _transferCts = null;
            NotifyOps();
        }

        ShowSummary(result, move, dest);
        await RefreshAfterTransferAsync();
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
        CancelRequested = true;
        TransferStatus = Strings.ExplorerCancelAfterFile;
    }

    private void ShowSummary(TransferResult result, bool move, string dest)
    {
        var parts = new List<string>();
        if (result.Moved > 0) parts.Add("перенесено " + Format.Count(result.Moved, "объект", "объекта", "объектов"));
        if (result.Copied > 0) parts.Add("скопировано " + Format.Count(result.Copied, "объект", "объекта", "объектов"));
        if (result.FilesTransferred > result.Moved + result.Copied)
            parts.Add("всего файлов: " + result.FilesTransferred);
        if (result.Skipped > 0) parts.Add("пропущено: " + result.Skipped);
        if (result.Cancelled) parts.Add("операция остановлена");

        SuccessText = parts.Count == 0 ? null : "Готово: " + string.Join(", ", parts) + ".";

        OperationError = result.Failed.Count == 0
            ? null
            : $"Не выполнено ({Format.Count(result.Failed.Count, "объект", "объекта", "объектов")}): " +
              string.Join(" · ", result.Failed.Take(3).Select(f =>
                  (string.IsNullOrEmpty(f.SourcePath) ? "" : Path.GetFileName(f.SourcePath.TrimEnd('\\', '/')) + " — ") + f.Message)) +
              (result.Failed.Count > 3 ? " …" : "");

        string title = move ? "Перенос завершён" : "Копирование завершено";
        if (result.Failed.Count > 0) title = move ? "Перенос завершён с ошибками" : "Копирование завершено с ошибками";
        if (result.Cancelled) title = "Операция остановлена";
        ToastRequested?.Invoke(title, SuccessText ?? OperationError ?? "");

        // Индекс руками не правим — это работа проверки. Только подсказываем обновить его.
        bool touched = result.Moved + result.Copied > 0 || result.FilesTransferred > 0;
        ShowRescanHint = touched && (VolumeIsIndexed(SelectedVolume) || DestVolumeIsIndexed(dest));
    }

    private static bool VolumeIsIndexed(VolumeItemViewModel? v) =>
        v is not null && (v.Model.IndexedFileCount > 0 || v.Model.LastScanned is not null);

    private bool DestVolumeIsIndexed(string dest) =>
        Volumes.Any(v => v.MountPath is not null &&
            dest.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase) && VolumeIsIndexed(v));

    private async Task RefreshAfterTransferAsync()
    {
        // Путь мог перестать существовать (перенесли папку, которую видели в крошках) —
        // откатываемся до ближайшей живой папки.
        ValidateChain();
        await RefreshRightPanelAsync();
        await ReloadTreeKeepingPathAsync();
    }

    private void ValidateChain()
    {
        while (Crumbs.Count > 1)
        {
            var last = Crumbs[^1];
            if (last.LivePath is not { } p) break; // слепок не меняется операциями
            bool exists;
            try { exists = Directory.Exists(LongPath.Fix(p)); }
            catch { exists = false; }
            if (exists) break;
            Crumbs.RemoveAt(Crumbs.Count - 1);
        }
    }

    /// <summary>Перечитать дерево слева, не трогая крошки/правую панель (после операции).</summary>
    private async Task ReloadTreeKeepingPathAsync()
    {
        var volume = SelectedVolume;
        if (volume is null || !volume.IsConnected || volume.MountPath is null) return;
        int version = ++_treeLoadVersion;
        var mount = volume.MountPath;
        var rootNodes = await Task.Run(() => EnumerateChildren(mount));
        if (version != _treeLoadVersion) return;
        Root.Clear();
        foreach (var node in rootNodes) Root.Add(node);
    }
}
