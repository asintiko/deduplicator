using Avalonia.Media;
using Deduplicator.App.Infrastructure;
using Deduplicator.App.Localization;
using Deduplicator.Core.Models;

namespace Deduplicator.App.ViewModels.Items;

/// <summary>Строка разбивки по категориям для диаграммы статистики.</summary>
public sealed class CategoryStat
{
    public FileCategory Category { get; init; }
    public long Count { get; init; }
    public long Bytes { get; init; }
    public double Fraction { get; init; }

    public string Name => Strings.Category(Category);
    public string CountText => $"{Count:N0} {Strings.Files}";
    public string BytesText => Format.Bytes(Bytes);
    public double BarWidth => System.Math.Max(2, Fraction * 280);

    public IBrush Color => Category switch
    {
        FileCategory.Image => new SolidColorBrush(Avalonia.Media.Color.Parse("#007AFF")),
        FileCategory.Audio => new SolidColorBrush(Avalonia.Media.Color.Parse("#34C759")),
        FileCategory.Video => new SolidColorBrush(Avalonia.Media.Color.Parse("#FF9500")),
        FileCategory.Document => new SolidColorBrush(Avalonia.Media.Color.Parse("#AF52DE")),
        _ => new SolidColorBrush(Avalonia.Media.Color.Parse("#8E8E93"))
    };
}
