using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Services;

/// <summary>
/// Derives, per cluster, the reasons its parts were declared a match: the distinct
/// <see cref="CandidatePair.ClassificationReason"/> values of the pair comparisons whose two
/// endpoints are both members of that cluster, strongest evidence (highest coarse score) first.
///
/// Pure, allocation-light, and independent of WPF so the rule is testable on its own. Indexes the
/// pairs by cluster in a single pass rather than re-scanning the whole pair list per cluster, which
/// the view-model previously did (O(clusters x pairs) — enough to stall the UI on a large scan).
/// </summary>
public static class MatchReasonAggregator
{
    /// <summary>
    /// Returns cluster id → distinct reasons, ordered by descending coarse score. Clusters with no
    /// reasoned pair are absent from the map; callers should treat a miss as "no reasons".
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<string>> ReasonsByCluster(
        IEnumerable<CandidatePair> pairs,
        IEnumerable<ClusterMember> members)
    {
        // A fingerprint belongs to exactly one cluster under union-find, but a lookup keeps this
        // correct (rather than throwing) if that ever stops holding.
        var clustersByFingerprint = members.ToLookup(m => m.FingerprintId, m => m.ClusterId);

        var pairsByCluster = new Dictionary<Guid, List<CandidatePair>>();
        foreach (var pair in pairs)
        {
            // Every candidate pair is persisted, including Distinct and ComparisonFailed ones. Two
            // members of the same cluster can be joined transitively (A~B, B~C) while the A–C pair
            // itself came out Distinct, so a reason is only evidence of a match if its pair is one.
            if (!pair.Classification.IsMatch()) continue;
            if (string.IsNullOrWhiteSpace(pair.ClassificationReason)) continue;

            foreach (var clusterId in clustersByFingerprint[pair.FingerprintAId])
            {
                if (!clustersByFingerprint[pair.FingerprintBId].Contains(clusterId)) continue;

                if (!pairsByCluster.TryGetValue(clusterId, out var list))
                    pairsByCluster[clusterId] = list = [];
                list.Add(pair);
            }
        }

        // OrderByDescending is stable, so pairs of equal score keep their source order — the output
        // is deterministic for a deterministic pair list.
        return pairsByCluster.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value
                .OrderByDescending(p => p.CoarseScore)
                .Select(p => p.ClassificationReason!)
                .Distinct()
                .ToList());
    }
}
