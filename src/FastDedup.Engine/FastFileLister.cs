using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace FastDedup.Engine;

/// <summary>Лёгкая запись о файле для внешнего потребителя. Без хэшей.</summary>
public readonly record struct EnumeratedFile(
    string FullPath, long Size, long CreationRaw, long LastWriteRaw, uint Attributes);

public sealed class FastListerOptions
{
    /// <summary>Заходить ли внутрь reparse-точек (симлинки/джанкшены). По умолчанию нет — защита от циклов.</summary>
    public bool IncludeReparsePoints { get; init; } = false;

    /// <summary>Параллелизм стадии stat (только MFT-путь). null — авто.</summary>
    public int? StatParallelism { get; init; }

    /// <summary>Пропускать скрытые/системные файлы И каталоги.</summary>
    public bool SkipHiddenSystem { get; init; } = false;

    /// <summary>Какой бэкенд выбран для этого корня (для UI/диагностики).</summary>
    public Action<BackendDecision>? OnBackend { get; init; }

    /// <summary>Ошибка перечисления (недоступная папка, сбой API).</summary>
    public Action<ScanError>? OnError { get; init; }
}

/// <summary>
/// Быстрый перечислитель файлов. Выбирает бэкенд по матрице (<see cref="BackendSelector"/>):
///   • целый том NTFS под админом → турбо MFT/USN (плюс stat — USN-записи не дают размер/время);
///   • всё остальное (exFAT/FAT32/ReFS/сеть/папка/без админа) → совместимый быстрый режим Win32Find,
///     где метаданные приходят прямо из FindFirstFileExW БЕЗ второго stat на каждый файл.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FastFileLister
{
    public IEnumerable<EnumeratedFile> Enumerate(string root, FastListerOptions options, CancellationToken ct)
    {
        var errors = new ConcurrentBag<ScanError>();
        var decision = DecideBackend(root);

        var engineOpts = new ScanOptions
        {
            Roots = new[] { root },
            IncludeReparsePoints = options.IncludeReparsePoints,
            SkipHiddenSystem = options.SkipHiddenSystem
        };

        try
        {
            if (decision.Backend == EnumerationBackend.NtfsMft)
            {
                // MFT может оказаться недоступен (неподдерживаемые записи / нет прав) — тогда
                // переключаемся на Win32Find и сообщаем реальный бэкенд через OnBackend изнутри.
                foreach (var ef in EnumerateViaMftOrFallback(root, engineOpts, options, errors, ct, decision))
                    yield return ef;
            }
            else
            {
                options.OnBackend?.Invoke(decision);
                // Совместимый быстрый режим: метаданные напрямую из FindFirstFileExW (без второго stat).
                foreach (var ef in new Win32FindMetadataEnumerator().EnumerateMetadata(root, engineOpts, errors, ct))
                    yield return ef;
            }
        }
        finally
        {
            // Ошибки (недоступные папки, сбой MFT) отдаём наружу в любом случае, в т.ч. при отмене.
            if (options.OnError != null)
                foreach (var e in errors) options.OnError(e);
        }
    }

    /// <summary>
    /// Пытаемся прочитать MFT; если он недоступен — НЕ старый Directory+stat, а совместимый быстрый
    /// Win32Find (метаданные напрямую). Решение о бэкенде (NtfsMft подтверждён / Win32Find откат)
    /// сообщаем через OnBackend ровно один раз, после того как стало известно, что реально используем.
    /// </summary>
    private static IEnumerable<EnumeratedFile> EnumerateViaMftOrFallback(
        string root, ScanOptions engineOpts, FastListerOptions options,
        ConcurrentBag<ScanError> errors, CancellationToken ct, BackendDecision decision)
    {
        Dictionary<ulong, UsnEnumerator.Rec>? map = null;
        string drive = "";
        MftEnumerationUnavailableException? failure = null;
        try { (map, drive) = UsnEnumerator.ReadMap(root, ct); }
        catch (MftEnumerationUnavailableException ex) { failure = ex; }

        IEnumerable<EnumeratedFile> source;
        if (map is null)
        {
            var fb = decision with
            {
                Backend = EnumerationBackend.Win32Find,
                Reason = failure!.Reason,
                TechnicalDetails = failure.Message
            };
            options.OnBackend?.Invoke(fb);
            errors.Add(new ScanError(root, "MFT недоступен, используется совместимый быстрый режим: " + failure.Message));
            source = new Win32FindMetadataEnumerator().EnumerateMetadata(root, engineOpts, errors, ct);
        }
        else
        {
            options.OnBackend?.Invoke(decision); // подтверждённый турбо NtfsMft
            source = StatPaths(UsnEnumerator.BuildPaths(map, drive, root, engineOpts, ct), options, ct);
        }

        // Единственная точка yield (без try/catch вокруг неё — требование итераторов C#).
        foreach (var ef in source) yield return ef;
    }

    /// <summary>MFT-путь: USN отдаёт пути, размер/время берём stat-ом (USN-записи их не содержат).</summary>
    private static IEnumerable<EnumeratedFile> StatPaths(
        IEnumerable<string> paths, FastListerOptions options, CancellationToken ct)
    {
        int dop = options.StatParallelism ?? Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        var output = new BlockingCollection<EnumeratedFile>(boundedCapacity: 50_000);

        var pump = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(paths,
                    new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                    path =>
                    {
                        if (!Native.TryGetFileInfo(path, out long size, out uint attrs, out long cr, out long lw)) return;
                        if ((attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) return;
                        if (!options.IncludeReparsePoints && (attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return;
                        if (options.SkipHiddenSystem && (attrs & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0) return;
                        output.Add(new EnumeratedFile(path, size, cr, lw, attrs), ct);
                    });
            }
            catch (OperationCanceledException) { }
            finally { output.CompleteAdding(); }
        }, ct);

        foreach (var item in output.GetConsumingEnumerable(ct))
            yield return item;

        pump.GetAwaiter().GetResult(); // пробросить исключения насоса
    }

    /// <summary>Выбор бэкенда: определяем ФС/тип носителя/целый ли том и зовём чистый BackendSelector.</summary>
    private static BackendDecision DecideBackend(string root)
    {
        try
        {
            bool win = OperatingSystem.IsWindows();
            bool whole = BackendSelector.IsWholeVolumeRoot(root);
            string? fs = null;
            string? volRoot = null;
            System.IO.DriveType? driveType = null;

            if (BackendSelector.IsUncPath(root))
            {
                volRoot = root; // сеть/UNC — детали ФС недоступны, BackendSelector выберет сетевой режим
            }
            else if (win)
            {
                var rootPath = Path.GetPathRoot(Path.GetFullPath(root));
                if (rootPath is { Length: >= 2 } && rootPath[1] == ':')
                {
                    volRoot = rootPath[..2] + "\\";
                    try
                    {
                        var di = new DriveInfo(rootPath[..2] + "\\");
                        driveType = di.DriveType;
                        if (di.IsReady) fs = di.DriveFormat; // "NTFS"/"exFAT"/"FAT32"/"ReFS"…
                    }
                    catch { /* ФС/тип неизвестны → совместимый режим */ }
                }
            }

            return BackendSelector.Decide(root, volRoot, fs, driveType, win, Native.IsElevated(), whole);
        }
        catch
        {
            return new BackendDecision(root, null, null, null, false, false,
                EnumerationBackend.Win32Find, BackendFallbackReason.ApiError, null);
        }
    }
}
