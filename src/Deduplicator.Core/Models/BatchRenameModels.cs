namespace Deduplicator.Core.Models;

/// <summary>Порядок нумерации при групповом переименовании.</summary>
public enum RenameSortOrder
{
    AsIs = 0,
    CreatedDate = 1,
    ModifiedDate = 2,
    Name = 3,
    Size = 4
}

/// <summary>
/// Исходный файл для группового переименования. Передаётся уже разрешённым (полный путь + метаданные),
/// чтобы сервис был чистым и тестируемым без доступа к БД/томам. <see cref="CanModify"/> = файл доступен
/// и не защищён (защищённые/офлайн пропускаются).
/// </summary>
public sealed record RenameSourceFile(
    long Id,
    string FullPath,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    long Size,
    bool CanModify);

/// <summary>Запрос на групповое переименование (MVP: на месте, с сохранением расширения).</summary>
public sealed record BatchRenameRequest(
    IReadOnlyList<RenameSourceFile> Files,
    string BaseName,
    int StartNumber,
    int NumberWidth,
    RenameSortOrder SortOrder,
    bool PreserveExtension = true);

/// <summary>Одна строка плана: старое имя → новое (или причина пропуска).</summary>
public sealed record BatchRenameItem(
    long FileId,
    string OldFullPath,
    string OldName,
    string NewName,
    string NewFullPath,
    bool Skipped,
    string? SkipReason);

/// <summary>Конфликт, блокирующий выполнение (имя занято/дубль внутри набора/слишком длинный путь).</summary>
public sealed record RenameConflict(long FileId, string NewFullPath, string Reason);

/// <summary>План переименования: предпросмотр + конфликты + можно ли выполнять.</summary>
public sealed record BatchRenamePlan(
    IReadOnlyList<BatchRenameItem> Items,
    IReadOnlyList<RenameConflict> Conflicts,
    bool CanExecute);

/// <summary>
/// Итог выполнения. RolledBack=true — была ошибка и всё откатилось обратно. Если откат какого-то файла
/// тоже не удался, его текущий (временный) путь попадает в <see cref="OrphanedTempPaths"/>, чтобы можно
/// было восстановить вручную (файл не потерян — лежит под GUID-именем).
/// </summary>
public sealed record BatchRenameResult(
    int Renamed,
    int Skipped,
    int Failed,
    bool RolledBack,
    string? Error,
    IReadOnlyList<RenamePair> RenamedPairs,
    IReadOnlyList<string> OrphanedTempPaths);

/// <summary>Пара «было → стало» (для журнала/отмены).</summary>
public sealed record RenamePair(long FileId, string OldFullPath, string NewFullPath);
