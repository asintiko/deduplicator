using Deduplicator.Core.Models;

namespace Deduplicator.Core.Matching;

/// <summary>Выбор эталона группы по настраиваемому правилу (FR-12, FR-36).</summary>
public static class RepresentativeSelector
{
    public static FileRecord Pick(IReadOnlyList<FileRecord> files, RepresentativeRule rule)
    {
        if (files.Count == 0) throw new ArgumentException("Пустая группа", nameof(files));
        return rule switch
        {
            RepresentativeRule.Oldest => files.OrderBy(f => f.Mtime).ThenBy(f => f.RelPath).First(),
            RepresentativeRule.Newest => files.OrderByDescending(f => f.Mtime).ThenBy(f => f.RelPath).First(),
            RepresentativeRule.ShortestPath => files.OrderBy(f => f.RelPath.Length).ThenBy(f => f.Mtime).First(),
            RepresentativeRule.LongestName => files.OrderByDescending(f => f.Name.Length).ThenBy(f => f.Mtime).First(),
            _ => files[0]
        };
    }
}
