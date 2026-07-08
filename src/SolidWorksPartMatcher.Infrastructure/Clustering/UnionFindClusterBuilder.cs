using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Clustering;

public sealed class UnionFindClusterBuilder : IClusterBuilder
{
    public IReadOnlyList<PartCluster> BuildClusters(
        Guid scanRunId,
        IReadOnlyList<PartFingerprint> fingerprints,
        IReadOnlyList<CandidatePair> pairs,
        IReadOnlyList<ScannedFile> files,
        ICanonicalNameService nameService)
    {
        var fileByScannedId = files.ToDictionary(f => f.Id);
        var parent = fingerprints.ToDictionary(f => f.Id, f => f.Id);
        var rank = fingerprints.ToDictionary(f => f.Id, _ => 0);

        Guid Find(Guid id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]]; // path compression
                id = parent[id];
            }
            return id;
        }

        void Union(Guid a, Guid b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
            parent[rb] = ra;
            if (rank[ra] == rank[rb]) rank[ra]++;
        }

        // Auto-join confirmed matches and near-matches:
        //   BinaryDuplicate              — identical file bytes
        //   ExactGeometryMatch           — confirmed rigid-body coincidence (Stage 4) or
        //                                  within-tolerance match (Stage 4.5)
        //   GeometryMatchMetadataVariant — same geometry, different material; noted in Excel
        //   RevisionFamily               — closely related revisions (volumetric Jaccard ≥ 0.90)
        //   MirrorOrHandedVariant        — grouped for review; remains separate from exact clusters
        //   PossibleMatch                — grouped for review (user must confirm before merge)
        foreach (var pair in pairs)
        {
            if (pair.Classification is PartClassification.BinaryDuplicate
                                    or PartClassification.ExactGeometryMatch
                                    or PartClassification.GeometryMatchMetadataVariant
                                    or PartClassification.EngravingVariant
                                    or PartClassification.RevisionFamily
                                    or PartClassification.MirrorOrHandedVariant
                                    or PartClassification.PossibleMatch)
            {
                Union(pair.FingerprintAId, pair.FingerprintBId);
            }
        }

        // Group by root
        var groups = new Dictionary<Guid, List<PartFingerprint>>();
        foreach (var fp in fingerprints)
        {
            var root = Find(fp.Id);
            if (!groups.TryGetValue(root, out var list))
                groups[root] = list = [];
            list.Add(fp);
        }

        var fpById = fingerprints.ToDictionary(f => f.Id);
        var clusters = new List<PartCluster>();

        // Determine classification per cluster.
        // Keep only the highest-confidence pair per canonical key so re-runs that
        // accumulate duplicate rows in candidate_pairs don't crash ToDictionary.
        var pairsByIds = pairs
            .GroupBy(p => p.FingerprintAId.CompareTo(p.FingerprintBId) <= 0
                ? (p.FingerprintAId, p.FingerprintBId)
                : (p.FingerprintBId, p.FingerprintAId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CoarseScore).First());

        foreach (var (root, members) in groups)
        {
            var classification = DetermineClassification(members, pairsByIds);
            var rep = members[0];

            var memberFiles = members
                .Select(fp => fileByScannedId.TryGetValue(fp.ScannedFileId, out var sf) ? sf : null)
                .Where(sf => sf != null).Select(sf => sf!).ToList();
            var name = nameService.Suggest(members, memberFiles);
            clusters.Add(new PartCluster(
                Id: root,
                ScanRunId: scanRunId,
                CanonicalName: name,
                Classification: classification,
                RepresentativeFingerprintId: rep.Id,
                ReviewStatus: members.Count > 1 ? ReviewStatus.NeedsReview : ReviewStatus.Pending,
                ReviewerNote: null,
                ReviewedUtc: null,
                ReviewerName: null));
        }

        return clusters;
    }

    private static PartClassification DetermineClassification(
        List<PartFingerprint> members,
        Dictionary<(Guid, Guid), CandidatePair> pairs)
    {
        if (members.Count == 1) return PartClassification.Distinct;

        // Check SHA-256 duplicates
        var sha = members.Select(m => m.FileSha256).Distinct().ToList();
        if (sha.Count == 1) return PartClassification.BinaryDuplicate;

        bool hasMetadataVariant = false;
        bool hasEngravingPair = false;
        bool hasRevisionPair = false;
        bool hasMirrorPair = false;

        for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var key = members[i].Id.CompareTo(members[j].Id) <= 0
                    ? (members[i].Id, members[j].Id)
                    : (members[j].Id, members[i].Id);
                if (!pairs.TryGetValue(key, out var pair)) continue;

                if (pair.Classification == PartClassification.ExactGeometryMatch)
                    return PartClassification.ExactGeometryMatch;
                if (pair.Classification == PartClassification.GeometryMatchMetadataVariant)
                    hasMetadataVariant = true;
                if (pair.Classification == PartClassification.EngravingVariant)
                    hasEngravingPair = true;
                if (pair.Classification == PartClassification.RevisionFamily)
                    hasRevisionPair = true;
                if (pair.Classification == PartClassification.MirrorOrHandedVariant)
                    hasMirrorPair = true;
            }

        if (hasMetadataVariant) return PartClassification.GeometryMatchMetadataVariant;
        if (hasEngravingPair) return PartClassification.EngravingVariant;
        if (hasRevisionPair) return PartClassification.RevisionFamily;
        if (hasMirrorPair) return PartClassification.MirrorOrHandedVariant;
        return PartClassification.PossibleMatch;
    }
}
