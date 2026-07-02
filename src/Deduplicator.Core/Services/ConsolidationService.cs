using Deduplicator.Core.Data;
using Deduplicator.Core.Matching;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IConsolidationService
{
    /// <summary>Построить план: какие файлы уникальны (копировать), какие — дубликаты (пропустить).</summary>
    ConsolidationPlan BuildPlan(ConsolidationOptions options);

    /// <summary>Выполнить план: скопировать уникальное содержимое в целевое хранилище.</summary>
    ConsolidationReport Execute(ConsolidationPlan plan, ConsolidationOptions options,
        Action<int, int, string>? onProgress, CancellationToken ct);
}

/// <summary>Консолидация разрозненных носителей в один дедуплицированный архив.</summary>
public sealed class ConsolidationService : IConsolidationService
{
    // 3.5: порог схлопывания ПОХОЖИХ фото. СОЗНАТЕЛЬНО жёстче пользовательского порога поиска
    // похожих (0.80–0.97): консолидация решает, что НЕ копировать в архив, поэтому схлопываются
    // только почти неразличимые кадры — схожесть ≥ 0.97 означает не более 1 бита различия pHash из 64.
    private const double SimilarCollapseThreshold = 0.97;

    // 3.5: кап числа кандидатов на полный попарный перебор похожести. Консолидация оперирует
    // тысячами файлов (не миллионами); при аномально большом наборе фото с pHash O(N²) стал бы
    // неподъёмным — тогда похожие НЕ схлопываем вовсе (консервативно: лишняя копия в архиве
    // лучше тихо пропавшего кадра).
    private const int SimilarCollapseCandidatesCap = 20_000;

    private readonly FileRepository _files;
    private readonly IVolumeService _volumeService;

    public ConsolidationService(FileRepository files, IVolumeService volumeService)
    {
        _files = files;
        _volumeService = volumeService;
    }

