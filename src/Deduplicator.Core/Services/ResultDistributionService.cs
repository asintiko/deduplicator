using System.Globalization;
using System.Text;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IResultDistributionService
{
    /// <summary>Построить план раскладки (предпросмотр) — без изменений на диске.</summary>
    DistributionPlan BuildPlan(DistributionRequest request);

    /// <summary>Выполнить: ReportOnly пишет CSV; CopyFiles безопасно копирует (оригиналы не трогаются).</summary>
    DistributionResult Execute(DistributionPlan plan, DistributionRequest request, CancellationToken ct);
}

/// <summary>
/// Безопасное распределение результатов по папкам (P0-D). НИКОГДА не перемещает и не удаляет оригиналы:
/// только отчёт или копирование с проверкой после копирования (copy→temp→verify→rename). Раскладка по
/// «категория /band похожести». Имя-коллизия разрешается добавлением номера (_2, _3…), а не перезаписью.
/// </summary>
public sealed class ResultDistributionService : IResultDistributionService
{
    public DistributionPlan BuildPlan(DistributionRequest req)
    {
        var items = new List<DistributionItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in req.Files)
        {
            string folderKey = Path.Combine(SanitizeSeg(f.Category), SanitizeSeg(Band(f.Similarity)));
            string targetDir = Path.Combine(req.OutputRoot, folderKey);
            string name = Path.GetFileName(f.FullPath);

            string target = Path.Combine(targetDir, name);
            // Имя-коллизия (в плане ИЛИ уже на диске) → добавляем _2, _3… (никогда не перезаписываем).
            if (!used.Add(target) || File.Exists(LongPath.Fix(target)))
            {
                string baseName = Path.GetFileNameWithoutExtension(name);
                string ext = Path.GetExtension(name);
                int k = 2;
                do { target = Path.Combine(targetDir, $"{baseName}_{k++}{ext}"); }
                while (!used.Add(target) || File.Exists(LongPath.Fix(target)));
            }

            items.Add(new DistributionItem(f.Id, f.FullPath, target, folderKey, Skipped: false, Reason: null));
        }

        return new DistributionPlan(items, items.Count > 0);
    }

    public DistributionResult Execute(DistributionPlan plan, DistributionRequest req, CancellationToken ct)
    {
        if (!plan.CanExecute)
            return new DistributionResult(0, 0, 0, null, "Пустой план.");

        try
        {
            Directory.CreateDirectory(req.OutputRoot);

            if (req.Mode == DistributionMode.ReportOnly)
            {
                string report = WriteReport(plan, req.OutputRoot);
                return new DistributionResult(0, 0, 0, report, null);
            }

            int copied = 0, failed = 0, unavailable = 0;
            foreach (var it in plan.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (it.Skipped) continue;
                try
                {
                    if (CopyVerified(it.SourcePath, it.TargetPath, req.VerifyAfterCopy)) copied++;
                    else failed++;
                }
                catch
                {
                    // Сбой одного файла не валит пакет. Источник исчез (файл удалили или диск
                    // выдернули) — считаем отдельно: это не «ошибка копирования», а честное
                    // «недоступен», о чём UI сообщает своими словами.
                    if (!File.Exists(LongPath.Fix(it.SourcePath))) unavailable++;
                    else failed++;
                }
            }

            // Отчёт пишем ПОСЛЕ копирования, и его сбой (папка назначения исчезла/защищена)
            // не должен «съедать» счётчики уже сделанной работы (аудит ночь-1: выдернутый диск).
            string? rep = null;
            string? error = null;
            try { rep = WriteReport(plan, req.OutputRoot); }
            catch (Exception ex) { error = $"отчёт не записан: {ex.Message}"; }
            return new DistributionResult(copied, 0, failed, rep, error, unavailable);
        }
        catch (Exception ex)
        {
            return new DistributionResult(0, 0, 0, null, ex.Message);
        }
    }

    /// <summary>Копирование через временный файл + проверка (размер, при VerifyAfterCopy — побайтно). Источник НЕ удаляем.</summary>
    private static bool CopyVerified(string source, string target, bool verify)
    {
        string dir = Path.GetDirectoryName(target) ?? "";
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, ".dedup-dist-" + Guid.NewGuid().ToString("N") + ".tmp");

        File.Copy(LongPath.Fix(source), LongPath.Fix(temp), overwrite: false);

        var srcLen = new FileInfo(LongPath.Fix(source)).Length;
        var tmpLen = new FileInfo(LongPath.Fix(temp)).Length;
        if (srcLen != tmpLen || (verify && !SameContent(source, temp)))
        {
            try { File.Delete(LongPath.Fix(temp)); } catch { }
            return false;
        }

        // Финализируем (если цель уже появилась — не перезаписываем, считаем неуспехом).
        if (File.Exists(LongPath.Fix(target))) { try { File.Delete(LongPath.Fix(temp)); } catch { } return false; }
        try
        {
            File.Move(LongPath.Fix(temp), LongPath.Fix(target));
            return true;
        }
        catch
        {
            // Гонка/блокировка на финальном Move — не оставляем висеть .tmp-файл.
            try { File.Delete(LongPath.Fix(temp)); } catch { }
            return false;
        }
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(LongPath.Fix(a));
        using var fb = File.OpenRead(LongPath.Fix(b));
        var ba = new byte[64 * 1024];
        var bb = new byte[64 * 1024];
        int ra;
        while ((ra = fa.Read(ba, 0, ba.Length)) > 0)
        {
            int off = 0;
            while (off < ra)
            {
                int rb = fb.Read(bb, off, ra - off);
                if (rb == 0) return false;
                off += rb;
            }
            for (int i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
        }
        return fb.ReadByte() == -1; // у b не должно остаться лишних байт
    }

    private static string WriteReport(DistributionPlan plan, string outputRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Папка;Источник;Цель");
        foreach (var it in plan.Items)
            sb.Append(Csv(it.FolderKey)).Append(';')
              .Append(Csv(it.SourcePath)).Append(';')
              .Append(Csv(it.TargetPath)).AppendLine();

        string path = Path.Combine(outputRoot, "distribution-report.csv");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>Band похожести для папки-раскладки (60+: понятные подписи).</summary>
    public static string Band(double similarity) => similarity switch
    {
        >= 0.95 => "Очень похожие",
        >= 0.85 => "Похожие",
        _ => "Проверить вручную"
    };

    private static string SanitizeSeg(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Прочее";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.', ' ');
    }

    private static string Csv(string s) =>
        s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
