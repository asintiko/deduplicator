using Deduplicator.Core.Data;
using Deduplicator.Core.Hashing;
using Deduplicator.Core.Models;
using Deduplicator.Core.Util;

namespace Deduplicator.Core.Matching;

public interface IDuplicateGrouper
{
    /// <param name="inScope">
    /// Предикат «файл входит в текущую область сканирования». Группа строится ТОЛЬКО из in-scope
    /// файлов — результат соответствует тому, что пользователь выбрал сейчас, и не подмешивает
    /// файлы из старого индекса/других дисков (раздел P0 аудита: scope-aware grouping).
    /// </param>
    int BuildExactGroups(IReadOnlyDictionary<long, VolumeRecord> volumes, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0);

    int BuildSimilarImageGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0);

    int BuildSimilarAudioGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0);

    /// <summary>Удалить группы прошлых сессий, оставив только текущую (P0.1 scope-aware результаты).</summary>
    void ClearGroupsNotInSession(long sessionId);

    /// <summary>Удалить частично построенные группы текущей сессии (при отмене/сбое скана).</summary>
    void ClearGroupsInSession(long sessionId);
}

/// <summary>Формирование групп точных и похожих дубликатов (раздел 7).</summary>
public sealed class DuplicateGrouper : IDuplicateGrouper
{
    private const int FlushEvery = 500;
    private const int BucketPairCap = 1500;
    // Для умеренных наборов полный попарный перебор даёт 100% полноту (LSH может пропускать
    // пары с расстоянием > 7 бит). Для больших наборов переходим на LSH ради скорости.
    private const int FullPairwiseCap = 5000;

    private readonly FileRepository _files;
    private readonly GroupRepository _groups;
    private readonly LshRepository _lsh;
    private readonly IHashService _hash;

    public DuplicateGrouper(FileRepository files, GroupRepository groups, LshRepository lsh, IHashService hash)
    {
        _files = files;
        _groups = groups;
        _lsh = lsh;
        _hash = hash;
    }

    public void ClearGroupsNotInSession(long sessionId) => _groups.ClearGroupsNotInSession(sessionId);
    public void ClearGroupsInSession(long sessionId) => _groups.ClearGroupsInSession(sessionId);

    // ---------------- Точные дубликаты ----------------