    public ConsolidationPlan BuildPlan(ConsolidationOptions options)
    {
        var catalog = _volumeService.SyncAndGetCatalog();
        var byId = catalog.ToDictionary(v => v.Id);

        // Собираем все файлы выбранных источников.
        var all = new List<FileRecord>();
        foreach (var vid in options.SourceVolumeIds)
        {
            var volFiles = _files.GetByVolume(vid);
            foreach (var f in volFiles)
            {
                if (byId.TryGetValue(vid, out var v)) f.VolumeLabel = v.DisplayName;
                all.Add(f);
            }
        }

        var plan = new ConsolidationPlan();

        // Файлы с полным хэшем группируем по содержимому; уникальные (без хэша) — каждый сам по себе.
        var withHash = all.Where(f => f.FullHash is not null)
                          .GroupBy(f => Convert.ToHexString(f.FullHash!));
        var withoutHash = all.Where(f => f.FullHash is null);

        // Сначала решаем, ЧТО копировать (эталоны точных групп + бесхэшевые), и только потом
        // назначаем целевые пути: схлопывание похожих (3.5) идёт между этими шагами, чтобы имена
        // схлопнутых не резервировались впустую и не плодили суффиксы __N у оставшихся файлов.
        var chosenToCopy = new List<(FileRecord File, int Siblings)>();

        foreach (var grp in withHash)
        {
            var members = grp.ToList();
            // Предпочитаем копию на ПОДКЛЮЧЁННОМ носителе, чтобы файл реально скопировался
            // (иначе при отключённом эталоне содержимое не попало бы в архив).
            var connectedMembers = members
                .Where(m => byId.TryGetValue(m.VolumeId, out var v) && v.IsConnected)
                .ToList();
            var pickFrom = connectedMembers.Count > 0 ? connectedMembers : members;
            var chosen = RepresentativeSelector.Pick(pickFrom, options.Rule);
            int siblings = members.Count - 1;

            chosenToCopy.Add((chosen, siblings));
            foreach (var skip in members.Where(m => m.Id != chosen.Id))
                plan.ToSkip.Add(new ConsolidationItem { File = skip, WillCopy = false, DuplicateSiblings = siblings });
        }

        foreach (var f in withoutHash)
            chosenToCopy.Add((f, 0));

        // 3.5: из группы почти одинаковых фото в архив едет один кадр; схлопнутые уходят
        // в plan.CollapsedSimilar (копироваться не будут, источники не трогаются).
        if (options.CollapseSimilarPhotos)
            chosenToCopy = CollapseSimilarPhotos(chosenToCopy, plan);

        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, siblings) in chosenToCopy)
        {
            plan.ToCopy.Add(new ConsolidationItem
            {
                File = file,
                WillCopy = true,
                TargetRelPath = UniqueTarget(file, options, usedTargets),
                DuplicateSiblings = siblings
            });
        }

        return plan;
    }

    /// <summary>
    /// 3.5: схлопывание ПОХОЖИХ фото среди файлов К КОПИРОВАНИЮ. Кандидаты — изображения с pHash;
    /// группируются полным попарным сравнением (PerceptualLsh.Similarity) с консервативным порогом
    /// <see cref="SimilarCollapseThreshold"/> через DSU (те же механизмы, что в группировке похожих).
    /// Из каждой группы остаётся самый резкий кадр (Sharpness из 3.7; fallback — больший размер
    /// файла), остальные помечаются схлопнутыми и НЕ копируются. Возвращает прореженный список.
    /// </summary>
    private static List<(FileRecord File, int Siblings)> CollapseSimilarPhotos(
        List<(FileRecord File, int Siblings)> chosen, ConsolidationPlan plan)
    {
        var candidates = chosen
            .Where(c => c.File.Category == FileCategory.Image && c.File.PHash is { Length: >= 8 })
            .ToList();
        if (candidates.Count < 2) return chosen;
        if (candidates.Count > SimilarCollapseCandidatesCap)
        {
            AppLog.Write($"Консолидация: кандидатов на схлопывание похожих {candidates.Count} > капа " +
                         $"{SimilarCollapseCandidatesCap} — похожие фото не схлопываются (все едут в архив).");
            return chosen;
        }

        var hashOf = candidates.ToDictionary(c => c.File.Id, c => PerceptualLsh.ToHash(c.File.PHash));
        var dsu = new Dsu();
        foreach (var c in candidates) dsu.MakeSet(c.File.Id);
        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (PerceptualLsh.Similarity(hashOf[candidates[i].File.Id], hashOf[candidates[j].File.Id])
                        >= SimilarCollapseThreshold)
                    dsu.Union(candidates[i].File.Id, candidates[j].File.Id);
            }
        }

        var fileById = candidates.ToDictionary(c => c.File.Id, c => c.File);
        var siblingsById = candidates.ToDictionary(c => c.File.Id, c => c.Siblings);
        var collapsedIds = new HashSet<long>();
        foreach (var (_, ids) in dsu.Groups())
        {
            if (ids.Count < 2) continue;
            var members = ids.Select(id => fileById[id]).ToList();
            var keep = PickSharpest(members);
            foreach (var m in members)
            {
                if (m.Id == keep.Id) continue;
                collapsedIds.Add(m.Id);
                plan.CollapsedSimilar.Add(new ConsolidationItem
                {
                    File = m,
                    WillCopy = false,
                    CollapsedInto = keep,
                    SimilarityToKept = PerceptualLsh.Similarity(hashOf[keep.Id], hashOf[m.Id]),
                    DuplicateSiblings = siblingsById[m.Id]
                });
            }
        }

        return collapsedIds.Count == 0 ? chosen : chosen.Where(c => !collapsedIds.Contains(c.File.Id)).ToList();
    }

    /// <summary>
    /// 3.5: какой кадр похожей группы оставить в архиве — с максимальной резкостью (3.7); резкость
    /// не посчитана ни у кого — больший размер файла; тай-брейки (размер, затем меньший id) делают
    /// план детерминированным.
    /// </summary>
    private static FileRecord PickSharpest(List<FileRecord> members)
    {
        var withSharpness = members.Where(f => f.Sharpness is not null).ToList();
        if (withSharpness.Count > 0)
        {
            return withSharpness
                .OrderByDescending(f => f.Sharpness!.Value)
                .ThenByDescending(f => f.Size)
                .ThenBy(f => f.Id)
                .First();
        }
        return members.OrderByDescending(f => f.Size).ThenBy(f => f.Id).First();
    }

    public ConsolidationReport Execute(ConsolidationPlan plan, ConsolidationOptions options,
        Action<int, int, string>? onProgress, CancellationToken ct)
    {
        var report = new ConsolidationReport();
        var catalog = _volumeService.SyncAndGetCatalog();
        var mountById = catalog
            .Where(v => v.IsConnected && v.MountPath is not null)
            .ToDictionary(v => v.Id, v => v.MountPath!);

        // Заранее проверяем доступность целевой папки на запись — даём понятную ошибку,
        // а не падаем посреди копирования.
        try
        {
            Directory.CreateDirectory(LongPath.Fix(options.TargetPath));
            var probe = Path.Combine(options.TargetPath, ".dedup_write_test");
            File.WriteAllText(LongPath.Fix(probe), string.Empty);
            File.Delete(LongPath.Fix(probe));
        }
        catch (Exception ex)
        {
            report.Failed++;
            report.Errors.Add($"Нет доступа к целевой папке «{options.TargetPath}»: {ex.Message}");
            return report;
        }

        int total = plan.ToCopy.Count;
        int done = 0;

        foreach (var item in plan.ToCopy)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            if (!mountById.TryGetValue(item.File.VolumeId, out var mount))
            {
                report.Failed++;
                report.Errors.Add($"Носитель отключён: {item.File.RelPath}");
                continue;
            }

            var src = Path.Combine(mount, item.File.RelPath);
            var dst = Path.Combine(options.TargetPath, item.TargetRelPath);
            onProgress?.Invoke(done, total, item.File.Name);

            try
            {
                if (!File.Exists(LongPath.Fix(src)))
                {
                    report.Failed++;
                    // mountById строился на старте: том могли выдернуть ПОСРЕДИ копирования.
                    // Пропавший корень — это «носитель отключён», а не «файл не найден».
                    report.Errors.Add(Directory.Exists(LongPath.Fix(mount))
                        ? $"Файл не найден: {src}"
                        : $"Носитель отключён: {item.File.RelPath}");
                    continue;
                }
                if (options.MoveInsteadOfCopy)
                    SafeIo.MoveSafe(src, dst, ct);
                else
                    SafeIo.CopyAtomic(src, dst, ct);

                report.Copied++;
                report.BytesCopied += item.File.Size;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                report.Failed++;
                // Диск выдернули прямо во время копирования этого файла — сообщаем понятно,
                // а не системным текстом исключения; пакет продолжает работу.
                report.Errors.Add(Directory.Exists(LongPath.Fix(mount))
                    ? $"{item.File.Name}: {ex.Message}"
                    : $"Носитель отключён: {item.File.RelPath}");
            }
        }

        report.Skipped = plan.ToSkip.Count;

        // 3.5: схлопнутые похожие явно перечисляются в итоге — что не поехало и какой кадр оставлен.
        // Сами файлы-источники не тронуты: консолидация не удаляет ничего с исходных носителей.
        report.CollapsedSimilar = plan.CollapsedSimilar.Count;
        foreach (var c in plan.CollapsedSimilar)
        {
            var vol = c.File.VolumeLabel ?? $"том #{c.File.VolumeId}";
            report.CollapsedDetails.Add(
                $"«{c.File.RelPath}» ({vol}) схлопнут как похожее фото (сходство {c.SimilarityToKept:P1}) — " +
                $"в архив поехал «{c.CollapsedInto?.RelPath}»");
        }
        return report;
    }

    private static string UniqueTarget(FileRecord file, ConsolidationOptions options, HashSet<string> used)
    {
        string rel = options.PreserveFolderStructure
            ? Path.Combine(Sanitize(file.VolumeLabel ?? $"vol_{file.VolumeId}"), file.RelPath)
            : file.Name;

        string candidate = rel;
        int i = 1;
        while (!used.Add(candidate))
        {
            // При плоской структуре конфликт разрешаем в корне; иначе — в исходной папке.
            var dir = options.PreserveFolderStructure ? (Path.GetDirectoryName(rel) ?? string.Empty) : string.Empty;
            var nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
            var ext = Path.GetExtension(file.Name);
            candidate = Path.Combine(dir, $"{nameNoExt}__{i}{ext}");
            i++;
        }
        return candidate;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
