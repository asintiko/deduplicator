using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.App.ViewModels.Items;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Models;
using Deduplicator.Core.Services;
using Deduplicator.Core.Util;

namespace Deduplicator.App.ViewModels;

/// <summary>Результаты: точные дубликаты и похожие (экраны 5–6).</summary>
public partial class ResultsViewModel : PageViewModel
{
    private readonly GroupRepository _groups;
    private readonly IVolumeService _volumes;
    private readonly IImageMatchService _thumbs;
    private readonly IFileOpsService _fileOps;
    private readonly IHashService _hash;
    private readonly ISettingsService _settings;
    private readonly IBatchRenameService _rename;
    private readonly IResultDistributionService _distribute;
    private readonly FileRepository _files;
    private readonly AppPaths _paths;
    private readonly ScanSessionRepository _sessions;

    public override string Title => Strings.ResultsTitle;

    public ObservableCollection<GroupItemViewModel> ExactGroups { get; } = new();
    public ObservableCollection<GroupItemViewModel> SimilarImageGroups { get; } = new();
    public ObservableCollection<GroupItemViewModel> SimilarAudioGroups { get; } = new();

    // Загруженные (построенные) VM похожих групп — накапливаются ПОСТРАНИЧНО (только открытые страницы),
    // миниатюры декодируются лишь для них. Слайдер порога фильтрует этот загруженный набор.
    private readonly List<GroupItemViewModel> _allImage = new();
    private readonly List<GroupItemViewModel> _allAudio = new();
    // Похожие вкладки строим ЛЕНИВО (по открытию вкладки) и ПОСТРАНИЧНО из БД, а не всё в память: иначе
    // сразу декодируются сотни миниатюр (наблюдалось ~1.44 ГБ) и материализуются все группы (P1).
    // P1.1: точная вкладка тоже грузится постранично из БД.
    private int _exactTotal, _exactOffset;
    private int _imageTotal, _imageOffset;   // всего похожих фото в сессии / сколько уже загружено страницами
    private int _audioTotal, _audioOffset;
    private long? _sessionFilter;
    private static readonly MatchType[] ExactTypes = { MatchType.Exact, MatchType.ExactVideo };
    private Dictionary<long, string> _mountById = new();
    private Dictionary<long, string> _labelById = new();
    private bool _imageBuilt, _audioBuilt;
    private string[] _referenceFolders = Array.Empty<string>();
    private bool _isLoading;

    /// <summary>Сколько групп показываем за раз (первая страница + «Показать ещё») — ТЗ MVP.</summary>
    private const int PageSize = 100;
    [ObservableProperty] private bool _canShowMoreExact;
    [ObservableProperty] private bool _canShowMoreSimilarImages;
    [ObservableProperty] private bool _canShowMoreSimilarAudio;
    // Текст индикатора загрузки (по умолчанию «Загрузка…»; на ревалидации удаления — отдельное сообщение).
    [ObservableProperty] private string _busyStatus = Strings.Loading;

