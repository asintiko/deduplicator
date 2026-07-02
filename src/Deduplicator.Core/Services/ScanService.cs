using System.Text.Json;
using Deduplicator.Core.Audio;
using Deduplicator.Core.Data;
using Deduplicator.Core.Engine;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Imaging;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;
// У продукта и движка одноимённые типы (ScanOptions/ScanProgress/FileEntry) — движковые только через алиас.
// Алиас Fde (не Engine), чтобы не конфликтовать с пространством имён Deduplicator.Core.Engine.
using Fde = FastDedup.Engine;

namespace Deduplicator.Core.Services;

public interface IScanService
{
    ScanProgress Progress { get; }
    event Action<ScanProgress>? ProgressChanged;

    bool IsRunning { get; }
    bool IsPaused { get; }

    Task RunAsync(ScanOptions options, CancellationToken ct);
    void Pause();
    void Resume();

    /// <summary>
    /// 2.8: id сессии ИДУЩЕГО скана (null вне скана). Вместе с <see cref="ActiveExactReady"/>
    /// позволяет UI показывать предпросмотр точных групп до завершения всего скана.
    /// </summary>
    long? ActiveSessionId { get; }

    /// <summary>
    /// 2.8: точные группы текущей сессии уже ЦЕЛИКОМ в БД (движок exact отработал), а скан
    /// продолжается этапами похожих. Сбрасывается по завершении скана (любым исходом).
    /// Выводить это из Stage ненадёжно (Grouping ставится и внутри exact-движка) — явный флаг.
    /// </summary>
    bool ActiveExactReady { get; }

    /// <summary>Незавершённая сессия (для предложения возобновить).</summary>
    ScanSession? GetResumableSession();
}

/// <summary>
/// Оркестратор многоступенчатой воронки сканирования (раздел 7).
/// Возобновляемый, отменяемый, с паузой; UI-поток не блокирует.
/// </summary>
public sealed class ScanService : IScanService, IDisposable
{
    private const int MetaBatch = 2000;
    private const int HashBatch = 500;
    // 2.7: размер пачки для lookup существующих метаданных при инкрементальном скане
    // (паттерн AddScope: до 400 параметров в IN — ниже лимита SQLite на параметры).
    private const int LookupBatch = 400;

    private readonly VolumeRepository _volumes;
    private readonly FileRepository _files;
    private readonly ScanSessionRepository _sessions;
    private readonly VolumeScanStateRepository _scanState;
    private readonly IVolumeService _volumeService;
    private readonly IImageMatchService _images;
    private readonly IAudioMatchService _audio;
    private readonly IDuplicateGrouper _grouper;
    private readonly ISettingsService _settings;
    private readonly Fde.FastFileLister _fastLister;
    // Движок точных дубликатов v2 (потоковая воронка вместо старого HashFunnel с N+1 по размеру).
    private readonly IExactDuplicateEngine _exactEngine;

