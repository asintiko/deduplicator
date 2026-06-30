using System.Runtime.InteropServices;
using System.Text;
using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Services;

public interface IFileOpsService
{
    /// <summary>
    /// Безопасно удалить файл (в корзину или карантин) с записью в журнал и обновлением БД.
    /// Безвозвратное удаление недопустимо (FR-27).
    /// </summary>
    bool Delete(FileRecord file, string fullPath, bool useRecycleBin, long? groupId);

    /// <summary>Восстановить ранее перемещённый в карантин файл (FR-32).</summary>
    bool Restore(OperationLogEntry entry);

    /// <summary>Удалить произвольный путь (файл или папку) в Корзину Windows, с записью в журнал.</summary>
    bool RecyclePath(string fullPath);

    /// <summary>
    /// Заменить дубликат жёсткой ссылкой на эталон (NTFS, тот же том). Освобождает место без
    /// потери данных: содержимое остаётся в эталоне (FR-33, продвинутый режим).
    /// </summary>
    bool ReplaceWithHardLink(FileRecord duplicate, string duplicatePath, string representativePath, long? groupId);

    string QuarantineDir { get; }
}

/// <summary>Перемещение в корзину/карантин, журнал операций и восстановление.</summary>
public sealed class FileOpsService : IFileOpsService
{
    private readonly OperationLogRepository _log;
    private readonly FileRepository _files;
    private readonly GroupRepository _groups;
    private readonly IHashService _hash;
    private readonly object _quarantineLock = new();

    public string QuarantineDir { get; }

    public FileOpsService(OperationLogRepository log, FileRepository files, GroupRepository groups,
        IHashService hash, string quarantineDir)
    {
        _log = log;
        _files = files;
        _groups = groups;
        _hash = hash;
        QuarantineDir = quarantineDir;
        Directory.CreateDirectory(quarantineDir);
    }

    public bool Delete(FileRecord file, string fullPath, bool useRecycleBin, long? groupId)
    {
        if (!File.Exists(LongPath.Fix(fullPath)))
            return false;

        long logId;

        if (useRecycleBin)
        {
            // Упреждающая запись в журнал: пишем намерение ДО перемещения. Если питание
            // пропадёт сразу после перемещения, запись о файле уже сохранена (WAL). Если
            // перемещение не удалось — запись убираем (откат). Восстановление при сбое до
            // перемещения безопасно: Restore проверяет наличие файла в назначении.
            logId = _log.Insert(new OperationLogEntry
            {
                OpType = OperationType.DeleteToRecycle,
                FileId = file.Id,
                GroupId = groupId,
                SrcPath = fullPath,
                DstPath = "Корзина Windows",
                PerformedAt = DateTimeOffset.Now,
                Reversible = false, // восстановление — средствами Корзины Windows
                SizeBytes = file.Size,
                FileName = file.Name
            });

            if (!RecycleBin.MoveToRecycleBin(fullPath))
            {
                TryDeleteLog(logId);
                return false;
            }
        }
        else
        {
            // Резервируем путь карантина, пишем журнал, затем перемещаем — всё под одним
            // замком, чтобы не было гонок при параллельных удалениях.
            lock (_quarantineLock)
            {
                var dst = BuildQuarantinePath(file);
                logId = _log.Insert(new OperationLogEntry
                {
                    OpType = OperationType.MoveToQuarantine,
                    FileId = file.Id,
                    GroupId = groupId,
                    SrcPath = fullPath,
                    DstPath = dst,
                    PerformedAt = DateTimeOffset.Now,
                    Reversible = true,
                    SizeBytes = file.Size,
                    FileName = file.Name
                });
                try
                {
                    SafeIo.MoveSafe(fullPath, dst);
                }
                catch
                {
                    TryDeleteLog(logId);
                    return false;
                }
            }
        }

        // Файл уже безопасно перемещён в корзину/карантин и записан в журнал.
        // Подчистку БД оборачиваем, чтобы сбой БД не прерывал пакет и не «терял» успех.
        try
        {
            if (groupId is long gid) _groups.RemoveMember(gid, file.Id);
            _files.DeleteById(file.Id);
        }
        catch (Exception ex) { AppLog.Error("Delete/db-cleanup", ex); /* запись в БД подчистится при следующем сканировании */ }
        return true;
    }

    private void TryDeleteLog(long id)
    {
        try { _log.Delete(id); }
        catch (Exception ex) { AppLog.Error("Delete/rollback-log", ex); }
    }

    public bool Restore(OperationLogEntry entry)
    {
        if (!entry.Reversible || entry.Restored) return false;
        if (entry.OpType != OperationType.MoveToQuarantine) return false;
        if (entry.DstPath is null || entry.SrcPath is null) return false;
        if (!File.Exists(LongPath.Fix(entry.DstPath))) return false;

        try
        {
            SafeIo.MoveSafe(entry.DstPath, entry.SrcPath);
        }
        catch
        {
            return false;
        }

        _log.MarkRestored(entry.Id);
        _log.Insert(new OperationLogEntry
        {
            OpType = OperationType.Restore,
            FileId = entry.FileId,
            SrcPath = entry.DstPath,
            DstPath = entry.SrcPath,
            PerformedAt = DateTimeOffset.Now,
            Reversible = false,
            Restored = true,
            SizeBytes = entry.SizeBytes,
            FileName = entry.FileName
        });
        return true;
    }

