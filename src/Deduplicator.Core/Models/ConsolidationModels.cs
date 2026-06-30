namespace Deduplicator.Core.Models;

/// <summary>Параметры построения плана консолидации в чистый архив.</summary>
public sealed class ConsolidationOptions
{
    public List<long> SourceVolumeIds { get; set; } = new();

    /// <summary>Целевая папка (диск/каталог), куда собирается чистый архив.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Правило выбора, какую копию из дубликатов переносить.</summary>
    public RepresentativeRule Rule { get; set; } = RepresentativeRule.Oldest;

    /// <summary>Перемещать вместо копирования (по умолчанию false — источники не трогаем).</summary>
    public bool MoveInsteadOfCopy { get; set; } = false;

    /// <summary>Сохранять относительную структуру папок носителя-источника.</summary>
    public bool PreserveFolderStructure { get; set; } = true;
}

/// <summary>Один элемент плана консолидации.</summary>
public sealed class ConsolidationItem
{
    public required FileRecord File { get; init; }

    /// <summary>true — уникальный файл к копированию; false — дубликат, пропускается.</summary>
    public bool WillCopy { get; init; }

    /// <summary>Относительный путь назначения в целевом хранилище.</summary>
    public string TargetRelPath { get; init; } = string.Empty;

    /// <summary>Сколько ещё копий этого содержимого пропускается (для дубликатов).</summary>
    public int DuplicateSiblings { get; init; }
}

/// <summary>Полный план консолидации с предпросмотром.</summary>
public sealed class ConsolidationPlan
{
    public List<ConsolidationItem> ToCopy { get; set; } = new();
    public List<ConsolidationItem> ToSkip { get; set; } = new();

    public int UniqueCount => ToCopy.Count;
    public int SkippedCount => ToSkip.Count;
    public long BytesToCopy => ToCopy.Sum(i => i.File.Size);
    public long BytesSaved => ToSkip.Sum(i => i.File.Size);
}

/// <summary>Итоговый отчёт выполнения консолидации.</summary>
public sealed class ConsolidationReport
{
    public int Copied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long BytesCopied { get; set; }
    public List<string> Errors { get; set; } = new();
}
