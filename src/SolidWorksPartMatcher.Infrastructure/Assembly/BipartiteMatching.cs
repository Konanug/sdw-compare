namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// Deterministic maximum-cardinality bipartite matching (Kuhn's augmenting-path algorithm).
/// Hand-rolled — small, well-understood, and disproportionate to pull in a dependency for; the
/// inputs here are occurrence counts per product (tens, not thousands). Used to answer the
/// position-comparison question "can every instance on the smaller side be paired to a distinct
/// within-tolerance instance on the other side?" (see <see cref="OccurrencePositionComparer"/>).
///
/// Proper matching, not greedy nearest-neighbour: greedy can make a locally-cheap pairing that
/// strands another vertex with no partner even when a full valid pairing exists, producing a
/// false "changed" verdict. Augmenting paths always find a maximum matching, so a valid pairing
/// is never missed.
/// </summary>
internal static class BipartiteMatching
{
    /// <summary>
    /// Size of a maximum matching in the bipartite graph where left vertex i and right vertex j
    /// are adjacent iff <paramref name="adjacency"/>[i, j] is true. Deterministic (fixed vertex
    /// iteration order).
    /// </summary>
    public static int MaxMatchingSize(bool[,] adjacency)
    {
        int leftCount = adjacency.GetLength(0);
        int rightCount = adjacency.GetLength(1);

        var matchRight = new int[rightCount];
        Array.Fill(matchRight, -1); // right vertex j → its matched left vertex, or -1 if free

        int matched = 0;
        for (int i = 0; i < leftCount; i++)
        {
            var seen = new bool[rightCount];
            if (TryAugment(i, adjacency, matchRight, seen)) matched++;
        }
        return matched;
    }

    private static bool TryAugment(int left, bool[,] adjacency, int[] matchRight, bool[] seen)
    {
        int rightCount = matchRight.Length;
        for (int j = 0; j < rightCount; j++)
        {
            if (!adjacency[left, j] || seen[j]) continue;
            seen[j] = true;
            if (matchRight[j] == -1 || TryAugment(matchRight[j], adjacency, matchRight, seen))
            {
                matchRight[j] = left;
                return true;
            }
        }
        return false;
    }
}
