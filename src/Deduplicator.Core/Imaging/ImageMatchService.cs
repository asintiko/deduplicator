using System.IO.Hashing;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using Deduplicator.Core.Util;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Deduplicator.Core.Imaging;

public interface IImageMatchService
{
    /// <summary>Перцептивный хэш изображения (64-битный pHash). null — если файл не читается.</summary>
    byte[]? ComputePHash(string fullPath, CancellationToken ct = default);

    /// <summary>Хэш декодированных пикселей (для одинаковых картинок с разными метаданными).</summary>
    byte[]? ComputePixelHash(string fullPath, CancellationToken ct = default);

    /// <summary>Сгенерировать (если нужно) миниатюру и вернуть путь к кэшированному файлу.</summary>
    string? EnsureThumbnail(string fullPath, string cacheKey, int maxSize = 256);
}

/// <summary>pHash, pixel-hash и миниатюры на ImageSharp (полностью управляемый код).</summary>
public sealed class ImageMatchService : IImageMatchService
{
    private static readonly IImageHash HashAlgo = new PerceptualHash();
    private readonly string _thumbDir;
    private readonly bool _rotationInvariant;

    public ImageMatchService(string thumbnailCacheDir, bool rotationInvariant = true)
    {
        _thumbDir = thumbnailCacheDir;
        _rotationInvariant = rotationInvariant;
        Directory.CreateDirectory(_thumbDir);
    }

    public byte[]? ComputePHash(string fullPath, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fs = OpenRead(fullPath);

            if (!_rotationInvariant)
                return BitConverter.GetBytes(HashAlgo.Hash(fs));

            // Канонический хэш = минимум по ориентациям (повороты + зеркало).
            // Тогда повёрнутые/отражённые копии дают одинаковое значение и группируются.
            using var img = Image.Load<Rgba32>(fs);
            // Полная диэдральная группа: 4 поворота × 2 отражения = 8 ориентаций.
            ulong best = HashAlgo.Hash(img);
            best = Math.Min(best, HashOriented(img, RotateMode.Rotate90, FlipMode.None));
            best = Math.Min(best, HashOriented(img, RotateMode.Rotate180, FlipMode.None));
            best = Math.Min(best, HashOriented(img, RotateMode.Rotate270, FlipMode.None));
            best = Math.Min(best, HashOriented(img, RotateMode.None, FlipMode.Horizontal));
            best = Math.Min(best, HashOriented(img, RotateMode.Rotate90, FlipMode.Horizontal));
            best = Math.Min(best, HashOriented(img, RotateMode.Rotate180, FlipMode.Horizontal));
            best = Math.Min(best, HashOriented(img, RotateMode.Rotate270, FlipMode.Horizontal));
            return BitConverter.GetBytes(best);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // битый/нечитаемый файл — пропускаем (NFR-1)
    }

    private static ulong HashOriented(Image<Rgba32> img, RotateMode rotate, FlipMode flip)
    {
        using var clone = img.Clone(c => c.RotateFlip(rotate, flip));
        return HashAlgo.Hash(clone);
    }

    public byte[]? ComputePixelHash(string fullPath, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fs = OpenRead(fullPath);
            using var img = Image.Load<Rgba32>(fs);
            long n = (long)img.Width * img.Height * 4;
            if (n <= 0 || n > int.MaxValue) return null;
            var buffer = new byte[(int)n];
            img.CopyPixelDataTo(buffer);
            return XxHash128.Hash(buffer);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public string? EnsureThumbnail(string fullPath, string cacheKey, int maxSize = 256)
    {
        var safeKey = MakeSafe(cacheKey);
        var thumbPath = Path.Combine(_thumbDir, $"{safeKey}_{maxSize}.jpg");
        if (File.Exists(thumbPath)) return thumbPath;

        // Пишем во временный файл и атомарно переименовываем: сбой при кодировании не оставит
        // «обрезанный» .jpg, который при следующем вызове вернулся бы как валидный.
        var tmp = thumbPath + "." + Guid.NewGuid().ToString("N") + SafeIo.TempSuffix;
        try
        {
            using (var fs = OpenRead(fullPath))
            using (var img = Image.Load(fs))
            {
                img.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxSize, maxSize),
                    Mode = ResizeMode.Max
                }));
                using var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                img.SaveAsJpeg(outFs);
            }
            File.Move(tmp, thumbPath, overwrite: true); // атомарная публикация в кэш
            return thumbPath;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return null;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private static string MakeSafe(string key)
    {
        Span<char> buf = stackalloc char[key.Length];
        for (int i = 0; i < key.Length; i++)
            buf[i] = char.IsLetterOrDigit(key[i]) ? key[i] : '_';
        return new string(buf);
    }
}
