using System.Runtime.InteropServices;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IJunkScanService
{
    List<JunkItem> Scan(JunkScanOptions options, Action<int>? onProgress, CancellationToken ct);
}

/// <summary>Поиск «мусора»: пустые папки, файлы нулевого размера, временные файлы, битые ярлыки, большие файлы.</summary>
public sealed class JunkScanService : IJunkScanService
{
    private static readonly HashSet<string> TempExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".bak", ".old", ".crdownload", ".part", ".partial", ".download", ".~tmp", ".chk"
    };
    private static readonly HashSet<string> TempNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", "ehthumbs.db", "ehthumbs_vista.db", ".ds_store", "desktop.ini.tmp"
    };

    private const int MaxItems = 50_000; // защита от переполнения памяти/UI на огромных дисках

    public List<JunkItem> Scan(JunkScanOptions options, Action<int>? onProgress, CancellationToken ct)
    {
        var items = new List<JunkItem>();
        var emptyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Один COM-объект WScript.Shell на весь скан (создавать на каждый .lnk нельзя — утечка/крах).
        object? shell = options.BrokenShortcuts ? CreateShell() : null;
        try
        {
            foreach (var root in options.Roots)
            {
                ct.ThrowIfCancellationRequested();
                Walk(root, options, items, emptyDirs, shell, onProgress, ct);
            }
        }
        finally
        {
            if (shell is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }

        if (options.EmptyFolders)
        {
            // Отбираем только верхние пустые папки (родитель не пуст / отсутствует).
            foreach (var d in emptyDirs)
            {
                var parent = Directory.GetParent(d.TrimEnd('\\', '/'))?.FullName;
                if (parent is null || !emptyDirs.Contains(parent))
                {
                    DateTimeOffset mtime = default;
                    try { mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(LongPath.Fix(d)), TimeSpan.Zero); }
                    catch { }
                    items.Add(new JunkItem { Kind = JunkKind.EmptyFolder, Path = d, IsFolder = true, Modified = mtime });
                }
            }
            onProgress?.Invoke(items.Count);
        }

        return items;
    }

    /// <summary>Кадр обхода каталога (для итеративного DFS вместо рекурсии).</summary>
    private sealed class DirFrame
    {
        public string Dir = "";
        public DirectoryInfo[] Subdirs = Array.Empty<DirectoryInfo>();
        public int Index;
        public bool HasFiles;
    }

    /// <summary>
    /// Итеративный обход дерева (без рекурсии — нет риска переполнения стека на очень глубоких
    /// деревьях; StackOverflowException не ловится try/catch и ронял бы всё приложение, #8).
    /// Пустой каталог определяется по принципу пост-обхода: поддерево без единого файла.
    /// </summary>
    private void Walk(string root, JunkScanOptions o, List<JunkItem> items,
        HashSet<string> emptyDirs, object? shell, Action<int>? onProgress, CancellationToken ct)
    {
        var rootFrame = MakeFrame(root, o, items, shell, onProgress, ct);
        if (rootFrame is null) return; // корень недоступен

        var stack = new Stack<DirFrame>();
        stack.Push(rootFrame);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= MaxItems) break; // лимит — мягко сворачиваем обход

            var top = stack.Peek();
            if (top.Index < top.Subdirs.Length)
            {
                var sub = top.Subdirs[top.Index++];
                try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) { top.HasFiles = true; continue; } }
                catch { continue; }

                var child = MakeFrame(Path.Combine(top.Dir, sub.Name), o, items, shell, onProgress, ct);
                if (child is null) { top.HasFiles = true; continue; } // нет доступа — родитель не пуст
                stack.Push(child);
            }
            else
            {
                stack.Pop();
                if (!top.HasFiles && o.EmptyFolders) emptyDirs.Add(top.Dir);
                if (stack.Count > 0 && top.HasFiles) stack.Peek().HasFiles = true;
            }
        }
    }

    /// <summary>
    /// Обрабатывает файлы одного каталога (мусор + признак «есть файлы») и возвращает кадр обхода.
    /// null — каталог недоступен (родитель пометит себя непустым, чтобы не считать ошибочно пустым).
    /// </summary>
    private DirFrame? MakeFrame(string dir, JunkScanOptions o, List<JunkItem> items,
        object? shell, Action<int>? onProgress, CancellationToken ct)
    {
        FileInfo[] files;
        DirectoryInfo[] subdirs;
        try
        {
            var di = new DirectoryInfo(LongPath.Fix(dir));
            files = di.GetFiles();
            subdirs = di.GetDirectories();
        }
        catch
        {
            return null;
        }

        bool hasFiles = false;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= MaxItems) { hasFiles = true; break; }
            // Любая ошибка по конкретному файлу (исчез, занят, длинный путь) не должна валить скан.
            try
            {
                hasFiles = true;
                if (o.SkipHiddenSystem && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;

                var full = Path.Combine(dir, f.Name);
                if (o.TempFiles && IsTemp(f))
                    Add(items, JunkKind.TempFile, full, f);
                else if (o.ZeroByteFiles && f.Length == 0)
                    Add(items, JunkKind.ZeroByteFile, full, f);

                if (o.LargeFiles && f.Length >= o.LargeThresholdBytes)
                    Add(items, JunkKind.LargeFile, full, f);

                if (o.BrokenShortcuts && shell is not null &&
                    f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    IsBrokenShortcut(shell, full))
                    Add(items, JunkKind.BrokenShortcut, full, f);
            }
            catch (OperationCanceledException) { throw; }
            catch { hasFiles = true; /* проблемный файл — пропускаем */ }
        }

        if (items.Count % 200 == 0) onProgress?.Invoke(items.Count);
        return new DirFrame { Dir = dir, Subdirs = subdirs, HasFiles = hasFiles };
    }

    private static object? CreateShell()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            return t is null ? null : Activator.CreateInstance(t);
        }
        catch { return null; }
    }

    private static void Add(List<JunkItem> items, JunkKind kind, string path, FileInfo f) =>
        items.Add(new JunkItem
        {
            Kind = kind,
            Path = path,
            Size = f.Length,
            Modified = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
            IsFolder = false
        });

    private static bool IsTemp(FileInfo f)
    {
        if (TempExt.Contains(f.Extension)) return true;
        if (TempNames.Contains(f.Name)) return true;
        if (f.Name.StartsWith("~$", StringComparison.Ordinal)) return true; // временные файлы Office
        if (f.Name.StartsWith("~", StringComparison.Ordinal) &&
            f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsBrokenShortcut(object shell, string lnkPath)
    {
        object? sc = null;
        try
        {
            dynamic sh = shell;
            sc = sh.CreateShortcut(lnkPath);
            string target = ((dynamic)sc!).TargetPath as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target)) return false; // не файловый ярлык
            return !File.Exists(LongPath.Fix(target)) && !Directory.Exists(LongPath.Fix(target));
        }
        catch
        {
            return false;
        }
        finally
        {
            if (sc is not null)
            {
                try { System.Runtime.InteropServices.Marshal.ReleaseComObject(sc); } catch { }
            }
        }
    }
}
