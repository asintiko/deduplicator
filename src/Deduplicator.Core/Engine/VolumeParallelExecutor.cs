namespace Deduplicator.Core.Engine;

/// <summary>
/// Запускает работу над набором элементов с параллелизмом НА КАЖДЫЙ ТОМ: разные тома обрабатываются
/// одновременно, но внутри тома — со своим DOP (HDD=1 последовательно, SSD высокий). Общий
/// <see cref="SemaphoreSlim"/>-потолок не даёт суммарному числу одновременных чтений разрастаться при
/// множестве томов. Единая реализация для exact-воронки и перцептивных этапов.
/// </summary>
public static class VolumeParallelExecutor
{
    public static void RunByVolume<T>(
        IReadOnlyCollection<T> items,
        Func<T, long> volumeKey,
        Func<long, int> contentDopFor,
        int maxVolumeParallelism,
        CancellationToken ct,
        Action<T> body)
    {
        if (items.Count == 0) return;

        var byVolume = items.GroupBy(volumeKey).ToList();
        using var gate = new SemaphoreSlim(GlobalCap(byVolume.Select(g => contentDopFor(g.Key)).ToList()));

        var outer = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(byVolume.Count, maxVolumeParallelism))
        };

        Parallel.ForEach(byVolume, outer, group =>
        {
            int dop = Math.Max(1, contentDopFor(group.Key));
            var inner = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = dop };
            Parallel.ForEach(group, inner, item =>
            {
                gate.Wait(ct);
                try { body(item); }
                finally { gate.Release(); }
            });
        });
    }

    /// <summary>
    /// Глобальный потолок одновременных чтений содержимого по всем томам. Базово — СУММА DOP томов
    /// (каждый том читает на своей глубине), но ограничен сверху 2× числом ядер, чтобы множество томов
    /// не вызывало переподписку; при этом не ниже DOP самого «толстого» тома. Чистая функция — тестируема.
    /// </summary>
    public static int GlobalCap(IReadOnlyCollection<int> perVolumeDops)
    {
        if (perVolumeDops.Count == 0) return 1;
        int maxSingle = perVolumeDops.Max(d => Math.Max(1, d));
        int sum = perVolumeDops.Sum(d => Math.Max(1, d));
        return Math.Max(1, Math.Min(sum, Math.Max(maxSingle, Environment.ProcessorCount * 2)));
    }
}