    public bool RecyclePath(string fullPath)
    {
        bool exists = File.Exists(LongPath.Fix(fullPath)) || Directory.Exists(LongPath.Fix(fullPath));
        if (!exists) return false;

        long? size = null;
        try { if (File.Exists(LongPath.Fix(fullPath))) size = new FileInfo(LongPath.Fix(fullPath)).Length; }
        catch { }

        if (!RecycleBin.MoveToRecycleBin(fullPath)) return false;

        _log.Insert(new OperationLogEntry
        {
            OpType = OperationType.DeleteToRecycle,
            SrcPath = fullPath,
            DstPath = "Корзина Windows",
            PerformedAt = DateTimeOffset.Now,
            Reversible = false,
            SizeBytes = size,
            FileName = Path.GetFileName(fullPath.TrimEnd('\\', '/'))
        });
        return true;
    }

    public bool ReplaceWithHardLink(FileRecord dup, string dupPath, string repPath, long? groupId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (!File.Exists(LongPath.Fix(dupPath)) || !File.Exists(LongPath.Fix(repPath))) return false;
        if (string.Equals(Path.GetFullPath(dupPath), Path.GetFullPath(repPath), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!SafeIo.SameVolume(dupPath, repPath)) return false;

        // Содержимое обязано быть идентичным — иначе это не дубликат и удалять нельзя.
        try { if (!_hash.FilesAreByteEqual(dupPath, repPath)) return false; }
        catch { return false; }

        // Безопасный порядок: сначала создаём ссылку во ВРЕМЕННЫЙ путь (оригинал не трогаем),
        // и только если она создана — удаляем оригинал и атомарно ставим ссылку на его место.
        // Так оригинал не теряется, даже если CreateHardLink не сработает.
        var tmpLink = dupPath + SafeIo.HardLinkSuffix;
        try
        {
            if (File.Exists(LongPath.Fix(tmpLink))) File.Delete(LongPath.Fix(tmpLink));
            if (!CreateHardLinkW(LongPath.Fix(tmpLink), LongPath.Fix(repPath), IntPtr.Zero))
                return false; // ссылка не создана — оригинал на месте, данные целы

            // Атомарная замена оригинала ссылкой (MoveFileEx + REPLACE_EXISTING на одном томе).
            // Нет окна «оригинал удалён, а ссылка ещё не на месте» — оригинал не теряется при сбое.
            File.Move(LongPath.Fix(tmpLink), LongPath.Fix(dupPath), overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("ReplaceWithHardLink", ex);
            // Замена не удалась — оригинал не тронут. Убираем временную ссылку, если осталась.
            try { if (File.Exists(LongPath.Fix(tmpLink))) File.Delete(LongPath.Fix(tmpLink)); } catch { }
            return false;
        }

        _log.Insert(new OperationLogEntry
        {
            OpType = OperationType.Hardlink,
            FileId = dup.Id,
            GroupId = groupId,
            SrcPath = dupPath,
            DstPath = repPath,
            PerformedAt = DateTimeOffset.Now,
            Reversible = false,
            SizeBytes = dup.Size,
            FileName = dup.Name
        });

        // Файл остаётся (как жёсткая ссылка), но из группы дубликатов убираем — место уже не тратится.
        if (groupId is long gid) _groups.RemoveMember(gid, dup.Id);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private string BuildQuarantinePath(FileRecord file)
    {
        // Карантин/<том>/<относительный путь> — уникальность через суффикс при коллизии.
        var rel = file.RelPath.Replace(':', '_');
        var baseDir = Path.Combine(QuarantineDir, $"vol_{file.VolumeId}");
        var target = Path.Combine(baseDir, rel);
        int i = 1;
        while (File.Exists(LongPath.Fix(target)))
        {
            var dir = Path.GetDirectoryName(target) ?? baseDir;
            var nameNoExt = Path.GetFileNameWithoutExtension(file.Name);
            var ext = Path.GetExtension(file.Name);
            target = Path.Combine(dir, $"{nameNoExt}__{i}{ext}");
            i++;
        }
        return target;
    }
}

/// <summary>Перемещение в Корзину Windows через SHFileOperation (FOF_ALLOWUNDO).</summary>
internal static class RecycleBin
{
    public static bool MoveToRecycleBin(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Вне Windows — деградация: оставляем файл, операция считается неуспешной.
            return false;
        }

        // SHFileOperation требует список путей с ДВОЙНЫМ null-терминатором. Формируем его
        // вручную в закреплённом буфере: маршалинг LPWStr не гарантирует сохранение двойного нуля.
        byte[] buffer = Encoding.Unicode.GetBytes(path + "\0\0");
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = handle.AddrOfPinnedObject(),
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING)
            };
            int result = SHFileOperation(ref op);
            return result == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle.Free();
        }
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;   // указатель на буфер с двойным null-терминатором
        public IntPtr pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);
}