    public int BuildExactGroups(IReadOnlyDictionary<long, VolumeRecord> volumes, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0)
    {
        // Чистим ТОЛЬКО точные группы текущей сессии (а не глобально): иначе пересборка в идущем скане
        // стирала бы результаты прошлой ЗАВЕРШЁННОЙ сессии ещё до того, как текущий скан успешно завершится
        // (раздел «Preserve previous completed results on cancel/fail»). Чистка прошлых сессий — только на успехе.
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.Exact);
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.ExactVideo);

        var pending = new List<DuplicateGroup>();
        int created = 0;

        foreach (var hash in _files.GetSharedFullHashes())
        {
            ct.ThrowIfCancellationRequested();
            // Scope-aware: группу строим ТОЛЬКО из файлов текущей области. Файлы с тем же хэшем из
            // старого индекса/других дисков сюда не попадают, поэтому результат соответствует именно
            // тому, что пользователь выбрал сейчас (нельзя удалить то, что не сканировали).
            var files = _files.GetByFullHash(hash).Where(inScope).ToList();
            if (files.Count < 2) continue;

            foreach (var vc in ByteVerifyClusters(files, volumes, ct))
            {
                if (vc.Members.Count < 2) continue;

                // Hardlink-aware: схлопываем алиасы (один физический файл под несколькими путями),
                // иначе reclaimable завышается, а ссылки показываются как «лишние копии».
                var (physical, linkCounts) = CollapseHardlinks(vc.Members, volumes);
                if (physical.Count < 2) continue; // вся группа — hardlink-и на один файл: удалять нечего

                var rep = RepresentativeSelector.Pick(physical, rule);
                bool isVideo = physical[0].Category == FileCategory.Video;

                // Освобождаемое место: размер не-эталонного файла считаем ТОЛЬКО если у его inode нет
                // других ссылок (LinkCount<=1). Если файл — один из нескольких hardlink-ов (в т.ч.
                // схлопнутых или внешних), удаление одного пути не освободит блоки — не завышаем оценку.
                long reclaimable = 0;
                foreach (var f in physical)
                {
                    if (f.Id == rep.Id) continue;
                    bool freeable = !linkCounts.TryGetValue(f.Id, out var lc) || lc <= 1;
                    if (freeable) reclaimable += f.Size;
                }

                var group = new DuplicateGroup
                {
                    MatchType = isVideo ? MatchType.ExactVideo : MatchType.Exact,
                    // Полностью сверенные байт-в-байт группы — 1.0; hash-only (есть члены, которые
                    // нельзя было прочитать/сверить) помечаем < 1.0, чтобы их нельзя было автоудалять.
                    Confidence = vc.ByteVerified ? 1.0 : HashOnlyExactConfidence,
                    RepresentativeId = rep.Id,
                    ReclaimableBytes = reclaimable
                };
                foreach (var f in physical)
                    group.Members.Add(new DuplicateMember { FileId = f.Id, Similarity = 1.0, File = f });

                pending.Add(group);
                created++;
            }

            if (pending.Count >= FlushEvery)
            {
                _groups.InsertGroups(pending, sessionId);
                pending.Clear();
                onGroups?.Invoke(created);
            }
        }
        if (pending.Count > 0) _groups.InsertGroups(pending, sessionId);
        onGroups?.Invoke(created);
        return created;
    }

    private const double HashOnlyExactConfidence = 0.99;

    /// <summary>Кластер-кандидат точных дубликатов и признак того, что он сверен байт-в-байт.</summary>
    private sealed record VerifiedCluster(List<FileRecord> Members, bool ByteVerified);

    /// <summary>
    /// Кластеризация файлов с совпавшим полным хэшем побайтовой сверкой (FR-11).
    ///
    /// Якорем кластера и участником сверки может быть ТОЛЬКО подключённый и реально читаемый файл.
    /// Это закрывает две опасные дыры предыдущей версии (сверка только с «головой» кластера):
    ///   • нечитаемый файл-голова «отравлял» всю корзину и ложно разрывал настоящую пару;
    ///   • офлайн-голова коротко замыкала сверку и могла ложно объединить два РАЗНЫХ подключённых
    ///     файла в группу с confidence 1.0 (риск удаления непохожих данных при коллизии хэша).
    ///
    /// Подключённые читаемые файлы кластеризуются строгой попарной сверкой друг с другом. Офлайн/
    /// нечитаемые файлы нельзя проверить — они подвешиваются как hash-only члены (группа получает
    /// confidence &lt; 1.0 и не подлежит автоудалению).
    /// </summary>
    private List<VerifiedCluster> ByteVerifyClusters(
        List<FileRecord> files, IReadOnlyDictionary<long, VolumeRecord> volumes, CancellationToken ct)
    {
        var readable = new List<(FileRecord Rec, string Path)>();
        var unverifiable = new List<FileRecord>(); // отключён ИЛИ подключён, но не читается
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Resolve(f, volumes);
            if (path is not null && File.Exists(LongPath.Fix(path)) && IsReadable(path))
                readable.Add((f, path));
            else
                unverifiable.Add(f);
        }

        // Подключённые читаемые: кластеризуем строгой попарной сверкой. Нечитаемый файл сюда не
        // попадает, поэтому не может быть «головой» и испортить решение для остальных.
        var clusters = new List<List<FileRecord>>();
        var headPaths = new List<string>();
        foreach (var (rec, path) in readable)
        {
            ct.ThrowIfCancellationRequested();
            bool placed = false;
            for (int i = 0; i < clusters.Count; i++)
            {
                bool equal;
                try { equal = _hash.FilesAreByteEqual(headPaths[i], path, ct); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Транзиентная ошибка чтения уже проверенного как читаемый файла — не объединяем
                    // с этим кластером, пробуем следующий (файл не теряем и не якорим ложно).
                    AppLog.Write($"Побайтовая сверка не удалась для «{path}» — не объединяем с кластером.");
                    continue;
                }
                if (equal) { clusters[i].Add(rec); placed = true; break; }
            }
            if (!placed) { clusters.Add(new List<FileRecord> { rec }); headPaths.Add(path); }
        }

        var result = clusters.Select(c => new VerifiedCluster(c, ByteVerified: true)).ToList();

        // Офлайн/непроверяемые (тот же full_hash): сверить нечем. Подвешиваем к первому байт-сверенному
        // кластеру как hash-only (FR-40/41 кросс-том), понижая его до неверифицированного; если сверенных
        // кластеров нет — формируем отдельную hash-only группу. Так офлайн-файл НИКОГДА не объединяет
        // два подключённых файла без прямой сверки.
        if (unverifiable.Count > 0)
        {
            if (result.Count > 0)
            {
                result[0].Members.AddRange(unverifiable);
                result[0] = result[0] with { ByteVerified = false };
            }
            else if (unverifiable.Count >= 2)
            {
                result.Add(new VerifiedCluster(new List<FileRecord>(unverifiable), ByteVerified: false));
            }
        }
        return result;
    }

    /// <summary>Можно ли реально прочитать файл (а не только что он «существует»). Дешёвая проба 1 байта.</summary>
    private static bool IsReadable(string path)
    {
        try
        {
            using var fs = new FileStream(LongPath.Fix(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> one = stackalloc byte[1];
            fs.Read(one); // 0 для пустого файла — это тоже «читаем»
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Схлопывает hardlink-алиасы внутри кластера: несколько путей к ОДНОМУ физическому файлу
    /// (одинаковые VolumeSerial+FileIndex) считаются одной копией. Так освобождаемое место не
    /// завышается, а ссылки не показываются как «лишние копии». Идентичность есть только у
    /// подключённых файлов; недоступные считаем отдельными (консервативно).
    /// </summary>
    private static (List<FileRecord> Physical, Dictionary<long, uint> LinkCounts) CollapseHardlinks(
        List<FileRecord> cluster, IReadOnlyDictionary<long, VolumeRecord> volumes)
    {
        var linkCounts = new Dictionary<long, uint>();
        if (!OperatingSystem.IsWindows() || cluster.Count < 2) return (cluster, linkCounts);

        var seen = new HashSet<(uint VolumeSerial, ulong FileIndex)>();
        var physical = new List<FileRecord>(cluster.Count);
        foreach (var f in cluster)
        {
            var path = Resolve(f, volumes);
            if (path is not null &&
                FastDedup.Engine.FileIdentityReader.TryGet(LongPath.Fix(path), out var id))
            {
                if (!seen.Add((id.VolumeSerialNumber, id.FileIndex)))
                    continue; // тот же физический файл — это алиас, не отдельная копия
                linkCounts[f.Id] = id.LinkCount; // число hardlink-ов на этот inode (для расчёта reclaimable)
            }
            physical.Add(f);
        }
        return (physical, linkCounts);
    }

    // ---------------- Похожие изображения ----------------

    public int BuildSimilarImageGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0)
    {
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.SimilarImage);
        _lsh.ClearKind(MatchType.SimilarImage);

        // Scope-aware: похожие ищем только среди файлов текущей области (не по всей базе) — фильтр scope-aware
        // SQL по сессии + предикат inScope как страховка.
        var imgs = _files.GetByCategoryWithPerceptual(FileCategory.Image, sessionId).Where(inScope).ToList();
        if (imgs.Count < 2) return 0;

        var byId = imgs.ToDictionary(f => f.Id);
        var hashOf = new Dictionary<long, ulong>(imgs.Count);
        foreach (var f in imgs) hashOf[f.Id] = PerceptualLsh.ToHash(f.PHash);

        var dsu = new Dsu();
        foreach (var f in imgs) dsu.MakeSet(f.Id);

        // 1) Точное совпадение pixel-hash → одинаковое изображение, иные метаданные (FR-14).
        UnionByExactBlob(imgs, f => f.PixelHash, dsu);
        // 2) Точное совпадение pHash — дёшево объединяем.
        UnionByExactHash(imgs, hashOf, dsu);

        // 3) Корзины LSH.
        var buckets = new Dictionary<(int, long), List<long>>();
        var lshRows = new List<(int, long, long)>();
        foreach (var f in imgs)
        {
            var h = hashOf[f.Id];
            foreach (var (band, key) in PerceptualLsh.BandKeys(h))
            {
                var bk = (band, key);
                if (!buckets.TryGetValue(bk, out var list)) buckets[bk] = list = new List<long>();
                list.Add(f.Id);
                lshRows.Add((band, key, f.Id));
            }
        }
        _lsh.InsertBuckets(MatchType.SimilarImage, lshRows);

        var allDistinct = DistinctByHash(imgs.Select(f => f.Id).ToList(), hashOf);
        if (allDistinct.Count <= FullPairwiseCap)
        {
            // Полный перебор — гарантированная полнота для типовых библиотек.
            for (int i = 0; i < allDistinct.Count; i++)
            for (int j = i + 1; j < allDistinct.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                if (PerceptualLsh.Similarity(hashOf[allDistinct[i]], hashOf[allDistinct[j]]) >= threshold)
                    dsu.Union(allDistinct[i], allDistinct[j]);
            }
        }
        else
        {
            // Большой набор — кандидаты только из общих корзин LSH. Крупные корзины НЕ отбрасываем
            // (это раньше приводило к молчаливому пропуску дубликатов), а дробим по доп. полосам.
            foreach (var (_, list) in buckets)
            {
                ct.ThrowIfCancellationRequested();
                var distinct = DistinctByHash(list, hashOf);
                if (distinct.Count < 2) continue;
                UnionSimilarInBucket(distinct, BucketPairCap,
                    (a, b) => PerceptualLsh.Similarity(hashOf[a], hashOf[b]) >= threshold,
                    (id, level) => PerceptualLsh.BandKey(hashOf[id], level), PerceptualLsh.Bands,
                    dsu, ct, "image");
            }
        }

        return PersistSimilarGroups(dsu, byId, MatchType.SimilarImage, rule, onGroups,
            (rep, member) => SimilarityImage(hashOf, rep, member), ct, sessionId);
    }

    private static double SimilarityImage(Dictionary<long, ulong> hashOf, FileRecord rep, FileRecord m)
    {
        if (rep.PixelHash is not null && m.PixelHash is not null && rep.PixelHash.AsSpan().SequenceEqual(m.PixelHash))
            return 1.0;
        return PerceptualLsh.Similarity(hashOf[rep.Id], hashOf[m.Id]);
    }

    // ---------------- Похожая музыка ----------------

    public int BuildSimilarAudioGroups(double threshold, Func<FileRecord, bool> inScope,
        RepresentativeRule rule, Action<long>? onGroups, CancellationToken ct, long sessionId = 0)
    {
        _groups.ClearByMatchTypeInSession(sessionId, MatchType.SimilarAudio);
        _lsh.ClearKind(MatchType.SimilarAudio);

        // Scope-aware: похожие ищем только среди файлов текущей области (не по всей базе) — scope-aware SQL
        // по сессии + предикат inScope как страховка.
        var tracks = _files.GetByCategoryWithPerceptual(FileCategory.Audio, sessionId).Where(inScope).ToList();
        if (tracks.Count < 2) return 0;

        var byId = tracks.ToDictionary(f => f.Id);
        var minhash = new MinHasher();
        var sigOf = new Dictionary<long, uint[]>(tracks.Count);
        foreach (var f in tracks)
        {
            var set = Deduplicator.Core.Audio.AudioFingerprint.ToSet(f.AudioFp!);
            sigOf[f.Id] = minhash.Signature(set);
        }

        var dsu = new Dsu();
        foreach (var f in tracks) dsu.MakeSet(f.Id);

        // Идентичные отпечатки (та же запись в разных контейнерах с тем же потоком) — объединяем сразу,
        // как и для изображений с одинаковым pHash. Делает обработку согласованной image/audio.
        UnionByExactBlob(tracks, f => f.AudioFp, dsu);

        var buckets = new Dictionary<(int, long), List<long>>();
        var lshRows = new List<(int, long, long)>();
        foreach (var f in tracks)
        {
            foreach (var (band, key) in MinHasher.BandKeys(sigOf[f.Id]))
            {
                var bk = (band, key);
                if (!buckets.TryGetValue(bk, out var list)) buckets[bk] = list = new List<long>();
                list.Add(f.Id);
                lshRows.Add((band, key, f.Id));
            }
        }
        _lsh.InsertBuckets(MatchType.SimilarAudio, lshRows);

        var allIds = tracks.Select(f => f.Id).ToList();
        if (allIds.Count <= FullPairwiseCap)
        {
            for (int i = 0; i < allIds.Count; i++)
            for (int j = i + 1; j < allIds.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                if (EstimatedJaccard(sigOf[allIds[i]], sigOf[allIds[j]]) >= threshold)
                    dsu.Union(allIds[i], allIds[j]);
            }
        }
        else
        {
            foreach (var (_, list) in buckets)
            {
                ct.ThrowIfCancellationRequested();
                if (list.Count < 2) continue;
                UnionSimilarInBucket(list, BucketPairCap,
                    (a, b) => EstimatedJaccard(sigOf[a], sigOf[b]) >= threshold,
                    (id, level) => MinHasher.BandKey(sigOf[id], level), MinHasher.Bands,
                    dsu, ct, "audio");
            }
        }

        return PersistSimilarGroups(dsu, byId, MatchType.SimilarAudio, rule, onGroups,
            (rep, member) => EstimatedJaccard(sigOf[rep.Id], sigOf[member.Id]), ct, sessionId);
    }

    private static double EstimatedJaccard(uint[] a, uint[] b)
    {
        int eq = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) eq++;
        return eq / (double)a.Length;
    }

    // ---------------- Общее для похожих ----------------

    private int PersistSimilarGroups(Dsu dsu, Dictionary<long, FileRecord> byId,
        MatchType type, RepresentativeRule rule, Action<long>? onGroups,
        Func<FileRecord, FileRecord, double> similarity, CancellationToken ct, long sessionId)
    {
        var pending = new List<DuplicateGroup>();
        int created = 0;

        foreach (var (_, ids) in dsu.Groups())
        {
            ct.ThrowIfCancellationRequested();
            if (ids.Count < 2) continue;
            var members = ids.Select(id => byId[id]).ToList();
            var rep = RepresentativeSelector.Pick(members, rule);

            var group = new DuplicateGroup
            {
                MatchType = type,
                RepresentativeId = rep.Id,
                // Похожие — НЕ точные копии. «Освобождаемое место» здесь не считаем (P0.3): иначе
                // «можно освободить X ГБ» подталкивает к удалению неодинаковых файлов. Экономия
                // станет известна только после ручного выбора в UI.
                ReclaimableBytes = 0
            };

            double sumSim = 0;
            foreach (var m in members)
            {
                double sim = m.Id == rep.Id ? 1.0 : Math.Clamp(similarity(rep, m), 0, 1);
                sumSim += sim;
                group.Members.Add(new DuplicateMember { FileId = m.Id, Similarity = sim, File = m });
            }
            group.Confidence = sumSim / members.Count;
            pending.Add(group);
            created++;

            if (pending.Count >= FlushEvery)
            {
                _groups.InsertGroups(pending, sessionId);
                pending.Clear();
                onGroups?.Invoke(created);
            }
        }
        if (pending.Count > 0) _groups.InsertGroups(pending, sessionId);
        onGroups?.Invoke(created);
        return created;
    }

    private static void UnionByExactHash(List<FileRecord> files, Dictionary<long, ulong> hashOf, Dsu dsu)
    {
        var seen = new Dictionary<ulong, long>();
        foreach (var f in files)
        {
            var h = hashOf[f.Id];
            if (h == 0) continue;
            if (seen.TryGetValue(h, out var first)) dsu.Union(first, f.Id);
            else seen[h] = f.Id;
        }
    }

    private static void UnionByExactBlob(List<FileRecord> files, Func<FileRecord, byte[]?> selector, Dsu dsu)
    {
        var seen = new Dictionary<string, long>();
        foreach (var f in files)
        {
            var blob = selector(f);
            if (blob is null) continue;
            var key = Convert.ToHexString(blob);
            if (seen.TryGetValue(key, out var first)) dsu.Union(first, f.Id);
            else seen[key] = f.Id;
        }
    }

    private static List<long> DistinctByHash(List<long> ids, Dictionary<long, ulong> hashOf)
    {
        var seen = new HashSet<ulong>();
        var result = new List<long>();
        foreach (var id in ids)
            if (seen.Add(hashOf[id])) result.Add(id);
        return result;
    }

    /// <summary>
    /// Объединяет похожие элементы внутри корзины LSH. Крупные корзины НЕ отбрасываем (это вело к
    /// молчаливому пропуску дубликатов), а дробим по дополнительным полосам, пока подкорзины не
    /// станут ≤ pairCap, и только тогда сравниваем попарно. Полнота для близких пар сохраняется,
    /// объём работы остаётся ограниченным. Рекурсия конечна: level строго растёт до maxLevels.
    /// </summary>
    public static void UnionSimilarInBucket(
        IReadOnlyList<long> ids, int pairCap,
        Func<long, long, bool> similar,
        Func<long, int, long> refineKey, int maxLevels,
        Dsu dsu, CancellationToken ct = default, string kindForLog = "")
    {
        Refine(ids, 0);

        void Refine(IReadOnlyList<long> bucket, int level)
        {
            if (bucket.Count < 2) return;

            if (bucket.Count <= pairCap || level >= maxLevels)
            {
                if (bucket.Count > pairCap)
                    AppLog.Write($"LSH {kindForLog}: остаточная корзина {bucket.Count} элементов — сравнение впритык (почти идентичные хэши).");
                for (int i = 0; i < bucket.Count; i++)
                for (int j = i + 1; j < bucket.Count; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (similar(bucket[i], bucket[j])) dsu.Union(bucket[i], bucket[j]);
                }
                return;
            }

            // Дробим корзину по ключу следующей полосы.
            var sub = new Dictionary<long, List<long>>();
            foreach (var id in bucket)
            {
                var key = refineKey(id, level);
                if (!sub.TryGetValue(key, out var lst)) sub[key] = lst = new List<long>();
                lst.Add(id);
            }
            // Если эта полоса не разделила корзину — пробуем следующую (level гарантированно растёт).
            if (sub.Count <= 1) { Refine(bucket, level + 1); return; }
            foreach (var (_, lst) in sub) Refine(lst, level + 1);
        }
    }

    private static string? Resolve(FileRecord f, IReadOnlyDictionary<long, VolumeRecord> volumes)
    {
        if (volumes.TryGetValue(f.VolumeId, out var v) && v.IsConnected && v.MountPath is not null)
            return Path.Combine(v.MountPath, f.RelPath);
        return null;
    }
}
