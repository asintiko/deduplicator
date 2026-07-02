namespace Deduplicator.Core.Models;

/// <summary>
/// Слепок файла: компактная запись о файле (метаданные + хэши), без самих данных.
/// Позволяет «помнить» содержимое даже отключённого носителя.
/// </summary>
public sealed class FileRecord
{
    public long Id { get; set; }
    public long VolumeId { get; set; }

    /// <summary>Путь относительно корня тома (без буквы диска).</summary>
    public string RelPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Ext { get; set; }
    public long Size { get; set; }

    /// <summary>Дата изменения (mtime) в ISO-8601 — используется для инкрементальности.</summary>
    public DateTimeOffset Mtime { get; set; }

    /// <summary>Дата создания файла (для отображения пользователю, FR-расширение).</summary>
    public DateTimeOffset Created { get; set; }

    public FileCategory Category { get; set; } = FileCategory.Other;

    public byte[]? PartialHash { get; set; }

    /// <summary>Версия алгоритма частичного хэша (см. HashService.CurrentPartialVersion).</summary>
    public int PartialVer { get; set; } = 1;
    public byte[]? FullHash { get; set; }

    /// <summary>Перцептивный хэш изображения (64-битный dHash/pHash в little-endian).</summary>
    public byte[]? PHash { get; set; }

    /// <summary>Версия алгоритма pHash (см. ImageMatchService.CurrentPHashVersion).</summary>
    public int PHashVer { get; set; } = 1;

    /// <summary>Хэш декодированных пикселей (опц., для одинаковых картинок разного формата).</summary>
    public byte[]? PixelHash { get; set; }

    /// <summary>Аудио-отпечаток (упакованный массив int32 от Chromaprint).</summary>
    public byte[]? AudioFp { get; set; }

    public ScanState ScanState { get; set; } = ScanState.Meta;
    public DateTimeOffset? LastIndexed { get; set; }

    // --- Состояние времени выполнения ---

    /// <summary>Метка тома (заполняется при выборках для отображения).</summary>
    public string? VolumeLabel { get; set; }

    /// <summary>Точка монтирования тома, если он подключён сейчас.</summary>
    public string? VolumeMountPath { get; set; }

    /// <summary>Полный путь на подключённом носителе (с длинным префиксом не содержит).</summary>
    public string? FullPathOnConnectedVolume =>
        VolumeMountPath is null ? null : System.IO.Path.Combine(VolumeMountPath, RelPath);
}