    /// <summary>Устанавливается представлением — выбор файла для сохранения отчёта.</summary>
    public Func<string, Task<string?>>? PickSaveFile { get; set; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _imageThreshold = 0.90;
    [ObservableProperty] private double _audioThreshold = 0.85;
    [ObservableProperty] private bool _showDeleteConfirm;
    [ObservableProperty] private string _deleteConfirmText = "";
    [ObservableProperty] private string? _exactEmpty;
    [ObservableProperty] private string? _imageEmpty;
    [ObservableProperty] private string? _audioEmpty;
    [ObservableProperty] private string _exactCountText = "";
    [ObservableProperty] private string _imageCountText = "";
    [ObservableProperty] private string _audioCountText = "";

    // P0-B: групповое переименование выбранных файлов.
    [ObservableProperty] private bool _showRenameDialog;
    [ObservableProperty] private string _renameBaseName = "";
    [ObservableProperty] private int _renameStartNumber = 1;
    [ObservableProperty] private string _renamePreviewText = "";
    [ObservableProperty] private string? _renameConflictText;
    [ObservableProperty] private bool _renameCanApply;
    private List<RenameSourceFile> _renameSources = new();
    private Dictionary<long, (long volumeId, string mount)> _renameMeta = new();

    // P0.4: видимый баннер результата операции (rename/delete) — НЕ прячется вместе с диалогом.
    [ObservableProperty] private string? _operationErrorText;
    [ObservableProperty] private string? _operationSuccessText;

    public string ManualHint => Strings.SimilarManualHint;

    public ResultsViewModel(GroupRepository groups, IVolumeService volumes, IImageMatchService thumbs,
        IFileOpsService fileOps, IHashService hash, ISettingsService settings, IBatchRenameService rename,
        FileRepository files, AppPaths paths, ScanSessionRepository sessions, IResultDistributionService distribute)
    {
        _groups = groups;
        _volumes = volumes;
        _thumbs = thumbs;
        _fileOps = fileOps;
        _hash = hash;
        _settings = settings;
        _rename = rename;
        _distribute = distribute;
        _files = files;
        _paths = paths;
        _sessions = sessions;
        _imageThreshold = settings.Current.DefaultImageThreshold;
        _audioThreshold = settings.Current.DefaultAudioThreshold;
    }

    public override void OnNavigatedTo() => _ = LoadAsync();

    /// <summary>
    /// Уход со страницы: сбрасываем «вооружённые» диалоги. Иначе при возврате поверх свежих
    /// (перезагруженных, без отметок) данных всплывал устаревший диалог, а сохранённый план
    /// переименования выполнялся по уже невидимой пользователю выборке (аудит 2026-07-02).
    /// </summary>
    public override void OnNavigatedFrom()
    {
        ShowDeleteConfirm = false;
        ShowRenameDialog = false;
        ShowDistributeDialog = false;
        ShowHardlinkConfirm = false;
        _renamePlanField = null;
        _hardlinkGroup = null;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        BusyStatus = Strings.Loading;
        _isLoading = true;
        try
        {
            _referenceFolders = _settings.Current.ReferenceFolders
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => { try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; } })
                .ToArray();

            var (exactTotal, imageTotal, audioTotal, mount, label, sid) = await Task.Run(() =>
            {
                var catalog = _volumes.SyncAndGetCatalog();
                var mountById = catalog.Where(v => v.IsConnected && v.MountPath is not null)
                    .ToDictionary(v => v.Id, v => v.MountPath!);
                var labelById = catalog.ToDictionary(v => v.Id, v => v.DisplayName);

                // P0.2: показываем только результаты ПОСЛЕДНЕГО успешно завершённого скана. Если завершённой
                // сессии ещё нет (первый в жизни скан, ещё идёт) — session=null. ВАЖНО (ревью P7): при null НЕ
                // читаем группы «по всем сессиям», иначе частичные группы идущего скана показались бы как
                // готовый результат, из которого можно удалять (счёт = 0, страницы не грузим — см. guard в Append*).
                long? session = _sessions.GetLatestCompletedSessionId();
                // P1.1/P1: ВСЕ вкладки грузим ПОСТРАНИЧНО из БД (счёт групп + первая страница по запросу),
                // а не материализуем весь результат в память.
                int exTotal = session is null ? 0 : _groups.GetGroupCount(ExactTypes, session);
                int imTotal = session is null ? 0 : _groups.GetGroupCount(MatchType.SimilarImage, session);
                int auTotal = session is null ? 0 : _groups.GetGroupCount(MatchType.SimilarAudio, session);
                return (exTotal, imTotal, auTotal, mountById, labelById, session);
            });

            _sessionFilter = sid;
            _exactTotal = exactTotal; _exactOffset = 0;
            _imageTotal = imageTotal; _imageOffset = 0;
            _audioTotal = audioTotal; _audioOffset = 0;
            _mountById = mount; _labelById = label;
            _imageBuilt = _audioBuilt = false;
            // Освобождаем нативную память миниатюр СТАРЫХ результатов (B5): Bitmap-ы без Dispose
            // копились между перезагрузками, GC их почти не собирал (малое managed-давление).
            DisposeGroupThumbnails(_allImage);
            DisposeGroupThumbnails(_allAudio);
            DisposeGroupThumbnails(ExactGroups);
            _allImage.Clear(); _allAudio.Clear();
            ExactGroups.Clear(); SimilarImageGroups.Clear(); SimilarAudioGroups.Clear();
            CanShowMoreSimilarImages = CanShowMoreSimilarAudio = false;

            // Точная вкладка открыта первой → показываем ПЕРВУЮ страницу из БД.
            AppendExactPage();
            ExactCountText = $"{_exactTotal}";
            ExactEmpty = _exactTotal == 0 ? Strings.NoGroups : null;
            // Счётчики похожих — всего найдено сканом (без декодирования миниатюр); слайдер порога сузит показ.
            ImageCountText = $"{_imageTotal}";
            AudioCountText = $"{_audioTotal}";
            ImageEmpty = _imageTotal == 0 ? Strings.NoGroups : null;
            AudioEmpty = _audioTotal == 0 ? Strings.NoGroups : null;

            // Если уже открыта вкладка похожих — построить её первую страницу сразу.
            if (SelectedTab == 1) EnsureImageBuilt();
            else if (SelectedTab == 2) EnsureAudioBuilt();

            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            // Без этого блок «Загрузка…» оставался бы навсегда при сбое чтения БД/тома.
            _isLoading = false;
            IsBusy = false;
        }
    }

    private static void DisposeGroupThumbnails(IEnumerable<GroupItemViewModel> groups)
    {
        foreach (var g in groups)
            foreach (var f in g.Files)
                f.DisposeThumbnail();
    }

    private GroupItemViewModel Build(DuplicateGroup g,
        Dictionary<long, string> mountById, Dictionary<long, string> labelById, bool withThumbnails = true)
    {
        // Точная группа разрешает удаление ТОЛЬКО если сверена байт-в-байт (Confidence>=1.0):
        // hash-only/офлайн exact (Confidence<1.0) — лишь информ., удалять нельзя, пока не перепроверено
        // (P0.2). Похожие — вручную (авто-отметку уже не даём). Видео — как точные.
        bool groupAllowsDeletion = g.MatchType switch
        {
            MatchType.Exact or MatchType.ExactVideo => g.Confidence >= 1.0,
            MatchType.SimilarImage or MatchType.SimilarAudio => true,
            _ => false
        };

        var files = g.Members.Select(m =>
        {
            var f = m.File!;
            string? mount = mountById.GetValueOrDefault(f.VolumeId);
            string? full = mount is null ? null : Path.Combine(mount, f.RelPath);
            string? label = labelById.GetValueOrDefault(f.VolumeId);
            bool isRep = m.IsRepresentative || f.Id == g.RepresentativeId;
            bool prot = full is not null && IsUnderReference(full);
            return new FileItemViewModel(f, m.Similarity, isRep, full, label, _thumbs, prot,
                groupAllowsDeletion, loadThumbnail: withThumbnails);
        });
        var gvm = new GroupItemViewModel(g, files);
        gvm.PropertyChanged += OnGroupPropertyChanged; // отметки → пересчёт доступности «Удалить»
        return gvm;
    }

    private bool IsUnderReference(string fullPath)
    {
        if (_referenceFolders.Length == 0) return false;
        string p;
        // Безопасный отказ: путь не разобрался — считаем защищённым (лучше не удалить, чем удалить лишнее).
        try { p = Path.GetFullPath(fullPath); } catch { return true; }
        foreach (var rf in _referenceFolders)
        {
            if (p.Equals(rf, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(rf + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Достроить следующую страницу точных групп из БД (первая страница + «Показать ещё»).</summary>
    private void AppendExactPage(bool withThumbnails = true)
    {
        // Нет завершённой сессии → ничего не показываем (не читаем чужие/частичные группы). Ревью P7.
        if (_sessionFilter is null) { CanShowMoreExact = false; return; }
        var page = _groups.GetGroupsPage(ExactTypes, _sessionFilter, _exactOffset, PageSize);
        _exactOffset += page.Count;
        foreach (var g in page) ExactGroups.Add(Build(g, _mountById, _labelById, withThumbnails));
        CanShowMoreExact = ExactGroups.Count < _exactTotal;
    }

    [RelayCommand]
    private void ShowMoreExact() => AppendExactPage();

    /// <summary>
    /// Достроить ВСЕ точные группы — для действий «над всем»: авто-выбор и экспорт.
    /// БЕЗ миниатюр: эти VM строятся ради галочек/CSV, а жадное декодирование превью
    /// для десятков тысяч фото давало гигабайты Bitmap-ов и OOM (B5).
    /// </summary>
    private void EnsureAllExactBuilt()
    {
        while (ExactGroups.Count < _exactTotal)
        {
            int before = ExactGroups.Count;
            AppendExactPage(withThumbnails: false);
            if (ExactGroups.Count == before) break; // защита от зацикливания, если БД отдала меньше
        }
    }

    /// <summary>Построить ПЕРВУЮ страницу похожих фото при первом открытии вкладки (миниатюры — только для неё).</summary>
    private void EnsureImageBuilt()
    {
        if (_imageBuilt) return;
        _imageBuilt = true;
        _imageOffset = 0;
        _allImage.Clear();
        AppendSimilarImagePage();
    }

    private void EnsureAudioBuilt()
    {
        if (_audioBuilt) return;
        _audioBuilt = true;
        _audioOffset = 0;
        _allAudio.Clear();
        AppendSimilarAudioPage();
    }

    /// <summary>Достроить следующую страницу похожих фото из БД (миниатюры декодируются только для неё, P1).</summary>
    private void AppendSimilarImagePage(bool withThumbnails = true)
    {
        if (_sessionFilter is null) { CanShowMoreSimilarImages = false; return; }
        var page = _groups.GetGroupsPage(MatchType.SimilarImage, _sessionFilter, _imageOffset, PageSize);
        _imageOffset += page.Count;
        foreach (var g in page) _allImage.Add(Build(g, _mountById, _labelById, withThumbnails));
        CanShowMoreSimilarImages = _imageOffset < _imageTotal;
        RefilterImages();
    }

    private void AppendSimilarAudioPage(bool withThumbnails = true)
    {
        if (_sessionFilter is null) { CanShowMoreSimilarAudio = false; return; }
        var page = _groups.GetGroupsPage(MatchType.SimilarAudio, _sessionFilter, _audioOffset, PageSize);
        _audioOffset += page.Count;
        foreach (var g in page) _allAudio.Add(Build(g, _mountById, _labelById, withThumbnails));
        CanShowMoreSimilarAudio = _audioOffset < _audioTotal;
        RefilterAudio();
    }

    [RelayCommand]
    private void ShowMoreSimilarImages() => AppendSimilarImagePage();

    [RelayCommand]
    private void ShowMoreSimilarAudio() => AppendSimilarAudioPage();

    /// <summary>Достроить ВСЕ похожие фото — для экспорта. Без миниатюр: они нужны только на экране (B5).</summary>
    private void EnsureAllSimilarImagesBuilt()
    {
        EnsureImageBuilt();
        while (_imageOffset < _imageTotal)
        {
            int before = _imageOffset;
            AppendSimilarImagePage(withThumbnails: false);
            if (_imageOffset == before) break; // защита от зацикливания, если БД отдала меньше
        }
    }

    private void EnsureAllSimilarAudioBuilt()
    {
        EnsureAudioBuilt();
        while (_audioOffset < _audioTotal)
        {
            int before = _audioOffset;
            AppendSimilarAudioPage(withThumbnails: false);
            if (_audioOffset == before) break;
        }
    }

    partial void OnImageThresholdChanged(double value)
    {
        if (_isLoading) return;
        if (_imageBuilt) RefilterImages();   // вкладка построена — перефильтровать загруженные страницы
        // иначе вкладка ещё не открыта: счётчик показывает «всего найдено», порог применится при открытии.
    }

    partial void OnAudioThresholdChanged(double value)
    {
        if (_isLoading) return;
        if (_audioBuilt) RefilterAudio();
    }

    private void RefilterImages()
    {
        SimilarImageGroups.Clear();
        foreach (var g in _allImage.Where(g => g.TopSimilarity >= ImageThreshold))
            SimilarImageGroups.Add(g);
        // Счётчик в заголовке — ВСЕГО найдено сканом (стабилен, как у точной вкладки), а не отфильтрованная
        // страница (ревью P7: иначе число «сколько найдено» молча падало бы при пейджинге/слайдере).
        // Пустое состояние различаем: «ничего не найдено» vs «всё скрыто текущим порогом».
        ImageEmpty = _imageTotal == 0 ? Strings.NoGroups
            : SimilarImageGroups.Count == 0 ? "Похожие есть, но скрыты текущим порогом — сдвиньте слайдер влево."
            : null;
        // Порог мог скрыть группы с отметками — кнопки действий обязаны пересчитать доступность,
        // иначе остаются визуально активными и «не работают» (аудит 2026-07-02).
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RefilterAudio()
    {
        SimilarAudioGroups.Clear();
        foreach (var g in _allAudio.Where(g => g.TopSimilarity >= AudioThreshold))
            SimilarAudioGroups.Add(g);
        AudioEmpty = _audioTotal == 0 ? Strings.NoGroups
            : SimilarAudioGroups.Count == 0 ? "Похожие есть, но скрыты текущим порогом — сдвиньте слайдер влево."
            : null;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<GroupItemViewModel> CurrentGroups() => SelectedTab switch
    {
        1 => SimilarImageGroups,
        2 => SimilarAudioGroups,
        _ => ExactGroups
    };

    /// <summary>Удалять можно, только если в текущей вкладке есть отмеченные файлы.</summary>
    private bool CanDeleteSelected() => CurrentGroups().Any(g => g.MarkedCount > 0);

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        int count = CurrentGroups().Sum(g => g.MarkedCount);
        if (count == 0) return;
        // P2.3: явно перечисляем, что будет перемещено и что НЕ тронем (недоступные/непроверенные
        // файлы вообще нельзя отметить — но честно сообщаем их число в текущей вкладке).
        int blocked = CurrentGroups().SelectMany(g => g.Files).Count(f => !f.IsRepresentative && !f.CanMark);
        var extra = blocked > 0
            ? $"\nНедоступно сейчас или проверено не полностью — НЕ будет тронуто: {blocked}."
            : "";
        // P0-G: для вкладок «похожие» — отдельное предупреждение (это не идентичные файлы).
        var similarWarn = SelectedTab != 0
            ? "\n\n⚠ Это ПОХОЖИЕ, а не одинаковые файлы. Удаляйте только после ручной проверки."
            : "";
        DeleteConfirmText = $"{Strings.ConfirmDeleteBody}\n\nБудет перемещено в Корзину: {count}.{extra}{similarWarn}";
        ShowDeleteConfirm = true;
    }

    partial void OnSelectedTabChanged(int value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        DistributeSelectedCommand.NotifyCanExecuteChanged();
        AutoSelectAllCommand.NotifyCanExecuteChanged(); // авто-выбор включён только на вкладке «точные»
        // Похожие вкладки строим ЛЕНИВО — миниатюры декодируются только при открытии вкладки.
        if (value == 1) EnsureImageBuilt();
        else if (value == 2) EnsureAudioBuilt();
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupItemViewModel.MarkedCount))
        {
            DeleteSelectedCommand.NotifyCanExecuteChanged();
            RenameSelectedCommand.NotifyCanExecuteChanged();
            DistributeSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelDelete() => ShowDeleteConfirm = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        ShowDeleteConfirm = false;
        IsBusy = true;
        BusyStatus = "Проверяем выбранные файлы перед перемещением...";
        bool useRecycle = _settings.Current.UseRecycleBin;

        OperationErrorText = null; OperationSuccessText = null;
        // P0-G: метод удаления ЗАЩИЩАЕТ СЕБЯ САМ, не полагаясь только на UI-гейтинг галочек.
        // CanMark = группа разрешает удаление И файл не защищён И путь доступен — повторно проверяем здесь.
        var toDelete = CurrentGroups()
            .SelectMany(g => g.MarkedFiles.Select(f => (group: g, file: f)))
            .Where(x => x.file.FullPath is not null)
            .Where(x => x.file.CanMark)
            .Where(x => !x.file.IsRepresentative) // эталон не удаляем никогда
            .ToList();

        int moved = 0, failed = 0, blocked = 0;
        await Task.Run(() =>
        {
            foreach (var (group, file) in toDelete)
            {
                // P0.1: ПОЛНАЯ ПРОВЕРКА НА МОМЕНТ ДЕЙСТВИЯ — между сканом и удалением файл могли заменить,
                // переместить или изменить (в т.ч. на содержимое ТОГО ЖЕ размера). Сверяем размер + время,
                // а для точных/видео-групп — пересчитанный полный хэш. Не прошёл проверку — НЕ трогаем.
                if (!RevalidateBeforeDelete(group, file)) { blocked++; continue; }

                if (_fileOps.Delete(file.Model, file.FullPath!, useRecycle, group.Model.Id)) moved++;
                else failed++;
            }
        });

        if (blocked > 0 || failed > 0)
        {
            var msg = $"Перемещено в Корзину: {moved}.";
            if (blocked > 0)
                msg += $" Файлы изменились после проверки и не были тронуты — запустите проверку этой папки заново: {blocked}.";
            if (failed > 0) msg += $" Не удалось переместить: {failed}.";
            OperationErrorText = msg;
        }
        else if (moved > 0)
            OperationSuccessText = $"Перемещено в Корзину: {moved}.";

        await LoadAsync();
    }

    /// <summary>
    /// Ревалидация файла НА МОМЕНТ УДАЛЕНИЯ (P0.1). Удаляем только если файл не изменился с момента скана:
    /// существует, тот же размер, то же время изменения и создания, а для точных/видео-групп — совпадает
    /// ПЕРЕСЧИТАННЫЙ полный хэш с проиндексированным. Для похожих фото/музыки полный хэш по ТЗ не считаем,
    /// но размер и время всё равно сверяем (выбранный файл мог измениться после просмотра). Любой сбой
    /// проверки трактуем как «файл изменился» — и не трогаем его (лучше не удалить, чем удалить лишнее).
    /// </summary>
    private bool RevalidateBeforeDelete(GroupItemViewModel group, FileItemViewModel file)
    {
        try
        {
            var m = file.Model;
            // Файл «ушёл в облако» (OneDrive-плейсхолдер): содержимое сверить нельзя, не скачав его,
            // а удаление плейсхолдера удаляет и облачную копию. Не трогаем (B4).
            if (CloudFiles.IsPlaceholderPath(file.FullPath!)) return false;
            var fi = new FileInfo(LongPath.Fix(file.FullPath!));
            if (!fi.Exists) return false;
            if (fi.Length != m.Size) return false;
            if (!TimesMatch(fi.LastWriteTimeUtc, m.Mtime)) return false;
            // Дату создания сверяем только если она была проиндексирована (старые строки могут быть пустыми).
            if (m.Created != default && !TimesMatch(fi.CreationTimeUtc, m.Created)) return false;

            bool exact = group.Model.MatchType is MatchType.Exact or MatchType.ExactVideo;
            if (exact && m.FullHash is not null)
            {
                var current = _hash.ComputeFullHash(file.FullPath!, default);
                if (!current.AsSpan().SequenceEqual(m.FullHash)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Сравнение времён с небольшим допуском: exFAT/FAT32 и сетевые ФС огрубляют mtime (до 1–2 c).</summary>
    private static bool TimesMatch(DateTime actualUtc, DateTimeOffset indexed) =>
        Math.Abs((actualUtc - indexed.UtcDateTime).TotalSeconds) <= 2.0;

    // --- P0-B: групповое переименование выбранных файлов ---
    private bool CanRenameSelected() => CurrentGroups().Any(g => g.MarkedCount > 0);

    [RelayCommand(CanExecute = nameof(CanRenameSelected))]
    private void RenameSelected()
    {
        // Берём отмеченные файлы текущей вкладки как выбранный набор (отмечать можно только доступные).
        var marked = CurrentGroups().SelectMany(g => g.MarkedFiles)
            .Where(f => f.FullPath is not null).ToList();
        _renameSources = marked
            .Select(f => new RenameSourceFile(f.Model.Id, f.FullPath!, f.Model.Created, f.Model.Mtime,
                f.Model.Size, f.CanMark))
            .ToList();
        // Запоминаем том + точку монтирования каждого файла — нужно для пересчёта rel_path после rename.
        _renameMeta = new Dictionary<long, (long, string)>();
        foreach (var f in marked)
            _renameMeta[f.Model.Id] = (f.Model.VolumeId, _mountById.GetValueOrDefault(f.Model.VolumeId, ""));
        if (_renameSources.Count == 0) return;
        OperationErrorText = null; OperationSuccessText = null;

        RenameBaseName = "Файл";
        RenameStartNumber = 1;
        RefreshRenamePlan();
        ShowRenameDialog = true;
    }

    partial void OnRenameBaseNameChanged(string value) { if (ShowRenameDialog) RefreshRenamePlan(); }
    partial void OnRenameStartNumberChanged(int value) { if (ShowRenameDialog) RefreshRenamePlan(); }

    private void RefreshRenamePlan()
    {
        var req = new BatchRenameRequest(_renameSources, RenameBaseName,
            System.Math.Max(0, RenameStartNumber), 3, RenameSortOrder.Name);
        var plan = _rename.BuildPlan(req);
        _renamePlanField = plan;

        // Предпросмотр: первые строки «старое → новое».
        var lines = plan.Items.Where(i => !i.Skipped).Take(12)
            .Select(i => $"{i.OldName}  →  {i.NewName}");
        var preview = string.Join("\n", lines);
        int more = plan.Items.Count(i => !i.Skipped) - 12;
        if (more > 0) preview += $"\n… и ещё {more}";
        int skipped = plan.Items.Count(i => i.Skipped);
        if (skipped > 0) preview += $"\n(пропущено недоступных/защищённых: {skipped})";
        RenamePreviewText = preview;

        RenameConflictText = plan.Conflicts.Count > 0
            ? $"Конфликты имён: {plan.Conflicts.Count}. Измените имя или номер."
            : null;
        RenameCanApply = plan.CanExecute;
    }

    private BatchRenamePlan? _renamePlanField;

    [RelayCommand]
    private void DismissOperationBanner() { OperationErrorText = null; OperationSuccessText = null; }

    // --- P1.6: распределение результатов по папкам ---
    [ObservableProperty] private bool _showDistributeDialog;
    [ObservableProperty] private bool _distributeCopyMode; // false = только отчёт CSV (по умолчанию — безопасно)
    [ObservableProperty] private string? _distributeDestination;
    [ObservableProperty] private string _distributePreviewText = "";
    [ObservableProperty] private bool _distributeCanRun;
    private List<DistributionFile> _distributeSources = new();

    /// <summary>Устанавливается представлением — выбор папки назначения.</summary>
    public Func<Task<string?>>? PickDestinationFolder { get; set; }

    private bool CanDistribute() => CurrentGroups().Any(g => g.MarkedCount > 0);

    [RelayCommand(CanExecute = nameof(CanDistribute))]
    private void DistributeSelected()
    {
        _distributeSources = CurrentGroups()
            .SelectMany(g => g.MarkedFiles)
            .Where(f => f.FullPath is not null)
            .Select(f => new DistributionFile(f.Model.Id, f.FullPath!, f.Model.Category.ToString(),
                f.Similarity, f.Model.Size))
            .ToList();
        if (_distributeSources.Count == 0) return;
        OperationErrorText = null; OperationSuccessText = null;
        DistributeCopyMode = false;
        RefreshDistributePreview();
        ShowDistributeDialog = true;
    }

    [RelayCommand]
    private async Task PickDistributeFolderAsync()
    {
        if (PickDestinationFolder is null) return;
        var folder = await PickDestinationFolder();
        if (!string.IsNullOrWhiteSpace(folder)) { DistributeDestination = folder; RefreshDistributePreview(); }
    }

    partial void OnDistributeCopyModeChanged(bool value) => RefreshDistributePreview();

    private void RefreshDistributePreview()
    {
        if (string.IsNullOrWhiteSpace(DistributeDestination))
        {
            DistributePreviewText = "Выберите папку назначения.";
            DistributeCanRun = false;
            return;
        }
        var req = new DistributionRequest(_distributeSources, DistributeDestination!,
            DistributeCopyMode ? DistributionMode.CopyFiles : DistributionMode.ReportOnly);
        var plan = _distribute.BuildPlan(req);
        var lines = plan.Items.Take(12).Select(i => $"{Path.GetFileName(i.SourcePath)}  →  {i.FolderKey}\\");
        DistributePreviewText = string.Join("\n", lines);
        if (plan.Items.Count > 12) DistributePreviewText += $"\n… и ещё {plan.Items.Count - 12}";
        DistributeCanRun = plan.CanExecute;
    }

    [RelayCommand]
    private void CancelDistribute() => ShowDistributeDialog = false;

    [RelayCommand]
    private async Task RunDistributeAsync()
    {
        if (string.IsNullOrWhiteSpace(DistributeDestination)) return;
        ShowDistributeDialog = false;
        IsBusy = true;
        var req = new DistributionRequest(_distributeSources, DistributeDestination!,
            DistributeCopyMode ? DistributionMode.CopyFiles : DistributionMode.ReportOnly);
        var result = await Task.Run(() =>
        {
            var plan = _distribute.BuildPlan(req);
            return _distribute.Execute(plan, req, default);
        });
        IsBusy = false;
        if (result.Error is not null)
            OperationErrorText = $"Распределение не удалось: {result.Error}";
        else if (DistributeCopyMode)
            OperationSuccessText = $"Скопировано: {result.Copied}, не удалось: {result.Failed}. " +
                                   $"Оригиналы не тронуты. Отчёт: {result.ReportPath}";
        else
            OperationSuccessText = $"Отчёт сохранён: {result.ReportPath}. Файлы не двигались.";
    }

    [RelayCommand]
    private void CancelRename() => ShowRenameDialog = false;

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        if (_renamePlanField is null || !_renamePlanField.CanExecute) return;
        ShowRenameDialog = false;
        IsBusy = true;
        OperationErrorText = null; OperationSuccessText = null;
        var plan = _renamePlanField;
        var result = await Task.Run(() => _rename.Execute(plan, default));

        if (result.RolledBack)
        {
            // P0.4: ошибку показываем в видимом баннере страницы, а не в скрытом диалоге.
            var msg = $"Переименование отменено из-за ошибки: {result.Error}";
            if (result.Orphans.Count > 0)
                msg += "\nНе удалось вернуть имена автоматически (будет повторено при следующем запуске):\n" +
                       string.Join("\n", result.Orphans.Select(o =>
                           $"{o.CurrentPath}  →  исходное имя: {Path.GetFileName(o.OriginalPath)}"));
            OperationErrorText = msg;
            await LoadAsync();
            return;
        }

        // P0.1: файлы переименованы физически → обновляем строки в БД, иначе UI ссылается на старые пути.
        int dbFailed = await Task.Run(() => UpdateIndexAfterRename(result.RenamedPairs));
        if (dbFailed > 0)
            OperationErrorText = $"Переименовано: {result.Renamed}, но индекс обновлён не полностью " +
                                 $"({dbFailed}). Запустите проверку этой папки заново. " +
                                 $"Детали сохранены в папке: {Path.Combine(_paths.RootDir, "recovery")}.";
        else
            OperationSuccessText = $"Переименовано: {result.Renamed}. Индекс обновлён.";

        await LoadAsync(); // перечитать (пути изменились)
    }

    /// <summary>Обновить строки БД по факту переименования. Возвращает число неудачных обновлений.</summary>
    private int UpdateIndexAfterRename(IReadOnlyList<RenamePair> pairs)
    {
        var failures = new List<(RenamePair pair, string reason)>();

        // Пакет собираем целиком и обновляем ОДНИМ вызовом (одна транзакция, два прохода):
        // по-файловое обновление при пересечении имён внутри пакета (сдвиг нумерации) удаляло
        // строку живого файла из индекса и маскировало это как успех (аудит 2026-07-02, B2).
        var updates = new List<RenameDbUpdate>(pairs.Count);
        foreach (var pair in pairs)
        {
            try
            {
                if (!_renameMeta.TryGetValue(pair.FileId, out var meta))
                { failures.Add((pair, "meta-missing")); continue; }
                if (string.IsNullOrEmpty(meta.mount))
                { failures.Add((pair, "empty-mount")); continue; }

                var fi = new FileInfo(pair.NewFullPath);
                var newRel = Path.GetRelativePath(meta.mount, pair.NewFullPath);
                var ext = Path.GetExtension(pair.NewFullPath);
                updates.Add(new RenameDbUpdate(pair.FileId, meta.volumeId, newRel,
                    Path.GetFileName(pair.NewFullPath), string.IsNullOrEmpty(ext) ? null : ext,
                    fi.LastWriteTimeUtc, fi.CreationTimeUtc, fi.Length));
            }
            catch (Exception ex) { failures.Add((pair, "meta-exception: " + ex.Message)); }
        }

        try
        {
            var failedIds = _files.UpdatePathsAfterRename(updates);
            foreach (var id in failedIds)
            {
                var pair = pairs.FirstOrDefault(p => p.FileId == id);
                if (pair is not null) failures.Add((pair, "db-row-not-updated"));
            }
        }
        catch (Exception ex)
        {
            foreach (var u in updates)
            {
                var pair = pairs.First(p => p.FileId == u.FileId);
                failures.Add((pair, "db-exception: " + ex.Message));
            }
        }

        // Safety (P0.1): физический rename прошёл, но БД не обновилась — пишем recovery-файл с путями + причиной.
        if (failures.Count > 0)
        {
            try
            {
                var dir = Path.Combine(_paths.RootDir, "recovery");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "rename_" + Guid.NewGuid().ToString("N") + ".json");
                var payload = failures.Select(f => new { f.pair.FileId, f.pair.OldFullPath, f.pair.NewFullPath, Reason = f.reason });
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* recovery — вспомогательное, не мешаем */ }
        }
        return failures.Count;
    }

    // --- Авто-выбор по правилу (оставить эталон, отметить остальные) ---
    // Доступно ТОЛЬКО на вкладке точных дубликатов. Похожие фото/музыка отмечаются исключительно
    // вручную: их «похожесть» не равна идентичности, автоудаление здесь опасно (раздел 6.3).
    private bool CanAutoSelect() => SelectedTab == 0;

    [RelayCommand(CanExecute = nameof(CanAutoSelect))]
    private void AutoSelectAll()
    {
        if (SelectedTab != 0) return; // защита на случай вызова в обход CanExecute
        EnsureAllExactBuilt();        // «Выбрать все» действует на ВСЕ точные группы, а не только на стр. 1
        foreach (var g in ExactGroups)
        {
            // Hash-only/непроверенные группы (confidence < 1.0: есть офлайн/непрочитанные члены)
            // НЕ автоотмечаем — их можно удалять только вручную после перепроверки (раздел 2, P0).
            if (g.Model.Confidence < 1.0) continue;
            foreach (var f in g.Files)
                f.IsMarked = !f.IsRepresentative && !f.IsProtected;
        }
    }

    // --- Экспорт отчёта в CSV ---
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (PickSaveFile is null) return;
        var path = await PickSaveFile("deduplicator-report.csv");
        if (string.IsNullOrEmpty(path)) return;

        var sb = new StringBuilder();
        sb.AppendLine("Тип;Группа;Эталон;Схожесть;Носитель;Путь;Размер(байт)");
        // Экспорт полный: достраиваем ВСЕ страницы точных и похожих групп, если их ещё не открывали.
        EnsureAllExactBuilt();
        EnsureAllSimilarImagesBuilt();
        EnsureAllSimilarAudioBuilt();
        // Экспортируем ПОЛНЫЕ наборы (_allImage/_allAudio), а не отфильтрованные слайдером порога коллекции
        // (ревью P7: иначе «полный» отчёт молча терял бы похожие группы ниже текущего порога).
        AppendGroups(sb, ExactGroups, Strings.TabExact);
        AppendGroups(sb, _allImage, Strings.TabSimilarImages);
        AppendGroups(sb, _allAudio, Strings.TabSimilarAudio);

        try
        {
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
            OperationSuccessText = $"Отчёт сохранён: {path}";
        }
        catch (Exception ex)
        {
            // Молчаливый провал (файл открыт в Excel, носитель защищён от записи) оставлял
            // пользователя в уверенности, что отчёт обновлён (B9).
            OperationErrorText = $"Не удалось сохранить отчёт: {ex.Message}";
        }
    }

    private static void AppendGroups(StringBuilder sb, IEnumerable<GroupItemViewModel> groups, string type)
    {
        int n = 0;
        foreach (var g in groups)
        {
            n++;
            foreach (var f in g.Files)
            {
                sb.Append(Csv(type)).Append(';')
                  .Append(n).Append(';')
                  .Append(f.IsRepresentative ? "да" : "").Append(';')
                  .Append(f.IsRepresentative ? "" : f.SimilarityText).Append(';')
                  .Append(Csv(f.VolumeText)).Append(';')
                  .Append(Csv(f.RelPath)).Append(';')
                  .Append(f.Model.Size).AppendLine();
            }
        }
    }

    private static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    // --- Открыть папку с файлом ---
    [RelayCommand]
    private void OpenContaining(FileItemViewModel? file)
    {
        if (file?.FullPath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"")
            { UseShellExecute = true });
        }
        catch { }
    }

    // --- Замена дублей жёсткими ссылками (NTFS) ---
    [ObservableProperty] private bool _showHardlinkConfirm;
    [ObservableProperty] private string _hardlinkConfirmText = "";
    private GroupItemViewModel? _hardlinkGroup;

    [RelayCommand]
    private void Hardlink(GroupItemViewModel? group)
    {
        if (group is null) return;
        _hardlinkGroup = group;
        HardlinkConfirmText = Strings.HardlinkWarning;
        ShowHardlinkConfirm = true;
    }

    [RelayCommand]
    private void CancelHardlink() { ShowHardlinkConfirm = false; _hardlinkGroup = null; }

    [RelayCommand]
    private async Task ConfirmHardlinkAsync()
    {
        ShowHardlinkConfirm = false;
        var group = _hardlinkGroup;
        _hardlinkGroup = null;
        if (group is null) return;

        var rep = group.Files.FirstOrDefault(f => f.IsRepresentative);
        if (rep?.FullPath is null) return;
        if (!File.Exists(rep.FullPath)) return; // эталон мог исчезнуть, пока был открыт диалог

        var targets = group.Files
            .Where(f => !f.IsRepresentative && !f.IsProtected && f.FullPath is not null)
            .ToList();
        if (targets.Count == 0) return;

        IsBusy = true;
        await Task.Run(() =>
        {
            foreach (var f in targets)
                _fileOps.ReplaceWithHardLink(f.Model, f.FullPath!, rep.FullPath!, group.Model.Id);
        });
        await LoadAsync();
    }
}
