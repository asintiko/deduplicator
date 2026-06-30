namespace Deduplicator.Core.Models;

/// <summary>
/// Носитель (том): отдельное физическое или логическое хранилище, идентифицируемое
/// серийным номером тома. Хранится в каталоге даже после отключения.
/// </summary>
public sealed class VolumeRecord
{
    public long Id { get; set; }

    /// <summary>Метка тома (например, «Архив фото»).</summary>
    public string? Label { get; set; }

    /// <summary>Серийный номер тома — устойчивый идентификатор носителя.</summary>
    public string? Serial { get; set; }

    /// <summary>Тип файловой системы (NTFS, exFAT, ...).</summary>
    public string? FsType { get; set; }

    /// <summary>Общий объём носителя в байтах.</summary>
    public long TotalBytes { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset? LastScanned { get; set; }

    // --- Состояние времени выполнения (в БД не хранится) ---

    /// <summary>Подключён ли носитель прямо сейчас.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Текущая точка монтирования (буква диска), если подключён.</summary>
    public string? MountPath { get; set; }

    /// <summary>Свободно байт сейчас (если подключён).</summary>
    public long? FreeBytes { get; set; }

    /// <summary>Количество проиндексированных файлов (для сводки каталога).</summary>
    public long IndexedFileCount { get; set; }

    /// <summary>Съёмный носитель (USB/флешка).</summary>
    public bool IsRemovable { get; set; }

    /// <summary>Сетевой/подключённый диск (UNC/маппинг). Скорость зависит от сети (P1.4).</summary>
    public bool IsNetwork { get; set; }

    /// <summary>Тип носителя по WMI: 3 = HDD, 4 = SSD, 0 = неизвестно.</summary>
    public int MediaType { get; set; }

    public bool IsHdd => MediaType == 3;
    public bool IsSsd => MediaType == 4;

    /// <summary>Для HDD, съёмных и сетевых носителей чтение сериализуется (NFR-7, P1.4).</summary>
    public bool PreferSequentialReads => IsHdd || IsRemovable || IsNetwork;

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Label) ? (MountPath ?? Serial ?? $"Том #{Id}") : Label!;
            return IsNetwork ? name + " (сетевой)" : name;
        }
    }
}
