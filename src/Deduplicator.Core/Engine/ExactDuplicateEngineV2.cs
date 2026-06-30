using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Engine;

/// <summary>
/// Потоковая воронка точных дубликатов (раздел 3.2 аудита):
///   потоковые size-кандидаты (один запрос, без N+1) → частичный хэш → полный хэш только для
///   коллизий частичного → точные группы с обязательной побайтовой сверкой подключённых файлов.
///
/// Метаданные уже должны быть в БД (этап перечисления в <c>ScanService</c>). Движок только читает
/// содержимое там, где есть шанс на дубль: уникальные размеры не читаются вовсе.
/// </summary>
public sealed class ExactDuplicateEngineV2 : IExactDuplicateEngine
{
    private readonly FileRepository _files;
    private readonly IHashService _hash;
    private readonly IDuplicateGrouper _grouper;

    public ExactDuplicateEngineV2(FileRepository files, IHashService hash, IDuplicateGrouper grouper)
    {
        _files = files;
        _hash = hash;
        _grouper = grouper;
    }

    // Сколько размеров-кандидатов выбирать одним запросом. Не N+1 (на каждый размер), но и не
    // один курсор на весь скан (тот держал бы WAL read-mark открытым и раздувал WAL на гигабайты).
    private const int SizeBatch = 200;

    public ExactScanResult ScanExact(ExactScanRequest req, IExactScanReporter rep, CancellationToken ct)
    {
        var result = new ExactScanResult();
        rep.Stage(ScanStage.PartialHashing);

        // P1.2: для скана папки кандидаты считаются по области сессии (scan_scope_files), а не по всей БД.
        long total = _files.CountFilesWithDuplicateSizes(req.SessionId);
        long processed = 0;

        // Размеры, встречающиеся >1 раза, — одним запросом (список long, мало памяти). Обрабатываем
        // ПАЧКАМИ размеров: короткоживущая выборка `GetBySizesOrdered` закрывается ДО записи хэшей,
        // поэтому read-mark не удерживает WAL от чекпоинта. Память ограничена одной пачкой размеров.
        var sizes = _files.GetDuplicateSizeCandidates(req.SessionId);
        for (int i = 0; i < sizes.Count; i += SizeBatch)
        {
            rep.WaitGate();
            ct.ThrowIfCancellationRequested();
            var batch = sizes.GetRange(i, Math.Min(SizeBatch, sizes.Count - i));

            foreach (var sizeGroup in _files.GetBySizesOrdered(batch, req.SessionId).GroupBy(f => f.Size))
                ProcessSizeBucket(sizeGroup.ToList(), req, rep, ct, result, ref processed, total);

            _files.PassiveCheckpoint(); // reader закрыт → SQLite может освободить WAL
        }

        // Точные группы (включая кросс-том/офлайн hash-only) — безопасная побайтовая сверка внутри.
        rep.Stage(ScanStage.Grouping);
        result.GroupsCreated = _grouper.BuildExactGroups(req.VolumesById, req.InScope, req.Rule, rep.ExactGroups, ct, req.SessionId);
        return result;
    }

    private void ProcessSizeBucket(List<FileRecord> sameSize, ExactScanRequest req, IExactScanReporter rep,
        CancellationToken ct, ExactScanResult result, ref long processed, long total)
    {
        // sameSize — все файлы этого размера (включая отключённые тома с уже сохранёнными хэшами):
        // это даёт совпадения между подключённым и ранее сохранённым носителем (FR-40/41).
        try
        {
            if (sameSize.Count < 2) return; // защитно; запрос и так отдаёт только дубль-размеры
            result.SizeCandidates += sameSize.Count;

            var inScope = sameSize.Where(req.InScope).ToList();
            if (inScope.Count == 0) return;

            // --- частичный хэш: только подключённые in-scope без хэша ---
            var needPartial = inScope.Where(f => f.PartialHash is null).ToList();
            if (needPartial.Count > 0)
            {
                HashByVolume(needPartial, partial: true, req, rep, ct);
                _files.UpdateComputedBatch(needPartial);
                result.PartialHashed += needPartial.Count;
            }

            // --- группировка по частичному → полный хэш только для коллизий ---
            foreach (var grp in sameSize.Where(f => f.PartialHash is not null)
                         .GroupBy(f => Convert.ToHexString(f.PartialHash!)))
            {
                var candidates = grp.ToList();
                if (candidates.Count < 2) continue;

                var needFull = candidates.Where(f => f.FullHash is null && req.InScope(f)).ToList();
                if (needFull.Count == 0) continue;
                rep.StageQuiet(ScanStage.FullHashing);
                HashByVolume(needFull, partial: false, req, rep, ct);
                _files.UpdateComputedBatch(needFull);
                result.FullHashed += needFull.Count;
            }
        }
        finally
        {
            processed += sameSize.Count;
            if (total > 0) rep.Fraction(Math.Min(1.0, processed / (double)total));
            rep.SessionTick();
        }
    }

    private void HashByVolume(List<FileRecord> files, bool partial, ExactScanRequest req,
        IExactScanReporter rep, CancellationToken ct)
    {
        VolumeParallelExecutor.RunByVolume(
            files,
            f => f.VolumeId,
            vid => req.ReadPlans.TryGetValue(vid, out var p) ? p.ContentDop : Math.Max(1, req.RequestedParallelism),
            req.MaxVolumeParallelism,
            ct,
            f =>
            {
                rep.WaitGate();
                var path = FullPath(f, req.MountById);
                if (path is null || !File.Exists(LongPath.Fix(path))) return;
                try
                {
                    if (partial)
                    {
                        // Передаём ожидаемый размер: бросит FileChangedDuringScanException, если файл
                        // изменился между metadata-этапом и чтением — тогда хэш не сохраняется.
                        f.PartialHash = _hash.ComputePartialHash(path, f.Size, ct);
                        if (f.ScanState < ScanState.Partial) f.ScanState = ScanState.Partial;
                    }
                    else
                    {
                        f.FullHash = _hash.ComputeFullHash(path, f.Size, ct);
                        f.ScanState = ScanState.Full;
                    }
                    rep.FileHashed(path, f.Size);
                }
                catch (OperationCanceledException) { throw; }
                catch (FileChangedDuringScanException) { rep.ChangedSkipped(); }
                catch { rep.Skipped(); } // битый/занятый файл — пропуск (NFR-1)
            });
    }

    private static string? FullPath(FileRecord f, IReadOnlyDictionary<long, string> mountById) =>
        mountById.TryGetValue(f.VolumeId, out var mp) ? Path.Combine(mp, f.RelPath) : null;
}
