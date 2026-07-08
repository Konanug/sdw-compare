namespace SolidWorksPartMatcher.Infrastructure.Step.Assembly;

/// <summary>
/// Computes the transitive closure of P21 entity references reachable from a starting entity —
/// e.g. everything a component's SHAPE_REPRESENTATION ultimately depends on. Schema-agnostic by
/// construction: it just follows every "#nnn" token found in each visited entity's raw text,
/// so it works unchanged across AP203/AP214/AP242 variants without hardcoding intermediate
/// wrapper entity types.
/// </summary>
public static class StepEntityClosureWalker
{
    public static HashSet<int> ComputeClosure(StepP21Reader reader, int startId, int maxEntities = 200_000)
    {
        var visited = new HashSet<int> { startId };
        var queue = new Queue<int>();
        queue.Enqueue(startId);

        while (queue.Count > 0)
        {
            if (visited.Count >= maxEntities) break;

            var id = queue.Dequeue();
            foreach (var refId in reader.GetReferencedIds(id))
            {
                if (!visited.Add(refId)) continue;
                queue.Enqueue(refId);
                if (visited.Count >= maxEntities) break;
            }
        }

        return visited;
    }
}
