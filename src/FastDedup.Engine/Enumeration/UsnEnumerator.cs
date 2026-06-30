using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace FastDedup.Engine;

/// <summary>
/// Быстрое перечисление через прямое чтение MFT (FSCTL_ENUM_USN_DATA).
/// Вместо обхода дерева читаем одну структуру тома и восстанавливаем пути по
/// ссылкам «ребёнок -> родитель». На миллионе файлов это секунды, а не минуты.
/// Требует прав администратора и тома NTFS. При сбое — прозрачный откат на обычный обход.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsnEnumerator : IFileEnumerator
{
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFFUL; // младшие 48 бит = номер записи MFT
    private const ulong RootRecord = 5UL;                      // корневой каталог тома — запись №5

    internal readonly record struct Rec(ulong Parent, uint Attrs, string Name);

    /// <summary>
    /// IFileEnumerator-контракт (используется CLI). Здесь сохраняем СТАРОЕ поведение: при сбое MFT
    /// откатываемся на обычный обход. В приложении же путь идёт через <see cref="FastFileLister"/>,
    /// который ловит <see cref="MftEnumerationUnavailableException"/> и переходит на Win32Find.
    /// </summary>
    public IEnumerable<string> EnumerateFiles(
        string root, ScanOptions options, ConcurrentBag<ScanError> errors, CancellationToken ct)
    {
        Dictionary<ulong, Rec> map;
        string drive;
        try
        {
            (map, drive) = ReadMap(root, ct);
        }
        catch (MftEnumerationUnavailableException ex)
        {
            errors.Add(new ScanError(root, "MFT недоступен, откат на обычный обход: " + ex.Message));
            return new FallbackEnumerator().EnumerateFiles(root, options, errors, ct);
        }
        return BuildPaths(map, drive, root, options, ct);
    }

    /// <summary>Построить карту MFT или бросить <see cref="MftEnumerationUnavailableException"/> с причиной.</summary>
    internal static (Dictionary<ulong, Rec> map, string drive) ReadMap(string root, CancellationToken ct)
    {
        try
        {
            return ReadMft(root, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (NotSupportedException ex)
        {
            throw new MftEnumerationUnavailableException(BackendFallbackReason.UnsupportedUsnRecord, ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            throw new MftEnumerationUnavailableException(BackendFallbackReason.AccessDenied, ex.Message);
        }
        catch (Exception ex)
        {
            throw new MftEnumerationUnavailableException(BackendFallbackReason.ApiError, ex.Message);
        }
    }

    /// <summary>Восстановить пути файлов из готовой карты MFT (в пределах корня скана).</summary>
    internal static IEnumerable<string> BuildPaths(
        Dictionary<ulong, Rec> map, string drive, string root, ScanOptions options, CancellationToken ct)
    {
        string rootPrefix = NormalizeRoot(root);
        var dirCache = new Dictionary<ulong, string?>();

        foreach (var kv in map)
        {
            ct.ThrowIfCancellationRequested();
            var rec = kv.Value;

            if ((rec.Attrs & Native.FILE_ATTRIBUTE_DIRECTORY) != 0) continue; // каталоги пропускаем
            if (!options.IncludeReparsePoints && (rec.Attrs & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                continue;

            string? dir = BuildDir(rec.Parent, map, dirCache, drive);
            if (dir == null) continue; // путь не восстановился (битая/служебная запись)

            string full = dir.EndsWith('\\') ? dir + rec.Name : dir + "\\" + rec.Name;

            if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                !ScanFilters.IsExcludedByPath(full, options))
                yield return full;
        }
    }

    /// <summary>Читаем все записи MFT тома в словарь FRN -> (родитель, атрибуты, имя).</summary>
    private static (Dictionary<ulong, Rec> map, string drive) ReadMft(string root, CancellationToken ct)
    {
        string drive = VolumeOf(root);                 // "C:"
        string devicePath = @"\\.\" + drive;

        using var h = Native.OpenVolumeRead(devicePath);
        var map = new Dictionary<ulong, Rec>(capacity: 1 << 20);
        long v2 = 0, unsupported = 0;

        const int OutSize = 1 << 20; // 1 МБ за вызов
        IntPtr inBuf = Marshal.AllocHGlobal(24);  // MFT_ENUM_DATA_V0
        IntPtr outBuf = Marshal.AllocHGlobal(OutSize);
        var managed = new byte[OutSize];
        try
        {
            ulong start = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                Marshal.WriteInt64(inBuf, 0, unchecked((long)start)); // StartFileReferenceNumber
                Marshal.WriteInt64(inBuf, 8, 0L);                     // LowUsn
                Marshal.WriteInt64(inBuf, 16, long.MaxValue);         // HighUsn

                bool ok = Native.DeviceIoControl(
                    h, Native.FSCTL_ENUM_USN_DATA, inBuf, 24, outBuf, OutSize,
                    out uint bytesReturned, IntPtr.Zero);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Native.ERROR_HANDLE_EOF) break; // дошли до конца MFT
                    throw new System.ComponentModel.Win32Exception(err, $"FSCTL_ENUM_USN_DATA ({err})");
                }
                if (bytesReturned <= 8) break; // только указатель «следующий», записей нет

                Marshal.Copy(outBuf, managed, 0, (int)bytesReturned);

                // Первые 8 байт — следующий StartFileReferenceNumber.
                start = BinaryPrimitives.ReadUInt64LittleEndian(managed.AsSpan(0, 8));

                ParseRecords(managed, (int)bytesReturned, map, ref v2, ref unsupported);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        // Режим «без потерь»: если встретилась ХОТЯ БЫ ОДНА запись неподдерживаемой версии (не V2),
        // парсер не гарантирует полный список → откатываемся на обычный обход целиком, а не пропускаем
        // часть файлов молча (раздел P0 аудита: лучше fallback, чем partial success).
        if (unsupported > 0)
            throw new NotSupportedException(
                $"USN: встретилось {unsupported} записей неподдерживаемой версии (из {v2 + unsupported}) — " +
                "откат на обычный обход, чтобы не потерять файлы.");

        return (map, drive);
    }

    /// <summary>
    /// Разбор последовательности USN-записей. Полноценно поддерживаем USN_RECORD_V2 (его и отдаёт
    /// FSCTL_ENUM_USN_DATA с входом V0 на NTFS). Любую другую версию НЕ теряем молча, а считаем —
    /// вызывающий решает, не пора ли откатиться на обычный обход.
    /// </summary>
    private static void ParseRecords(byte[] buf, int length, Dictionary<ulong, Rec> map,
        ref long v2, ref long unsupported)
    {
        int offset = 8; // пропускаем «следующий FRN»
        while (offset + 60 <= length)
        {
            var span = buf.AsSpan(offset);
            uint recLen = BinaryPrimitives.ReadUInt32LittleEndian(span);
            if (recLen == 0 || offset + (int)recLen > length) break;

            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4));
            if (major == 2)
            {
                v2++;
                ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(8));
                ulong pfrn = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(16));
                uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(52));
                ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(56));
                ushort nameOff = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(58));

                if (nameOff + nameLen <= recLen && nameLen > 0)
                {
                    string name = Encoding.Unicode.GetString(span.Slice(nameOff, nameLen));
                    map[frn] = new Rec(pfrn, attrs, name);
                }
            }
            else
            {
                // major == 3 (128-битные FRN) и прочие версии для входа V0 на NTFS не приходят;
                // если всё же встретились — считаем как неподдерживаемые (см. порог отката в ReadMft).
                unsupported++;
            }

            offset += (int)recLen;
        }
    }

    /// <summary>Рекурсивно (с мемоизацией) строим путь каталога по цепочке родителей.</summary>
    private static string? BuildDir(ulong frn, Dictionary<ulong, Rec> map,
        Dictionary<ulong, string?> cache, string drive)
    {
        if ((frn & RecordMask) == RootRecord) return drive + "\\"; // корень тома, напр. "C:\"
        if (cache.TryGetValue(frn, out var cached)) return cached;
        if (!map.TryGetValue(frn, out var rec)) { cache[frn] = null; return null; }

        cache[frn] = null; // временно — разрывает возможные циклы
        string? parent = BuildDir(rec.Parent, map, cache, drive);
        string? result = parent == null
            ? null
            : (parent.EndsWith('\\') ? parent + rec.Name : parent + "\\" + rec.Name);
        cache[frn] = result;
        return result;
    }

    private static string VolumeOf(string fullPath)
    {
        var rootPath = Path.GetPathRoot(Path.GetFullPath(fullPath));
        if (rootPath != null && rootPath.Length >= 2 && rootPath[1] == ':')
            return rootPath.Substring(0, 2);
        throw new NotSupportedException($"MFT-режим поддерживает только локальные тома с буквой диска: '{fullPath}'");
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return full.EndsWith('\\') ? full : full + "\\";
    }
}
