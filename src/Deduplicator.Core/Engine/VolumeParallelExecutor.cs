namespace Deduplicator.Core.Engine;

/// <summary>
/// Запускает работу над набором элементов с параллелизмом НА КАЖДЫЙ ТОМ: разные тома обрабатываются
/// одновременно, но внутри тома — со своим DOP (HDD=1 последовательно, SSD высокий). Глобальный
/// потолок применяется СТАТИЧЕСКИМИ квотами (<see cref="DistributeCap"/>), а не блокирующим
/// семафором: gate.Wait внутри вложенного Parallel.ForEach парковал потоки и раздувал ThreadPool
/// на слабых машинах при мультиномных сканах (этап 1.7 дорожной карты).
/// Единая реализация для exact-воронки и перцептивных этапов.
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
        var requested = byVolume.Select(g => (g.Key, Dop: contentDopFor(g.Key))).ToList();
        var quotas = DistributeCap(requested, GlobalCap(requested.Select(r => r.Dop).ToList()));

        var outer = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(byVolume.Count, maxVolumeParallelism))
        };

        Parallel.ForEach(byVolume, outer, group =>
        {
            int dop = quotas.TryGetValue(group.Key, out var q) ? q : 1;
            var inner = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = dop };
            Parallel.ForEach(group, inner, body);
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

    /// <summary>
    /// Распределить глобальный потолок между томами статическими квотами (без блокировок):
    /// каждая квота ≥ 1, суммарно вписываемся в cap, урезая самые «толстые» тома первыми.
    /// Чистая функция — тестируема. Спроектирована так, чтобы губернатор ресурсов (этап 2.6)
    /// мог динамически сжимать cap между пачками работы.
    /// </summary>
    public static Dictionary<long, int> DistributeCap(IReadOnlyList<(long Key, int Dop)> volumes, int cap)
    {
        var result = new Dictionary<long, int>(volumes.Count);
        foreach (var (key, dop) in volumes) result[key] = Math.Max(1, dop);

        int total = result.Values.Sum();
        while (total > Math.Max(cap, volumes.Count)) // ниже 1 на том не ужимаем
        {
            long topKey = 0;
            int topVal = 1;
            foreach (var kv in result)
                if (kv.Value > topVal) { topKey = kv.Key; topVal = kv.Value; }
            if (topVal <= 1) break;
            result[topKey] = topVal - 1;
            total--;
        }
        return result;
    }
}
