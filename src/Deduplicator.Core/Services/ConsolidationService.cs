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
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Файлы с полным хэшем группируем по содержимому; уникальные (без хэша) — каждый сам по себе.
        var withHash = all.Where(f => f.FullHash is not null)
                          .GroupBy(f => Convert.ToHexString(f.FullHash!));
        var withoutHash = all.Where(f => f.FullHash is null);

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

            plan.ToCopy.Add(new ConsolidationItem
            {
                File = chosen,
                WillCopy = true,
                TargetRelPath = UniqueTarget(chosen, options, usedTargets),
                DuplicateSiblings = siblings
            });
            foreach (var skip in members.Where(m => m.Id != chosen.Id))
                plan.ToSkip.Add(new ConsolidationItem { File = skip, WillCopy = false, DuplicateSiblings = siblings });
        }

        foreach (var f in withoutHash)
        {
            plan.ToCopy.Add(new ConsolidationItem
            {
                File = f,
                WillCopy = true,
                TargetRelPath = UniqueTarget(f, options, usedTargets),
                DuplicateSiblings = 0
            });
        }

        return plan;
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
                    report.Errors.Add($"Файл не найден: {src}");
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
                report.Errors.Add($"{item.File.Name}: {ex.Message}");
            }
        }

        report.Skipped = plan.ToSkip.Count;
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
