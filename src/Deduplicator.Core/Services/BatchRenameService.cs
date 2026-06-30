using System.Text;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IBatchRenameService
{
    /// <summary>Построить план (предпросмотр) — без изменений на диске.</summary>
    BatchRenamePlan BuildPlan(BatchRenameRequest request);

    /// <summary>Выполнить план двухфазно с откатом при ошибке.</summary>
    BatchRenameResult Execute(BatchRenamePlan plan, CancellationToken ct);
}

/// <summary>
/// Безопасное групповое переименование (P0-B). Правила: сначала предпросмотр; запрещённые символы → «_»;
/// проверка длины пути; коллизии с существующими файлами и внутри набора; не трогаем защищённые/офлайн;
/// двухфазно (исходные → временные GUID-имена → финальные), что корректно обрабатывает свопы/циклы
/// (A↔B) и переименование с изменением только регистра; при ошибке на середине — откат всего, что можно.
/// </summary>
public sealed class BatchRenameService : IBatchRenameService
{
    private const int MaxPathLen = 259; // классический MAX_PATH-1; длиннее → конфликт (не рискуем)

    public BatchRenamePlan BuildPlan(BatchRenameRequest req)
    {
        string baseName = Sanitize(req.BaseName);
        int width = Math.Clamp(req.NumberWidth, 1, 9);
        var ordered = Sort(req.Files, req.SortOrder);

        var items = new List<BatchRenameItem>();
        int n = req.StartNumber;

        var newPathCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = new HashSet<string>(req.Files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var f in ordered)
        {
            string oldName = Path.GetFileName(f.FullPath);
            if (!f.CanModify)
            {
                items.Add(new BatchRenameItem(f.Id, f.FullPath, oldName, oldName, f.FullPath,
                    Skipped: true, SkipReason: "Недоступен сейчас или защищён — пропущен"));
                continue;
            }

            string dir = Path.GetDirectoryName(f.FullPath) ?? "";
            string ext = req.PreserveExtension ? Path.GetExtension(f.FullPath) : "";
            string newName = $"{baseName}_{n.ToString("D" + width)}{ext}";
            n++;
            string newFull = Path.Combine(dir, newName);

            items.Add(new BatchRenameItem(f.Id, f.FullPath, oldName, newName, newFull, Skipped: false, SkipReason: null));
            newPathCount[newFull] = newPathCount.GetValueOrDefault(newFull) + 1;
        }

        var conflicts = new List<RenameConflict>();
        foreach (var it in items)
        {
            if (it.Skipped) continue;

            if (it.NewFullPath.Length > MaxPathLen)
            {
                conflicts.Add(new RenameConflict(it.FileId, it.NewFullPath, "Слишком длинный путь"));
                continue;
            }
            if (newPathCount[it.NewFullPath] > 1)
            {
                conflicts.Add(new RenameConflict(it.FileId, it.NewFullPath, "Два файла получают одинаковое имя"));
                continue;
            }
            // Существующий файл-помеха ВНЕ переименовываемого набора (файлы из набора уедут — это нормально).
            if (File.Exists(LongPath.Fix(it.NewFullPath)) && !sourcePaths.Contains(it.NewFullPath))
                conflicts.Add(new RenameConflict(it.FileId, it.NewFullPath, "Файл с таким именем уже существует"));
        }

        bool canExecute = conflicts.Count == 0 && items.Any(i => !i.Skipped);
        return new BatchRenamePlan(items, conflicts, canExecute);
    }

    public BatchRenameResult Execute(BatchRenamePlan plan, CancellationToken ct)
    {
        int skipped = plan.Items.Count(i => i.Skipped);
        if (!plan.CanExecute)
            return new BatchRenameResult(0, skipped, 0, RolledBack: false,
                Error: "План содержит конфликты или нечего переименовывать.",
                RenamedPairs: Array.Empty<RenamePair>(), OrphanedTempPaths: Array.Empty<string>());

        var todo = plan.Items.Where(i => !i.Skipped).ToList();
        // moved[i] = (текущее расположение файла, куда вернуть при откате). Изначально текущее = исходное.
        var moved = new List<(string current, string original)>();

        try
        {
            // Фаза 1: исходные → временные имена (разрывает свопы/циклы и case-only).
            var temps = new List<string>(todo.Count);
            foreach (var it in todo)
            {
                ct.ThrowIfCancellationRequested();
                string dir = Path.GetDirectoryName(it.OldFullPath) ?? "";
                string temp = Path.Combine(dir, ".dedup-ren-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.Move(LongPath.Fix(it.OldFullPath), LongPath.Fix(temp));
                temps.Add(temp);
                moved.Add((temp, it.OldFullPath));
            }

            // Фаза 2: временные → финальные.
            var done = new List<RenamePair>(todo.Count);
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var it = todo[i];
                File.Move(LongPath.Fix(temps[i]), LongPath.Fix(it.NewFullPath));
                moved[i] = (it.NewFullPath, it.OldFullPath); // теперь файл на финальном месте
                done.Add(new RenamePair(it.FileId, it.OldFullPath, it.NewFullPath));
            }

            return new BatchRenameResult(done.Count, skipped, 0, RolledBack: false, Error: null,
                RenamedPairs: done, OrphanedTempPaths: Array.Empty<string>());
        }
        catch (Exception ex)
        {
            // Откат: всё, что успели сдвинуть, возвращаем к исходным именам (best-effort, в обратном порядке).
            // СРАВНЕНИЕ Ordinal (с учётом регистра): иначе rename, меняющий только регистр (foo→FOO),
            // считался бы «ничем не двигался» и НЕ откатывался. temp-имена — GUID, регистрового совпадения нет.
            var orphans = new List<string>();
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                var (current, original) = moved[i];
                if (string.Equals(current, original, StringComparison.Ordinal)) continue; // двигать нечего
                try { File.Move(LongPath.Fix(current), LongPath.Fix(original)); }
                catch { orphans.Add(current); } // не смогли вернуть — отдадим путь пользователю (файл не потерян)
            }
            return new BatchRenameResult(0, skipped, todo.Count, RolledBack: true, Error: ex.Message,
                RenamedPairs: Array.Empty<RenamePair>(), OrphanedTempPaths: orphans);
        }
    }

    private static IReadOnlyList<RenameSourceFile> Sort(IReadOnlyList<RenameSourceFile> files, RenameSortOrder order) => order switch
    {
        RenameSortOrder.CreatedDate => files.OrderBy(f => f.Created).ToList(),
        RenameSortOrder.ModifiedDate => files.OrderBy(f => f.Modified).ToList(),
        RenameSortOrder.Name => files.OrderBy(f => Path.GetFileName(f.FullPath), StringComparer.OrdinalIgnoreCase).ToList(),
        RenameSortOrder.Size => files.OrderBy(f => f.Size).ToList(),
        _ => files
    };

    /// <summary>Заменить запрещённые в Windows символы на «_», убрать хвостовые точки/пробелы.</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "file" : s;
    }
}
