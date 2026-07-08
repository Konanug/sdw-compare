namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// Compares two assembly versions' sets of occurrence positions for one product and answers a
/// single coarse question: did any instance of this part move? Deliberately NOT per-instance — it
/// never claims which, or how many, instances moved (that pinpointing was inconsistent and was
/// removed). The two sides' positions are compared as unordered sets, never matched by index or
/// any persistent id (STEP has no reliable cross-file occurrence identity).
///
/// "Moved" = some instance has no distinct counterpart within tolerance on the other side. This is
/// decided by whether a within-tolerance bipartite matching can saturate the smaller side, which
/// is robust where a plain nearest-neighbour check is not: several identical instances sitting
/// near each other, or two instances collapsing onto one position, are handled correctly because a
/// counterpart can only be claimed once.
/// </summary>
internal static class OccurrencePositionComparer
{
    /// <summary>
    /// null when either side has no resolved positions (never treated as "unchanged"); true when
    /// at least one instance lacks a within-tolerance counterpart; false when every instance on
    /// the smaller side matches a distinct within-tolerance counterpart. (Cardinality differences
    /// are a quantity change, reported separately — pure additions/removals of otherwise-stationary
    /// instances report position-unchanged here.)
    /// </summary>
    public static bool? PositionChanged(
        IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, double thresholdMeters)
    {
        if (a.Count == 0 || b.Count == 0) return null;

        var adjacency = new bool[a.Count, b.Count];
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                adjacency[i, j] = PlacementMath.PositionDistance(a[i], b[j]) <= thresholdMeters;

        int matched = BipartiteMatching.MaxMatchingSize(adjacency);
        return matched < Math.Min(a.Count, b.Count);
    }
}