    private RepresentativeRule _representativeRule;
    private bool _computePixelHash;
    private ScanOptions _options = new();

    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "$RECYCLE.BIN", "System Volume Information", "$SysReset", "Recovery", "$WinREAgent"
    };

    private static readonly HashSet<string> MagicProbeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".dat", ".tmp", ".cache", ".part", ".download", ".unknown"
    };

    private readonly ManualResetEventSlim _gate = new(true);
    private readonly object _progressLock = new();

    // План чтения на каждый том (вместо одного глобального _parallelism). HDD/съёмные читаются
    // последовательно, SSD/NVMe — параллельно; один HDD не заставляет SSD читать в один поток.
    private Dictionary<long, VolumeReadPlan> _readPlans = new();
    private int _maxVolumeParallelism = 1;

    // Сглаживатель прогресса: per-file Report не дёргает UI на каждый файл.
    private ProgressCoalescer<ScanProgress>? _coalescer;

    // Накопленные уведомления о режиме перечисления по дискам (чтобы предупреждение одного диска
    // не затиралось другим и не висело устаревшим). Доступ только из потока скана.
    private readonly List<string> _enumNotices = new();

    public ScanProgress Progress { get; private set; } = new();
    public event Action<ScanProgress>? ProgressChanged;

    // volatile — флаги читаются из UI-потока, пишутся из фонового (видимость без блокировок).
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    public bool IsRunning { get => _isRunning; private set => _isRunning = value; }
    public bool IsPaused { get => _isPaused; private set => _isPaused = value; }

    // 2.8: прогрессивные результаты. Читаются из UI-потока, пишутся из фонового; long не атомарен
    // на 32-бит — доступ через Interlocked (0 = скана нет). Флаг готовности exact — volatile.
    private long _activeSessionId;
    private volatile bool _activeExactReady;
    public long? ActiveSessionId
    {
        get
        {
            long v = Interlocked.Read(ref _activeSessionId);
            return v > 0 ? v : null;
        }
    }
    public bool ActiveExactReady => _activeExactReady;

    public ScanService(
        VolumeRepository volumes, FileRepository files, ScanSessionRepository sessions,
        IVolumeService volumeService, IHashService hash, IImageMatchService images,
        IAudioMatchService audio, IDuplicateGrouper grouper, ISettingsService settings,
        Fde.FastFileLister fastLister, VolumeScanStateRepository scanState)
    {
        _volumes = volumes;
        _files = files;
        _sessions = sessions;
        _scanState = scanState;
        _volumeService = volumeService;
        _images = images;
        _audio = audio;
        _grouper = grouper;
        _settings = settings;
        _fastLister = fastLister;
        // Зависим от абстракции IExactDuplicateEngine; конкретику собираем из уже внедрённых
        // репозиториев/хэшера/группировщика, чтобы не менять сигнатуру конструктора и все вызовы.
        _exactEngine = new ExactDuplicateEngineV2(files, hash, grouper);
    }

    public ScanSession? GetResumableSession() => _sessions.GetUnfinished();

    public void Pause() { if (IsRunning) { IsPaused = true; _gate.Reset(); } }
    public void Resume() { IsPaused = false; _gate.Set(); }

    public Task RunAsync(ScanOptions options, CancellationToken ct) =>
        Task.Run(() => RunCore(options, ct), ct);

    private void RunCore(ScanOptions options, CancellationToken ct)
    {
        IsRunning = true;
        IsPaused = false;
        _gate.Set();
        _representativeRule = _settings.Current.DefaultRepresentativeRule;
        _computePixelHash = _settings.Current.ComputePixelHash;
        _options = options;
        _enumNotices.Clear();
        // Диагностика: фиксируем версию движка в логе — так видно, не запущена ли старая сборка.
        AppLog.Write($"Старт скана. Движок: {EngineBuildInfo.Summary}");
        Progress = new ScanProgress { Stage = ScanStage.Enumerating };
        // Реальная отправка событий в UI идёт через сглаживатель (троттлинг ~5/сек по ТЗ + немедленная
        // отправка смены этапа/завершения). Создаём на каждый запуск, освобождаем в finally.
        _coalescer = new ProgressCoalescer<ScanProgress>(snap => ProgressChanged?.Invoke(snap),
            TimeSpan.FromMilliseconds(200));
        // Создание сессии — ВНУТРИ try: это запись в БД (диск переполнен/БД занята — реальный кейс
        // у аудитории, чистящей забитые диски), и сбой здесь должен давать состояние «Ошибка
        // сканирования», а не необработанное исключение с падением приложения и вечным IsRunning.
        long sessionId = -1;
        bool succeeded = false;
        // 1.1: телеметрия ресурсов на время скана (пики WS/heap/потоков → лог) — база «до/после»
        // для перф-работ и диагностика поведения на слабых ПК.
        using var telemetry = new ResourceTelemetry("скан");

        // 2.9: режим «Эко» — пониженный приоритет процесса + чтение содержимого в один поток;
        // скан перестаёт душить 2-ядерный ноутбук. Приоритет ГАРАНТИРОВАННО восстанавливается в
        // finally. PROCESS_MODE_BACKGROUND сознательно не используем: он режет и рабочий сет.
        bool eco = _settings.Current.EcoMode;
        System.Diagnostics.ProcessPriorityClass? savedPriority = null;
        if (eco)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                savedPriority = proc.PriorityClass;
                proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                AppLog.Write("Режим «Эко»: приоритет BelowNormal, чтение в один поток.");
            }
            catch (Exception ex) { AppLog.Error("EcoPriority", ex); }
        }
        int effectiveParallelism = eco ? 1 : Math.Max(1, options.Parallelism);

        try
        {
            sessionId = _sessions.Create(JsonSerializer.Serialize(options));
            // 2.8: публикуем id идущей сессии — UI сможет предпросматривать её точные группы,
            // как только ActiveExactReady станет true. Сбрасывается в finally.
            Interlocked.Exchange(ref _activeSessionId, sessionId);

            // P0-A: «что искать» не должно быть пустым — иначе скан бессмыслен.
            if (options.IncludedCategories.Count == 0)
                throw new InvalidOperationException("Не выбран ни один тип файлов для поиска.");

            // Привязка к подключённым носителям (точки монтирования).
            var catalog = _volumeService.SyncAndGetCatalog();
            var volumesById = catalog.ToDictionary(v => v.Id);
            var mountById = catalog
                .Where(v => v.IsConnected && v.MountPath is not null)
                .ToDictionary(v => v.Id, v => v.MountPath!);

            var targets = ResolveTargets(options, catalog);
            var scanScope = BuildScanScope(targets, mountById);

            // Инкрементальность через USN-журнал: тома, не менявшиеся с прошлого скана, не перечисляем
            // заново (на 1M файлов это экономит весь обход); тома с НЕБОЛЬШИМИ изменениями (3.1)
            // обновляются точечно по дельте журнала. Любая неоднозначность → полный пересбор.
            string optionsHash = OptionsHash(options);
            var (skipVolumes, journalSnaps, deltaPlans) = PlanIncremental(targets, volumesById, options, optionsHash);

            // === Этап 1: обход и метаданные (пропуская неизменившиеся тома) ===
            Enumerate(targets, mountById, ct, sessionId, skipVolumes, deltaPlans);

            // === Этапы 2–5: точные дубликаты через движок v2 ===
            // Потоковая воронка (size→partial→full→byte-verify→группы), без N+1 по размеру.
            // ScanService здесь — фасад: движок делает воронку и точную группировку, мы только
            // прокидываем прогресс/паузу/сессии через ScanReporter.
            // P0-A: группировка должна учитывать не только текущий скоуп (том/папка), но и ВЫБРАННЫЕ
            // категории — иначе старые записи невыбранных типов (из прежнего полного скана, ещё в БД)
            // всплыли бы как удаляемые exact-группы при скане «только фото». Категорийность тут — защита.
            Func<FileRecord, bool> inScope =
                f => scanScope.Contains(f) && options.IncludedCategories.Contains(f.Category);

            // 2.10: предикат «файл в папке-эталоне» для умного выбора эталона (KeepScorer).
            // Core настроек и точек монтирования не знает — фасад строит готовый Func и прокидывает
            // его в exact-движок и группировку похожих. null (нет папок-эталонов) — бонус не начисляется.
            Func<FileRecord, bool>? isPreferredKeep = BuildPreferredKeepPredicate(mountById);

            var exactRequest = new ExactScanRequest
            {
                MountById = mountById,
                VolumesById = volumesById,
                InScope = inScope,
                ReadPlans = _readPlans,
                MaxVolumeParallelism = _maxVolumeParallelism,
                RequestedParallelism = effectiveParallelism,
                Rule = _representativeRule,
                SessionId = sessionId,
                IsPreferredKeep = isPreferredKeep
            };
            var exactResult = _exactEngine.ScanExact(exactRequest, new ScanReporter(this, sessionId, ct), ct);
            // 2.8: точные группы сессии целиком в БД — «Результаты» могут показывать предпросмотр,
            // пока идут перцептив и похожие (на большом диске это часы). Флаг снимается в finally.
            _activeExactReady = true;
            if (exactResult.GroupsCreated > 0)
                NoteEnumerationNotice("Точные дубликаты уже можно посмотреть в «Результатах» — проверка продолжается");
            // Счётчик групп кумулятивен по всем типам: точные + похожие фото + похожая музыка.
            long exactTotal = exactResult.GroupsCreated, imageTotal = 0;

            // === Этап 6: перцептивные признаки (опционально) ===
            // P0-A: считаем признаки только для включённых категорий (нет смысла на исключённых типах).
            bool wantImages = options.IncludedCategories.Contains(FileCategory.Image);
            bool wantAudio = options.IncludedCategories.Contains(FileCategory.Audio);
            if (options.Depth >= ScanDepth.WithSimilarImages && wantImages)
                ComputeImagePerceptual(mountById, scanScope, ct, sessionId);
            if (options.Depth >= ScanDepth.WithSimilarImagesAndAudio && _audio.IsAvailable && wantAudio)
                ComputeAudioPerceptual(mountById, scanScope, ct, sessionId);

            // === Этап 7: группировка похожих (точные уже построены движком) ===
            // P1.5: порог из профиля (если задан) переопределяет дефолтный.
            double imgThreshold = options.ImageProfile?.ImageThreshold ?? options.ImageThreshold;
            double audThreshold = options.AudioProfile?.AudioThreshold ?? options.AudioThreshold;
            // P1.5 (ревью): профиль применяем И на группировке — иначе файл с КЭШИРОВАННЫМ (из прошлого
            // скана) pHash/fp обошёл бы фильтр размера/даты/папки. Складываем профиль в предикат scope.
            Func<FileRecord, bool> imgInScope = options.ImageProfile is { } ip
                ? f => inScope(f) && SimilarityProfileService.Matches(f.Category, f.Size, f.Created, f.Mtime, f.RelPath, ip)
                : inScope;
            Func<FileRecord, bool> audInScope = options.AudioProfile is { } apr
                ? f => inScope(f) && SimilarityProfileService.Matches(f.Category, f.Size, f.Created, f.Mtime, f.RelPath, apr)
                : inScope;
            if (options.Depth >= ScanDepth.WithSimilarImages && wantImages)
            {
                SetStage(ScanStage.Grouping, sessionId);
                _grouper.BuildSimilarImageGroups(imgThreshold, imgInScope, _representativeRule,
                    g => { imageTotal = g; Report(p => p.GroupsFound = exactTotal + g); }, ct, sessionId,
                    () => WaitGate(ct), isPreferredKeep);
            }
            if (options.Depth >= ScanDepth.WithSimilarImagesAndAudio && wantAudio)
                _grouper.BuildSimilarAudioGroups(audThreshold, audInScope, _representativeRule,
                    g => Report(p => p.GroupsFound = exactTotal + imageTotal + g), ct, sessionId,
                    () => WaitGate(ct), isPreferredKeep);

            // Отметить тома как просканированные.
            foreach (var vid in targets.Select(t => t.VolumeId).Distinct())
                _volumes.MarkScanned(vid);

            // Сохранить состояние USN-журнала для быстрого следующего скана (после успешного индексирования).
            StoreJournalState(journalSnaps, optionsHash);

            SetStage(ScanStage.Completed, sessionId);
            _sessions.Finish(sessionId, ScanStage.Completed);
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // Отмена: убираем ЧАСТИЧНО построенные группы этой сессии, чтобы прерванный скан не
            // показывался как готовый результат, из которого можно удалять файлы (ревью P0.1).
            if (sessionId > 0)
            {
                CleanupSessionGroups(sessionId, partialOnly: true);
                SetStage(ScanStage.Cancelled, sessionId);
                try { _sessions.Finish(sessionId, ScanStage.Cancelled); }
                catch (Exception fex) { AppLog.Error("Finish(Cancelled)", fex); }
            }
        }
        catch (Exception ex)
        {
            if (sessionId > 0) CleanupSessionGroups(sessionId, partialOnly: true);
            Report(p => { p.Stage = ScanStage.Failed; p.Message = ex.Message; }, immediate: true);
            // Сама Finish — тоже запись в БД: если упало на Create (БД недоступна), не роняем повторно.
            if (sessionId > 0)
            {
                try { _sessions.Finish(sessionId, ScanStage.Failed); }
                catch (Exception fex) { AppLog.Error("Finish(Failed)", fex); }
            }
        }
        finally
        {
            // 2.8: скан закончился (любым исходом) — предпросмотр активной сессии больше не валиден.
            // Сбрасываем ДО финального Flush/IsRunning=false: UI, перечитывающий результаты по
            // терминальному событию, уже не увидит «идущей» сессии.
            _activeExactReady = false;
            Interlocked.Exchange(ref _activeSessionId, 0);

            // Область скана (scan_scope_files) нужна только ВО ВРЕМЯ скана (scoped-SQL перцептива и
            // воронки); строки старых сессий никто не читает и раньше никто не удалял — таблица росла
            // неограниченно, раздувая БД на регулярных сканах папок (аудит 2026-07-02).
            if (sessionId > 0)
            {
                try { _files.ClearScope(sessionId); }
                catch (Exception scex) { AppLog.Error("ClearScope(finally)", scex); }
            }
            // Группы прошлых сессий убираем ТОЛЬКО на успехе: тогда в результатах остаются лишь группы
            // текущего завершённого скана. На отмене/сбое прошлый ЗАВЕРШЁННЫЙ результат СОХРАНЯЕМ —
            // частичные группы текущей сессии уже сняты в catch (ClearGroupsInSession). Это и есть
            // «preserve previous completed results on cancel/fail»: ResultsViewModel всё равно показывает
            // последнюю успешную сессию (GetLatestCompletedSessionId), поэтому пользователь не теряет
            // прежние результаты из-за прерванного скана.
            if (succeeded) CleanupSessionGroups(sessionId, partialOnly: false);

            // Отдать последнее накопленное состояние и закрыть троттлинг до сброса флагов,
            // чтобы UI получил финальные счётчики, а поздних событий после завершения не было.
            if (savedPriority is { } sp)
            {
                try { using var proc = System.Diagnostics.Process.GetCurrentProcess(); proc.PriorityClass = sp; }
                catch { /* приоритет вернётся с завершением процесса */ }
            }

            _coalescer?.Flush();
            _coalescer?.Dispose();
            _coalescer = null;
            IsRunning = false;
            IsPaused = false;
            _gate.Set();
        }
    }

    /// <summary>
    /// Предикат «файл в папке-эталоне» для правила Smart (2.10). Строится один раз на скан из
    /// AppSettings.ReferenceFolders и точек монтирования. null — папок-эталонов нет (частый случай,
    /// нулевые накладные расходы). Нерезолвящийся путь даёт false (нет БОНУСА) — в отличие от
    /// защиты от удаления в UI (там неразобранный путь консервативно считается защищённым):
    /// здесь ошибка в мягкую сторону — файл просто не получает преимущества.
    /// </summary>
    private Func<FileRecord, bool>? BuildPreferredKeepPredicate(IReadOnlyDictionary<long, string> mountById)
    {
        var roots = _settings.Current.ReferenceFolders
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
                catch { return p.TrimEnd('\\', '/'); }
            })
            .ToArray();
        if (roots.Length == 0) return null;

        return f =>
        {
            if (!mountById.TryGetValue(f.VolumeId, out var mount)) return false;
            string full;
            try { full = Path.GetFullPath(Path.Combine(mount, f.RelPath)); }
            catch { return false; }
            foreach (var r in roots)
            {
                if (full.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        };
    }

    /// <summary>Подчистка групп. partialOnly=true — только текущая сессия; иначе — прошлые сессии.
    /// Обёрнуто в try/catch, чтобы сбой подчистки не маскировал исходную ошибку скана.</summary>
    private void CleanupSessionGroups(long sessionId, bool partialOnly)
    {
        try
        {
            if (partialOnly) _grouper.ClearGroupsInSession(sessionId);
            else _grouper.ClearGroupsNotInSession(sessionId);
        }
        catch (Exception ex) { AppLog.Error("CleanupSessionGroups", ex); }
    }

    // ---------------- Этап 1: обход ----------------

    private void Enumerate(List<ScanTarget> targets, Dictionary<long, string> mountById,
        CancellationToken ct, long sessionId, HashSet<long> skipVolumes,
        Dictionary<long, UsnDeltaPlanRequest> deltaPlans)
    {
        SetStage(ScanStage.Enumerating, sessionId);

        // P1.2: для ЧИСТОГО скана папок (без целых томов) заполняем область сессии — тогда воронка считает
        // кандидатов по ней, а не по всей старой БД. Для скана тома область = весь том, выгоды нет → не пишем.
        bool useScope = _options.Folders.Count > 0 && _options.VolumeIds.Count == 0;
        HashSet<long>? scopeIds = null;
        if (useScope)
        {
            try { _files.ClearScope(sessionId); scopeIds = new HashSet<long>(); }
            catch (Exception ex) { AppLog.Error("ClearScope", ex); }
        }

        // Накапливаем seen и решение о прунинге на ВЕСЬ том. Иначе при сканировании двух папок
        // одного диска (режим Folders) prune после второй папки удалял бы из индекса записи первой
        // (PruneVolume чистит весь том по seen текущей цели).
        foreach (var volumeGroup in targets.GroupBy(t => t.VolumeId))
        {
            long volumeId = volumeGroup.Key;
            if (!mountById.TryGetValue(volumeId, out var mountRoot)) continue;

            // Инкрементальность: том не менялся с прошлого скана (по USN-журналу) — не перечисляем
            // заново, существующий индекс актуален. Воронка ниже отработает по сохранённым данным.
            if (skipVolumes.Contains(volumeId))
            {
                AppLog.Write($"Том {volumeId}: без изменений по USN-журналу — перечисление пропущено.");
                continue;
            }

            // 3.1: том менялся МАЛО — точечный реконсайл по дельте USN-журнала вместо полного
            // перечисления. Успех → том дальше ведёт себя как Unchanged: seen/prune НЕ выполняются
            // (прунинг разрешён только при полном обходе), а в scan_scope_files том, как и
            // Unchanged-том, ничего не добавляет — воронка идёт по сохранённым в БД данным плюс
            // обновлённым дельтой строкам (предикат BuildScanScope целый том и так покрывает,
            // а режим Folders-скана с целым томом в PlanIncremental — тот же редкий случай, что и
            // у Unchanged; поведение области сознательно идентично skipVolumes). Неуспех дельты
            // (недоступна/аномалия) — честный полный пересбор тома ниже, причина уже в логе.
            if (deltaPlans.TryGetValue(volumeId, out var deltaPlan) &&
                TryApplyUsnDelta(volumeId, mountRoot, deltaPlan, sessionId, ct))
                continue;

            // Пер-томовое состояние индексирования (meta-пачки, пакетный lookup 2.7, seen) —
            // общий код обработки одного файла для перечисления и дельта-реконсайла.
            var indexer = new VolumeIndexer(this, volumeId, mountRoot, sessionId);
            bool wholeVolume = false;
            int enumErrors = 0;   // ошибки перечисления этого тома (недоступные поддеревья, отказ корня)
            long cloudSkipped = 0; // пропущено облачных/reparse-элементов (OneDrive-плейсхолдеры, симлинки)

            foreach (var target in volumeGroup)
            {
                // Полное сканирование тома (выбор носителя в UI) — можно прунинговать весь том.
                if (IsSameRoot(target.ScanRoot, mountRoot)) wholeVolume = true;

                int metaDop = _readPlans.TryGetValue(volumeId, out var rp)
                    ? rp.MetadataDop : Math.Max(1, _options.Parallelism);
                var listerOpts = new Fde.FastListerOptions
                {
                    StatParallelism = metaDop,
                    SkipHiddenSystem = _options.SkipHiddenSystem,    // не заходить в скрытые/системные папки (P1.5)
                    OnBackend = d => NoteBackend(d, sessionId),      // режим бэкенда по диску + диагностика
                    // Ошибки перечисления — это уведомления/недоступные ПАПКИ (целые поддеревья) и откат
                    // MFT, а не пропуск одного файла. Не накручиваем FilesSkipped (это счётчик файлов) —
                    // логируем и СЧИТАЕМ: при ошибках перечисления прунинг тома запрещён (B3).
                    OnError = e =>
                    {
                        Interlocked.Increment(ref enumErrors);
                        AppLog.Write($"Перечисление: {e.Path} — {e.Message}");
                    },
                    // Облачные плейсхолдеры (OneDrive) и симлинки не сканируем (не скачиваем из
                    // облака), но и не молчим об этом: раньше целая фото-библиотека в OneDrive
                    // невидимо выпадала из проверки (аудит 2026-07-02).
                    OnReparseSkipped = (_, _) => Interlocked.Increment(ref cloudSkipped)
                };
                foreach (var ef in _fastLister.Enumerate(target.ScanRoot, listerOpts, ct))
                {
                    WaitGate(ct);
                    try
                    {
                        indexer.Process(ef);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        Report(p => p.FilesSkipped++);
                    }
                }
            }

            indexer.FlushAll(); // хвост meta-пачки и буфера инкрементальных кандидатов (2.7)
            var seen = indexer.Seen;

            if (Interlocked.Read(ref cloudSkipped) > 0)
                NoteEnumerationNotice(
                    $"Пропущено облачных/ссылочных файлов: {cloudSkipped:N0} — содержимое в облаке не проверяется и не скачивается");

            // Носитель исчез во время перечисления (выдернули флешку/оборвалась сеть) — честно
            // валим скан: частичные результаты не должны публиковаться как успех и затирать
            // предыдущую полную сессию (catch в RunCore сохранит прежние группы). Прунинг ниже
            // до этой точки не дойдёт — офлайн-каталог тома («память дисков») останется цел (B3).
            if (!Directory.Exists(mountRoot))
                throw new IOException($"Носитель {mountRoot} отключился во время сканирования — результаты неполные, прежние данные сохранены.");

            // Прунинг — только при полном сканировании тома И когда выбраны ВСЕ категории: удаляем записи,
            // которых больше нет. Для папок и для фильтра по типам не прунингуем — иначе снесли бы индекс
            // невыбранных категорий (P0-A) или файлов вне папки.
            // И только при ЧИСТОМ перечислении: недоступные поддеревья дают частичный «seen»,
            // по которому прунинг снёс бы из каталога живые записи (B3, аудит 2026-07-02).
            if (wholeVolume && _options.AllCategoriesIncluded)
            {
                if (enumErrors == 0)
                {
                    try { _files.PruneVolume(volumeId, seen); }
                    catch (Exception ex) { AppLog.Error("PruneVolume", ex); }
                }
                else
                {
                    AppLog.Write($"Том {volumeId}: перечисление с ошибками ({enumErrors}) — прунинг каталога пропущен, записи не удаляются.");
                }
            }

            // P1.2: копим in-scope файлы тома; область запишем ОДНОЙ транзакцией после всех томов.
            if (scopeIds != null) scopeIds.UnionWith(seen);
        }

        // P1.2: единая запись области (всё-или-ничего). При любом сбое область очищается, и движок
        // безопасно идёт по ГЛОБАЛЬНОМУ запросу — надмножество безопасно, частичное подмножество нет.
        if (scopeIds is { Count: > 0 })
        {
            try { _files.AddScope(sessionId, scopeIds); }
            catch (Exception ex)
            {
                AppLog.Error("AddScope", ex);
                try { _files.ClearScope(sessionId); } catch { /* fallback на глобальный запрос */ }
            }
        }
    }

    /// <summary>
    /// Пер-томовое состояние этапа индексирования: meta-пачки, пакетный lookup инкрементальных
    /// кандидатов (2.7) и набор seen для прунинга/области. Вынесено из Enumerate, чтобы обработка
    /// одного файла была ОБЩИМ кодом обычного перечисления и дельта-реконсайла USN (3.1): фильтры,
    /// категории и строгое переиспользование метаданных не могут разойтись между путями.
    /// </summary>
    private sealed class VolumeIndexer
    {
        private readonly ScanService _s;
        private readonly long _volumeId;
        private readonly string _mountRoot;
        private readonly long _sessionId;
        private readonly List<FileRecord> _batch = new(MetaBatch);
        // 2.7: вместо загрузки ВСЕГО индекса тома в словарь (250–500 МБ на 1–2 млн файлов) —
        // пакетный lookup: кандидаты копятся в буфер, и одним SELECT ... rel_path IN (...) на
        // пачку подтягиваются существующие метаданные (FlushLookup ниже).
        private readonly List<(FileRecord Record, string FullPath)> _lookupBuffer = new(LookupBatch);

        /// <summary>Id встреченных строк (заполняется на флашах) — для прунинга и области скана.</summary>
        public HashSet<long> Seen { get; } = new();

        public VolumeIndexer(ScanService s, long volumeId, string mountRoot, long sessionId)
        {
            _s = s;
            _volumeId = volumeId;
            _mountRoot = mountRoot;
            _sessionId = sessionId;
        }

        /// <summary>
        /// Обработка одного файла перечисления/дельты. true — файл прошёл фильтры и остаётся в
        /// индексе (записан или строго переиспользован); false — отфильтрован (системная папка,
        /// атрибуты/размер/расширение, невыбранная категория) и в индексе ему не место.
        /// </summary>
        public bool Process(Fde.EnumeratedFile ef)
        {
            // Системные папки и skip-файлы раньше отсекал EnumerateFilesSafe/SkipDir/SkipFile.
            // В USN-режиме движок выдаёт плоский список всего тома — $Recycle.Bin и т.п. отсекаем тут.
            var relForSkip = Path.GetRelativePath(_mountRoot, ef.FullPath);
            if (IsUnderSystemFolder(relForSkip)) return false;
            if (_s.SkipFileByAttrs(ef.Attributes, ef.Size, ef.FullPath)) return false;

            var fi = new FileEntry(ef.FullPath, ef.Size,
                new DateTimeOffset(DateTime.FromFileTimeUtc(ef.LastWriteRaw), TimeSpan.Zero),
                new DateTimeOffset(DateTime.FromFileTimeUtc(ef.CreationRaw), TimeSpan.Zero));

            var cleanFull = fi.FullClean;
            var rel = Path.GetRelativePath(_mountRoot, cleanFull);
            var ext = Path.GetExtension(cleanFull);
            var category = _s.DetectCategory(cleanFull, ext);

            // P0-A «Что искать»: файлы невыбранных категорий не индексируем —
            // exact/похожие будут строиться только по выбранным типам.
            if (!_s._options.IncludedCategories.Contains(category))
            {
                _s.Report(p => p.FilesSkipped++);
                return false;
            }

            var record = new FileRecord
            {
                VolumeId = _volumeId,
                RelPath = rel,
                Name = Path.GetFileName(cleanFull),
                Ext = string.IsNullOrEmpty(ext) ? null : ext,
                Size = fi.Length,
                Mtime = fi.LastWrite,
                Created = fi.Created,
                Category = category,
                ScanState = ScanState.Meta,
                LastIndexed = DateTimeOffset.Now
            };

            // Инкрементальность: решение о переиспользовании строки принимается ПАЧКОЙ
            // (2.7, см. FlushLookup ниже) — кандидат копится в буфер, существующие
            // метаданные подтягиваются одним SELECT на пачку, весь индекс тома в память
            // не грузится. Критерий строгого совпадения не менялся.
            if (_s._options.Incremental)
            {
                _lookupBuffer.Add((record, cleanFull));
                if (_lookupBuffer.Count >= LookupBatch) FlushLookup();
                return true;
            }

            _batch.Add(record);
            _s.Report(p => { p.FilesSeen++; p.CurrentFile = cleanFull; });

            if (_batch.Count >= MetaBatch) FlushMetaBatch();
            return true;
        }

        /// <summary>Хвост буферов в конце тома (семантика прежнего кода Enumerate сохранена).</summary>
        public void FlushAll()
        {
            FlushLookup(); // 2.7: хвост буфера инкрементальных кандидатов (< LookupBatch)
            if (_batch.Count > 0)
            {
                _s._files.UpsertMetaBatch(_batch);
                foreach (var b in _batch) Seen.Add(b.Id);
                _batch.Clear();
            }
        }

        // Общий сброс meta-пачки (семантика прежняя: Upsert → seen → прогресс сессии).
        private void FlushMetaBatch()
        {
            _s._files.UpsertMetaBatch(_batch);
            foreach (var b in _batch) Seen.Add(b.Id);
            _batch.Clear();
            var snap = _s.Snapshot();
            _s._sessions.UpdateProgress(_sessionId, ScanStage.Enumerating, snap.FilesSeen, snap.FilesSkipped);
        }

        // 2.7: решает судьбу накопленных инкрементальных кандидатов ПАЧКОЙ. Логика та же, что
        // была по-файлово: строгая тройная сверка size+mtime+created (точно, без допуска ±2с —
        // файл могли переписать содержимым того же размера, а sync/backup-инструмент сохранить
        // близкий timestamp, раздел P0) → строка переиспользуется БЕЗ перехэша (seen.Add, хэши
        // и verified_full_hash сохранены). Любое расхождение или новая строка → UpsertMetaBatch,
        // где хэши и verified_full_hash сбрасываются. existing.Created == default — строка из
        // старой БД до миграции «created» (значение не сохранялось): тогда сверяем только
        // size+mtime, чтобы не гнать ОДНОРАЗОВЫЙ полный перехэш всей базы после обновления.
        private void FlushLookup()
        {
            if (_lookupBuffer.Count == 0) return;
            var existingByPath = _s._files.GetExistingMetaByPaths(_volumeId,
                _lookupBuffer.Select(t => t.Record.RelPath).ToList());
            foreach (var (record, fullPath) in _lookupBuffer)
            {
                if (existingByPath.TryGetValue(record.RelPath, out var existing) &&
                    existing.Size == record.Size &&
                    existing.Mtime == record.Mtime &&
                    (existing.Created == default || existing.Created == record.Created))
                {
                    Seen.Add(existing.Id);
                    _s.Report(p => p.FilesSeen++);
                    continue; // оставляем существующую запись с её хэшами
                }

                _batch.Add(record);
                _s.Report(p => { p.FilesSeen++; p.CurrentFile = fullPath; });
                if (_batch.Count >= MetaBatch) FlushMetaBatch();
            }
            _lookupBuffer.Clear();
        }
    }

    /// <summary>Совпадает ли корень сканирования с корнем тома (полное сканирование тома).</summary>
    private static bool IsSameRoot(string scanRoot, string mountRoot)
    {
        try
        {
            var a = Path.GetFullPath(scanRoot).TrimEnd('\\', '/');
            var b = Path.GetFullPath(mountRoot).TrimEnd('\\', '/');
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Режим бэкенда по каждому диску: честное сообщение (для НЕ-NTFS НЕ предлагаем админа) + строка
    /// диагностики в лог. Сообщения по дискам НАКАПЛИВАЮТСЯ — один диск не затирает другой.
    /// </summary>
    private void NoteBackend(Fde.BackendDecision d, long sessionId)
    {
        // Диагностика (раздел Diagnostics): по этой строке в логе видно, какой бэкенд реально выбран.
        AppLog.Write($"Backend ScanSessionId={sessionId} Root={d.Root} VolumeRoot={d.VolumeRoot} " +
                     $"FileSystem={d.FileSystem} DriveType={d.DriveType} Backend={d.Backend} " +
                     $"Reason={d.Reason} IsAdmin={d.IsElevated}");

        var line = Fde.BackendMessages.BuildUserNotice(d);
        if (!_enumNotices.Contains(line)) _enumNotices.Add(line);
        Report(p => p.Message = string.Join("  ·  ", _enumNotices));
    }

    /// <summary>Добавить пользовательское уведомление этапа перечисления (показывается на карточке скана).</summary>
    private void NoteEnumerationNotice(string line)
    {
        AppLog.Write(line);
        if (!_enumNotices.Contains(line)) _enumNotices.Add(line);
        Report(p => p.Message = string.Join("  ·  ", _enumNotices));
    }

    // ---------------- Инкрементальность (USN-журнал) ----------------

    /// <summary>Хэш фильтров скана: при их изменении переиспользовать индекс нельзя — полный пересбор.</summary>
    private static string OptionsHash(ScanOptions o)
    {
        var exts = string.Join(",", o.ExcludedExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        // Категории — тоже фильтр «что индексировать»: их смена должна форсировать полный пересбор
        // (иначе на неизменившемся томе вновь включённая категория не проиндексировалась бы).
        var cats = string.Join(",", o.IncludedCategories.Select(c => (int)c).OrderBy(x => x));
        var s = $"{o.MinFileSize}|{o.MaxFileSize}|{o.SkipHiddenSystem}|{(int)o.Depth}|{exts}|{cats}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(hash);
    }

    /// <summary>Заявка на дельта-реконсайл тома: журнал и диапазон [StartUsn; StopUsn) для чтения.</summary>
    private readonly record struct UsnDeltaPlanRequest(ulong JournalId, long StartUsn, long StopUsn);

    /// <summary>
    /// Решает, какие тома можно НЕ перечислять (без изменений по USN-журналу) или обновить ТОЧЕЧНО
    /// по дельте журнала (3.1), и возвращает снимки журнала для последующего сохранения. Только
    /// целый том NTFS под админом попадает в план; иначе журнал недоступен и том сканируется
    /// полностью. Любая неоднозначность → полный пересбор.
    /// </summary>
    private (HashSet<long> skip, Dictionary<long, Fde.UsnJournalSnapshot> snaps,
        Dictionary<long, UsnDeltaPlanRequest> deltas) PlanIncremental(
        List<ScanTarget> targets, Dictionary<long, VolumeRecord> volumesById, ScanOptions options, string optionsHash)
    {
        var skip = new HashSet<long>();
        var snaps = new Dictionary<long, Fde.UsnJournalSnapshot>();
        var deltas = new Dictionary<long, UsnDeltaPlanRequest>();
        if (!options.Incremental || !OperatingSystem.IsWindows()) return (skip, snaps, deltas);

        foreach (var grp in targets.GroupBy(t => t.VolumeId))
        {
            long vid = grp.Key;
            if (grp.Count() != 1) continue;                                   // несколько целей на томе — не усложняем
            if (!volumesById.TryGetValue(vid, out var vol) || vol.MountPath is null) continue;
            if (!IsSameRoot(grp.First().ScanRoot, vol.MountPath)) continue;   // не целый том (папка) — без журнала

            if (!Fde.UsnJournalReader.TryQuery(vol.MountPath, out var snap)) continue; // нет прав/не NTFS/нет журнала
            snaps[vid] = snap;

            var saved = _scanState.Get(vid);
            var decision = IncrementalScanPlanner.Decide(
                saved, journalAvailable: true, unchecked((long)snap.JournalId), snap.FirstUsn, snap.NextUsn,
                IncrementalScanPlanner.EngineVersion, optionsHash);
            AppLog.Write($"Инкрементальность тома «{vol.DisplayName}»: {decision.Kind} — {decision.Reason}.");
            if (decision.Kind == ScanPlanKind.Unchanged)
            {
                skip.Add(vid);
            }
            else if (decision.Kind == ScanPlanKind.Incremental)
            {
                // 3.1: точечный реконсайл. Дельта читается с СОХРАНЁННОГО NextUsn (водяной знак
                // прошлого скана, Decide гарантирует saved != null) до NextUsn текущего снимка;
                // применение — в Enumerate/TryApplyUsnDelta, любая аномалия там → полный пересбор.
                deltas[vid] = new UsnDeltaPlanRequest(snap.JournalId, saved!.NextUsn, snap.NextUsn);
            }
        }
        return (skip, snaps, deltas);
    }

    /// <summary>
    /// 3.1: точечный реконсайл тома по дельте USN-журнала вместо полного перечисления (сотня
    /// изменений на миллионе файлов: минуты обхода → секунды). true — дельта применена, том дальше
    /// ведёт себя как Unchanged (prune/seen/область не трогаются); false — дельта недоступна или
    /// аномалия (причина в логе) → вызывающий делает полный пересбор тома.
    ///
    /// Гарантия «дельта не теряет файлы»: любые сомнения (усечение журнала, записи не-V2, запись
    /// без имени, каталожные rename/delete, невосстановимый путь, кап 100k записей, отключение
    /// носителя, любой сбой применения) → false и полный пересбор. Уже применённые к БД изменения
    /// при этом безопасны: удалялись только строки путей, изменение которых журнал зафиксировал
    /// достоверно, а полный пересбор перестраивает строки заново (upsert).
    /// </summary>
    private bool TryApplyUsnDelta(long volumeId, string mountRoot, UsnDeltaPlanRequest req,
        long sessionId, CancellationToken ct)
    {
        Fde.UsnDeltaResult delta;
        try
        {
            delta = Fde.UsnDeltaReader.ReadResolved(mountRoot, req.JournalId, req.StartUsn, req.StopUsn);
        }
        catch (Exception ex)
        {
            AppLog.Error("UsnDelta.Read", ex); // ридер guarded, но пояс и подтяжки
            return false;
        }
        if (!delta.Available)
        {
            AppLog.Write($"Том {volumeId} ({mountRoot}): дельта USN недоступна — {delta.UnavailableReason}; полный пересбор.");
            return false;
        }

        var plan = UsnDeltaPlanner.Build(delta.Changes);
        if (plan.AnomalyReason is not null)
        {
            AppLog.Write($"Том {volumeId} ({mountRoot}): аномалия дельты USN — {plan.AnomalyReason}; полный пересбор.");
            return false;
        }

        // Общий с обычным перечислением путь индексирования (фильтры/категории/строгое
        // переиспользование 2.7 → UpsertMetaBatch, где хэши и verified_full_hash сбрасываются).
        var indexer = new VolumeIndexer(this, volumeId, mountRoot, sessionId);
        var hooks = new UsnDeltaApplyHooks
        {
            Stat = Fde.FileStatReader.TryGet,
            TryIndexFile = ef =>
            {
                WaitGate(ct); // пауза/отмена действуют и на дельте
                // Каталоги и reparse-точки обычное перечисление отфильтровывает ещё в движке
                // (FastFileLister) — здесь та же граница, чтобы не индексировать их через дельту.
                const uint DIRECTORY = 0x10, REPARSE_POINT = 0x400;
                if ((ef.Attributes & (DIRECTORY | REPARSE_POINT)) != 0) return false;
                return indexer.Process(ef);
            },
            DeleteByRelPath = rel => _files.DeleteByVolumeAndPath(volumeId, rel),
            VolumeAlive = () => Directory.Exists(mountRoot)
        };

        var outcome = UsnDeltaExecutor.Apply(plan, mountRoot, hooks);
        if (!outcome.Success)
        {
            AppLog.Write($"Том {volumeId} ({mountRoot}): применение дельты USN прервано — {outcome.AnomalyReason}; полный пересбор.");
            return false;
        }

        indexer.FlushAll();
        NoteEnumerationNotice(
            $"Быстрое обновление по журналу USN ({mountRoot}): изменений {outcome.Reindexed}, удалений {outcome.Deleted}");
        return true;
    }

    /// <summary>
    /// Сохранить снимок USN-журнала после успешного индексирования (для быстрого след. скана).
    /// Намеренно храним NextUsn НА МОМЕНТ НАЧАЛА скана (нижняя граница): перечисление — это снимок
    /// тома примерно на старте, а любое изменение во время/после скана продвигает журнал выше этого
    /// значения, поэтому следующий скан увидит USN &gt; сохранённого и сделает пересбор (не пропустит
    /// изменение). Хранить NextUsn конца скана было бы ОПАСНО: изменения, случившиеся во время обхода
    /// и не попавшие в индекс, оказались бы «ниже водяного знака» и были бы пропущены как «без изменений».
    /// Для дельта-реконсайла (3.1) этот же snap.NextUsn — корректный НОВЫЙ водяной знак: дельта
    /// читалась и применялась ровно до него (StopUsn), а изменения во время скана лежат выше и
    /// будут дочитаны следующей дельтой (повторное применение идемпотентно — restat смотрит на диск).
    /// </summary>
    private void StoreJournalState(Dictionary<long, Fde.UsnJournalSnapshot> snaps, string optionsHash)
    {
        foreach (var (vid, snap) in snaps)
        {
            try
            {
                _scanState.Upsert(new VolumeScanState
                {
                    VolumeId = vid,
                    JournalId = unchecked((long)snap.JournalId),
                    NextUsn = snap.NextUsn,
                    IndexedAt = DateTimeOffset.Now,
                    EngineVersion = IncrementalScanPlanner.EngineVersion,
                    OptionsHash = optionsHash
                });
            }
            catch (Exception ex) { AppLog.Error("StoreJournalState", ex); }
        }
    }

    private FileCategory DetectCategory(string fullPath, string? ext)
    {
        var categoryFromExt = CategoryDetector.FromExt(ext);
        if (categoryFromExt != FileCategory.Other && !NeedsMagicProbe(ext))
            return categoryFromExt;

        try
        {
            using var fs = new FileStream(LongPath.Fix(fullPath), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[CategoryDetector.ProbeBytes];
            int read = 0;
            while (read < head.Length)
            {
                int n = fs.Read(head.Slice(read));
                if (n == 0) break;
                read += n;
            }
            return CategoryDetector.Detect(head.Slice(0, read), ext);
        }
        catch
        {
            return CategoryDetector.FromExt(ext);
        }
    }

    // ---------------- Этапы 2–4: хэш-воронка ----------------

    private static bool NeedsMagicProbe(string? ext)
        => string.IsNullOrWhiteSpace(ext) || MagicProbeExtensions.Contains(ext);

    /// <summary>
    /// Адаптер прогресса/управления для <see cref="IExactDuplicateEngine"/>. Троттлинг (через
    /// <c>Report</c>/coalescer), сессии и пауза/отмена остаются здесь — движок только дёргает методы.
    /// Вложенный класс имеет доступ к приватным членам <see cref="ScanService"/>.
    /// </summary>
    private sealed class ScanReporter : IExactScanReporter
    {
        private readonly ScanService _s;
        private readonly long _sessionId;
        private readonly CancellationToken _ct;

        public ScanReporter(ScanService s, long sessionId, CancellationToken ct)
        {
            _s = s;
            _sessionId = sessionId;
            _ct = ct;
        }

        public void WaitGate() => _s.WaitGate(_ct);
        public void Stage(ScanStage stage) => _s.SetStage(stage, _sessionId);
        public void StageQuiet(ScanStage stage) => _s.SetStageQuiet(stage);
        public void FileHashed(string path, long bytes) =>
            _s.Report(p => { p.FilesHashed++; p.BytesProcessed += bytes; p.CurrentFile = path; });
        public void Skipped() => _s.Report(p => p.FilesSkipped++);
        public void ChangedSkipped() => _s.Report(p => { p.FilesSkipped++; p.FilesChangedSkipped++; });
        public void Fraction(double value) => _s.Report(p => p.Fraction = value);
        public void ExactGroups(long count) => _s.Report(p => p.GroupsFound = count);
        public void SessionTick()
        {
            var snap = _s.Snapshot();
            _s._sessions.UpdateProgress(_sessionId, snap.Stage, snap.FilesSeen, snap.FilesSkipped);
        }
    }

    // ---------------- Этап 6: перцептивные признаки ----------------

    private void ComputeImagePerceptual(Dictionary<long, string> mountById, ScanScope scanScope,
        CancellationToken ct, long sessionId)
    {
        // 2.3: байт-идентичные копии не декодируем повторно — pHash/pixel-hash переносится
        // SQL-запросом с любого файла с тем же full_hash (прошлые сканы, другая копия).
        // Библиотека с 50% точных копий проходит перцептив ~2x быстрее без чтения диска.
        try
        {
            int propagated = _files.PropagatePerceptualByFullHash(FileCategory.Image);
            if (propagated > 0)
                AppLog.Write($"Похожие фото: признаков перенесено с байт-идентичных копий: {propagated} (без декодирования).");
        }
        catch (Exception ex) { AppLog.Error("PropagatePerceptual(Image)", ex); }

        long total = _files.CountNeedingPerceptual(FileCategory.Image, sessionId);
        if (total == 0) return; // нет работы — не мигаем этапом «Анализ похожих»
        SetStage(ScanStage.Perceptual, sessionId);

        // P1.5: профиль похожести — предфильтр по размеру/дате/папке ДО расчёта pHash (экономим работу).
        var profile = _options.ImageProfile;
        long skippedByProfile = 0, computed = 0;

        // 2.6: EWMA скорости чтения по томам — состояние ТОЛЬКО этого этапа (между сканами не
        // живёт). Продюсеры конвейера меряют каждое чтение; клэмп применяется к DOP чтения
        // СЛЕДУЮЩЕЙ страницы (ReadDopFor вызывается исполнителем на старте каждой страницы).
        var throughput = new System.Collections.Concurrent.ConcurrentDictionary<long, ThroughputEwma>();
        int ClampedContentDop(long vid)
        {
            _readPlans.TryGetValue(vid, out var plan);
            int baseDop = plan?.ContentDop ?? Math.Max(1, _options.Parallelism);
            double mbps = throughput.TryGetValue(vid, out var ewma) ? ewma.Value : 0.0;
            int dop = GovernorDecisions.ClampDop(baseDop, mbps, plan?.PreferSequential ?? false);
            if (dop != baseDop)
                AppLog.Write($"Губернатор: том «{plan?.Label ?? vid.ToString()}» читается ~{mbps:0} МБ/с " +
                             $"(USB2/сеть?) — DOP чтения снижен {baseDop}→{dop}.");
            return dop;
        }

        // Постранично (keyset по id): не держим всю библиотеку изображений в памяти.
        long processedImages = 0, afterId = 0;
        while (true)
        {
            WaitGate(ct);
            var page = _files.GetNeedingPerceptualPage(FileCategory.Image, afterId, HashBatch, sessionId);
            if (page.Count == 0) break;
            afterId = page[^1].Id;

            var chunk = page.Where(f => mountById.ContainsKey(f.VolumeId) && scanScope.Contains(f)).ToList();
            if (profile is not null)
            {
                int before = chunk.Count;
                chunk = chunk.Where(f => SimilarityProfileService.Matches(
                    f.Category, f.Size, f.Created, f.Mtime, f.RelPath, profile)).ToList();
                skippedByProfile += before - chunk.Count;
            }
            // Строки со СТАРЫМ (v1) pHash до пересчёта: если новый расчёт не удастся (иконка меньше
            // 64×64 после префильтра, битый файл, cloud-заглушка), устаревшее значение надо СБРОСИТЬ —
            // группировка версию не фильтрует, и v1 вечно мешался бы с v2 у порога Хэмминга.
            var hadStalePHash = chunk
                .Where(f => f.PHash is not null && f.PHashVer < ImageMatchService.CurrentPHashVersion)
                .Select(f => f.Id).ToHashSet();
            // 2.4: конвейер чтение→декод вместо «чтение и декод в одном воркере». Продюсеры
            // читают файл целиком в память по плану тома (HDD — последовательно, диск не ждёт
            // декодера), декодеры считают pHash/pixel-hash из памяти на всех ядрах. Бюджет
            // канала и число декодеров — от губернатора (2.6), пересчёт на старте КАЖДОЙ
            // страницы: при высокой загрузке памяти канал сжимается до 32 МБ, декодеров — 2.
            var budget = ResourceGovernor.CurrentBudget();
            ImagePerceptualPipeline.Run(chunk, _images, new ImagePipelineOptions
            {
                PathFor = f => FullPath(f, mountById),
                ReadDopFor = ClampedContentDop,
                Budget = budget,
                MaxVolumeParallelism = _maxVolumeParallelism,
                ComputePixelHash = _computePixelHash,
                WaitGate = WaitGate,
                OnComputed = (f, path) =>
                {
                    if (f.PHash is not null)
                    {
                        // Пересчитанный pHash — всегда текущей версии алгоритма (этап 2.2).
                        f.PHashVer = ImageMatchService.CurrentPHashVersion;
                        if (f.ScanState < ScanState.Perceptual) f.ScanState = ScanState.Perceptual;
                    }
                    Report(p => { p.FilesHashed++; p.BytesProcessed += f.Size; p.CurrentFile = path; });
                },
                OnSkipped = () => Report(p => p.FilesSkipped++),
                OnReadMeasured = (vid, bytes, seconds) =>
                    throughput.GetOrAdd(vid, static _ => new ThroughputEwma()).Update(bytes, seconds)
            }, ct);
            // Пишем успешные расчёты и сбросы устаревших v1-хэшей (у них f.PHash обнулён расчётом).
            var done = chunk.Where(f => f.PHash is not null || hadStalePHash.Contains(f.Id)).ToList();
            _files.UpdateComputedBatch(done);
            computed += done.Count(f => f.PHash is not null);

            processedImages += page.Count;
            Report(p => p.Fraction = Math.Min(1.0, processedImages / (double)total));
        }

        // Debug-счётчики (раздел P1.5: «почему похожие не нашлись»).
        AppLog.Write($"Похожие фото: кандидатов {total}, отфильтровано профилем {skippedByProfile}, " +
                     $"посчитано pHash {computed}, порог {(profile?.ImageThreshold ?? _options.ImageThreshold):0.00}");
    }

    private void ComputeAudioPerceptual(Dictionary<long, string> mountById, ScanScope scanScope,
        CancellationToken ct, long sessionId)
    {
        // 2.3: отпечаток переносится с байт-идентичной копии — fpcalc не запускается повторно.
        try
        {
            int propagated = _files.PropagatePerceptualByFullHash(FileCategory.Audio);
            if (propagated > 0)
                AppLog.Write($"Похожая музыка: отпечатков перенесено с байт-идентичных копий: {propagated}.");
        }
        catch (Exception ex) { AppLog.Error("PropagatePerceptual(Audio)", ex); }

        long total = _files.CountNeedingPerceptual(FileCategory.Audio, sessionId);
        if (total == 0) return; // нет работы — не мигаем этапом «Анализ похожих»
        SetStage(ScanStage.Perceptual, sessionId);

        var profile = _options.AudioProfile;
        long skippedByProfile = 0, computed = 0;

        // Постранично (keyset по id): не держим всю фонотеку в памяти.
        long processedAudio = 0, afterId = 0;
        while (true)
        {
            WaitGate(ct);
            var page = _files.GetNeedingPerceptualPage(FileCategory.Audio, afterId, HashBatch, sessionId);
            if (page.Count == 0) break;
            afterId = page[^1].Id;

            // fpcalc — внешний процесс; параллелизм держим умеренным (последовательно по chunk).
            foreach (var f in page)
            {
                WaitGate(ct);
                if (!mountById.ContainsKey(f.VolumeId) || !scanScope.Contains(f)) continue;
                // P1.5: профиль аудио — предфильтр по размеру/дате/папке до дорогого fpcalc.
                if (profile is not null &&
                    !SimilarityProfileService.Matches(f.Category, f.Size, f.Created, f.Mtime, f.RelPath, profile))
                { skippedByProfile++; continue; }
                var path = FullPath(f, mountById);
                if (path is null) continue;
                f.AudioFp = _audio.ComputeFingerprint(path, ct);
                if (f.AudioFp is not null) { f.ScanState = ScanState.Perceptual; computed++; }
                Report(p => { p.FilesHashed++; p.CurrentFile = path; });
            }
            _files.UpdateComputedBatch(page.Where(f => f.AudioFp is not null).ToList());

            processedAudio += page.Count;
            Report(p => p.Fraction = Math.Min(1.0, processedAudio / (double)total));
        }

        AppLog.Write($"Похожая музыка: кандидатов {total}, отфильтровано профилем {skippedByProfile}, " +
                     $"посчитано отпечатков {computed}, порог {(profile?.AudioThreshold ?? _options.AudioThreshold):0.00}");
    }

    // ---------------- Вспомогательное ----------------

    private static string? FullPath(FileRecord f, Dictionary<long, string> mountById) =>
        mountById.TryGetValue(f.VolumeId, out var mp) ? Path.Combine(mp, f.RelPath) : null;

    private static string NormalizeRelPath(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath) || relPath == ".") return string.Empty;
        return relPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
    }

    private void WaitGate(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _gate.Wait(ct);
    }

    private void SetStage(ScanStage stage, long sessionId)
    {
        // Смена этапа — немедленно (минуя троттлинг): пользователь сразу видит «Полный хэш» и т.п.
        Report(p => { p.Stage = stage; p.Fraction = null; }, immediate: true);
        var snap = Snapshot();
        _sessions.UpdateProgress(sessionId, stage, snap.FilesSeen, snap.FilesSkipped);
    }

    /// <summary>Снимок прогресса под локом — для согласованного чтения счётчиков из фонового потока.</summary>
    private ScanProgress Snapshot()
    {
        lock (_progressLock) return Progress.Clone();
    }

    public void Dispose()
    {
        _coalescer?.Dispose();
        _gate.Dispose();
    }

    private void SetStageQuiet(ScanStage stage)
    {
        // Внутри воронки этот метод зовётся часто; реально отдаём событие только при смене этапа.
        bool changed;
        lock (_progressLock) changed = Progress.Stage != stage;
        if (changed) Report(p => p.Stage = stage, immediate: true);
    }

    private void Report(Action<ScanProgress> mutate, bool immediate = false)
    {
        ScanProgress snapshot;
        lock (_progressLock)
        {
            mutate(Progress);
            snapshot = Progress.Clone();
        }
        var coalescer = _coalescer;
        if (coalescer is null) ProgressChanged?.Invoke(snapshot); // вне скана (на всякий случай)
        else coalescer.Report(snapshot, immediate);
    }

    // ---------------- Цели сканирования ----------------

    private sealed record ScanTarget(long VolumeId, string ScanRoot);

    private sealed class ScanScope
    {
        private readonly HashSet<long> _wholeVolumes = new();
        private readonly Dictionary<long, List<string>> _folderRelPaths = new();

        public void AddWholeVolume(long volumeId) => _wholeVolumes.Add(volumeId);

        public void AddFolder(long volumeId, string relPath)
        {
            if (!_folderRelPaths.TryGetValue(volumeId, out var list))
            {
                list = new List<string>();
                _folderRelPaths[volumeId] = list;
            }

            list.Add(NormalizeRelPath(relPath));
        }

        public bool Contains(FileRecord file)
        {
            if (_wholeVolumes.Contains(file.VolumeId)) return true;
            if (!_folderRelPaths.TryGetValue(file.VolumeId, out var folders)) return false;

            var relPath = NormalizeRelPath(file.RelPath);
            return folders.Any(folder =>
                string.IsNullOrEmpty(folder) ||
                string.Equals(relPath, folder, StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static ScanScope BuildScanScope(List<ScanTarget> targets, Dictionary<long, string> mountById)
    {
        var scope = new ScanScope();
        foreach (var target in targets)
        {
            if (!mountById.TryGetValue(target.VolumeId, out var mountRoot)) continue;

            if (IsSameRoot(target.ScanRoot, mountRoot))
            {
                scope.AddWholeVolume(target.VolumeId);
                continue;
            }

            var rel = Path.GetRelativePath(mountRoot, target.ScanRoot);
            if (rel == ".") scope.AddWholeVolume(target.VolumeId);
            else scope.AddFolder(target.VolumeId, rel);
        }

        return scope;
    }

    private List<ScanTarget> ResolveTargets(ScanOptions options, List<VolumeRecord> catalog)
    {
        var byId = catalog.ToDictionary(v => v.Id);
        var targets = new List<ScanTarget>();

        foreach (var vid in options.VolumeIds)
            if (byId.TryGetValue(vid, out var v) && v.IsConnected && v.MountPath is not null)
                targets.Add(new ScanTarget(vid, v.MountPath));

        foreach (var folder in options.Folders)
        {
            var root = Path.GetPathRoot(folder);
            if (root is null) continue;
            var vol = catalog.FirstOrDefault(v =>
                v.IsConnected && v.MountPath is not null &&
                string.Equals(Path.GetPathRoot(v.MountPath), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase));
            if (vol is not null)
                targets.Add(new ScanTarget(vol.Id, folder));
        }

        // NFR-7: план чтения на каждый том. HDD/съёмные — последовательно, SSD/NVMe — параллельно.
        // Раньше один HDD среди целей делал весь скан однопоточным и убивал SSD/NVMe.
        _readPlans = VolumeReadPlanner.Build(
            targets.Select(t => t.VolumeId), byId,
            _settings.Current.EcoMode ? 1 : Math.Max(1, options.Parallelism));
        _maxVolumeParallelism = Math.Max(1, _readPlans.Count);
        foreach (var p in _readPlans.Values)
            AppLog.Write($"План чтения тома «{p.Label}»: {(p.PreferSequential ? "последовательно" : "параллельно")}, " +
                         $"метаданные DOP={p.MetadataDop}, содержимое DOP={p.ContentDop}");
        return targets;
    }

    // Порт старых SkipFile/SystemFolders на атрибуты (USN/GetFileAttributesEx дают uint attrs, а не FileInfo).
    private bool SkipFileByAttrs(uint attrs, long size, string path)
    {
        const uint HIDDEN = 0x2, SYSTEM = 0x4;
        if (_options.SkipHiddenSystem && (attrs & (HIDDEN | SYSTEM)) != 0) return true;
        if (size < _options.MinFileSize) return true;
        if (_options.MaxFileSize is long max && max > 0 && size > max) return true;
        if (_options.ExcludedExtensions.Count > 0 &&
            _options.ExcludedExtensions.Contains(Path.GetExtension(path))) return true;
        return false;
    }

    /// <summary>Файл лежит в системной папке ($Recycle.Bin, System Volume Information и т.п.) — пропустить.</summary>
    private static bool IsUnderSystemFolder(string relPath)
    {
        foreach (var seg in relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (seg.Length > 0 && SystemFolders.Contains(seg)) return true;
        return false;
    }

    private readonly record struct FileEntry(string FullClean, long Length, DateTimeOffset LastWrite, DateTimeOffset Created);
}
