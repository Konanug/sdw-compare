using System.Globalization;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Shared greedy multiset matching over face geometric signatures (the sorted canonical surface
/// descriptors produced by <see cref="StepGeometryEstimator.BuildFaceDescriptor"/>).
///
/// Two key strategies, deliberately different in strength:
/// <list type="bullet">
/// <item><b>Exact</b> (<see cref="StepGeometryEstimator.ParseDescriptor"/>) — keeps the axis/normal,
/// compares only radii with tolerance. Use when both parts come from the same CAD system in the same
/// frame (two revisions of one part).</item>
/// <item><b>Orientation-invariant</b> (<see cref="OrientationInvariantKey"/>) — drops the axis
/// entirely. Robust to a part stored rotated, but note it collapses <i>every</i> plane to one
/// parameterless key, so on a prismatic part it carries far less information than it appears to.
/// Use only when the two parts genuinely may be rotated relative to each other (an assembly rename).
/// </item>
/// </list>
///
/// And two denominators, which answer different questions:
/// <list type="bullet">
/// <item><see cref="AgreementFraction"/> — <c>matched / max(|A|,|B|)</c>: "are these the same set of
/// surfaces?" A extra faces on either side drive it down.</item>
/// <item><see cref="ContainmentFraction"/> — <c>matched / |subset|</c>: "is A's surface set contained
/// in B's?" Indifferent to how many faces B adds. This is what an engraving looks like: the base
/// part's faces all survive into the engraved part, which simply has many more.</item>
/// </list>
/// </summary>
internal static class FaceSignatureMatcher
{
    /// <summary>Splits a descriptor into (key compared exactly, radii compared with tolerance).</summary>
    internal delegate (string Key, double[] Radii) DescriptorKey(string descriptor);

    /// <summary>Exact key: surface type + axis/normal retained; only radii are tolerant.</summary>
    internal static readonly DescriptorKey ExactKey = StepGeometryEstimator.ParseDescriptor;

    /// <summary>Orientation-invariant key: surface type + radii only; axis/normal dropped.</summary>
    internal static readonly DescriptorKey OrientationInvariant = OrientationInvariantKey;

    /// <summary>
    /// How many descriptors in <paramref name="from"/> find a distinct partner in
    /// <paramref name="against"/>: same key, radii within <paramref name="relTol"/>. Greedy — each
    /// target descriptor is consumed at most once. Order-sensitive by construction (the greedy pick
    /// takes the first unused candidate), which is why callers pass the ordinal-sorted signature.
    /// </summary>
    internal static int GreedyMatchCount(
        IReadOnlyList<string> from, IReadOnlyList<string> against, DescriptorKey key, double relTol)
    {
        var byKey = new Dictionary<string, List<(double[] Radii, bool Used)>>(StringComparer.Ordinal);
        foreach (var d in against)
        {
            var (k, radii) = key(d);
            if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = [];
            list.Add((radii, false));
        }

        int matched = 0;
        foreach (var d in from)
        {
            var (k, radii) = key(d);
            if (!byKey.TryGetValue(k, out var candidates)) continue;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Used) continue;
                if (RadiiWithinTolerance(radii, candidates[i].Radii, relTol))
                {
                    candidates[i] = (candidates[i].Radii, true);
                    matched++;
                    break;
                }
            }
        }
        return matched;
    }

    /// <summary>
    /// Fraction of faces that agree, over <c>max(|A|,|B|)</c> — so extra faces on either side count
    /// against it. 0 when either signature is missing or empty.
    /// </summary>
    internal static double AgreementFraction(
        IReadOnlyList<string>? sigA, IReadOnlyList<string>? sigB, DescriptorKey key, double relTol)
    {
        if (sigA is null || sigB is null || sigA.Count == 0 || sigB.Count == 0) return 0.0;
        return (double)GreedyMatchCount(sigA, sigB, key, relTol) / Math.Max(sigA.Count, sigB.Count);
    }

    /// <summary>
    /// Fraction of <paramref name="subset"/>'s faces that are present in <paramref name="superset"/>,
    /// over <c>|subset|</c> — so <paramref name="superset"/> may carry arbitrarily many extra faces
    /// without lowering the score. 0 when either signature is missing or empty.
    /// </summary>
    internal static double ContainmentFraction(
        IReadOnlyList<string>? subset, IReadOnlyList<string>? superset, DescriptorKey key, double relTol)
    {
        if (subset is null || superset is null || subset.Count == 0 || superset.Count == 0) return 0.0;
        return (double)GreedyMatchCount(subset, superset, key, relTol) / subset.Count;
    }

    /// <summary>
    /// Descriptors in <paramref name="superset"/> that found no partner in <paramref name="subset"/>
    /// — i.e. the faces the bigger part <i>added</i>. Used to inspect what an engraving is made of.
    /// </summary>
    internal static List<string> UnmatchedIn(
        IReadOnlyList<string> superset, IReadOnlyList<string> subset, DescriptorKey key, double relTol)
    {
        var byKey = new Dictionary<string, List<(double[] Radii, bool Used)>>(StringComparer.Ordinal);
        foreach (var d in subset)
        {
            var (k, radii) = key(d);
            if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = [];
            list.Add((radii, false));
        }

        var added = new List<string>();
        foreach (var d in superset)
        {
            var (k, radii) = key(d);
            bool paired = false;
            if (byKey.TryGetValue(k, out var candidates))
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Used) continue;
                    if (RadiiWithinTolerance(radii, candidates[i].Radii, relTol))
                    {
                        candidates[i] = (candidates[i].Radii, true);
                        paired = true;
                        break;
                    }
                }
            }
            if (!paired) added.Add(d);
        }
        return added;
    }

    /// <summary>True when the descriptor is a curved surface (not a plane / OTHER / parse error).</summary>
    internal static bool IsCurved(string descriptor)
        => descriptor.StartsWith("CYLINDER|", StringComparison.Ordinal)
        || descriptor.StartsWith("CONE|", StringComparison.Ordinal)
        || descriptor.StartsWith("SPHERE|", StringComparison.Ordinal)
        || descriptor.StartsWith("TORUS|", StringComparison.Ordinal);

    internal static bool RadiiWithinTolerance(double[] a, double[] b, double relTol)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (double.IsNaN(a[i]) || double.IsNaN(b[i])) return false;
            double max = Math.Max(Math.Abs(a[i]), Math.Abs(b[i]));
            if (max == 0) continue; // both zero → equal
            if (Math.Abs(a[i] - b[i]) / max > relTol) return false;
        }
        return true;
    }

    // Strips a face descriptor down to an orientation-invariant key + radius/size values: the
    // surface type (and half-angle for cones) plus its radii, dropping the axis/normal direction.
    // Grammar (see StepGeometryEstimator.BuildFaceDescriptor):
    //   CYLINDER|r|ax|ay|az   PLANE|nx|ny|nz   CONE|ha|r|ax|ay|az   SPHERE|r   TORUS|R|r
    private static (string Key, double[] Radii) OrientationInvariantKey(string descriptor)
    {
        var parts = descriptor.Split('|');
        static double R(string s) => double.TryParse(
            s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

        return parts[0] switch
        {
            "CYLINDER" when parts.Length == 5 => ("CYLINDER", [R(parts[1])]),
            "CONE" when parts.Length == 6 => ($"CONE|{parts[1]}", [R(parts[2])]),
            "SPHERE" when parts.Length == 2 => ("SPHERE", [R(parts[1])]),
            "TORUS" when parts.Length == 3 => ("TORUS", [R(parts[1]), R(parts[2])]),
            "PLANE" => ("PLANE", []),
            _ => (parts[0], []), // OTHER, PARSE_ERROR
        };
    }
}
