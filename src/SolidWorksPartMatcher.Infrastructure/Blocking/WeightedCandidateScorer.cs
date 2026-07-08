using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Domain.Utilities;

namespace SolidWorksPartMatcher.Infrastructure.Blocking;

public sealed class WeightedCandidateScorer : ICandidateScorer
{
    public double Score(PartFingerprint a, PartFingerprint b, ScoringWeights w)
    {
        var bbSim = BoundingBoxSimilarity(EffectiveBB(a), EffectiveBB(b));
        var volSim = ScalarSimilarity(EffectiveVolume(a), EffectiveVolume(b));
        var saSim = ScalarSimilarity(EffectiveSA(a), EffectiveSA(b));
        var topoSim = TopologySimilarity(a, b);
        var featSim = FeatureHistogramSimilarity(a.FeatureTypeHistogram, b.FeatureTypeHistogram);
        var matSim = MaterialSimilarity(a.Material, b.Material);
        var propSim = CustomPropertiesSimilarity(a.CustomProperties, b.CustomProperties);
        var nameSim = FilenameSimilarity(a.ConfigName, b.ConfigName);

        return w.BoundingBox * bbSim
             + w.Volume * volSim
             + w.SurfaceArea * saSim
             + w.Topology * topoSim
             + w.FeatureHistogram * featSim
             + w.MaterialProperties * matSim
             + w.CustomProperties * propSim
             + w.FilenameTokens * nameSim;
    }

    // Features whose linear dimensions differ by this amount or less are treated as
    // dimensionally equivalent. Applied per bounding-box axis before relative scoring.
    private static double[] EffectiveBB(PartFingerprint fp)
        => fp.SuppressedBoundingBoxM ?? fp.SortedBoundingBoxM;
    private static double EffectiveVolume(PartFingerprint fp)
        => fp.SuppressedVolumeM3 ?? fp.VolumeM3;
    private static double EffectiveSA(PartFingerprint fp)
        => fp.SuppressedSurfaceAreaM2 ?? fp.SurfaceAreaM2;

    internal const double FeatureToleranceM = 0.0005; // 0.5 mm in metres

    private static double BoundingBoxSimilarity(double[] a, double[] b)
    {
        if (a.Length < 3 || b.Length < 3) return 0;
        var sim = 0.0;
        for (var i = 0; i < 3; i++)
            sim += DimensionSimilarity(a[i], b[i]);
        return sim / 3.0;
    }

    // Returns 1.0 when the absolute difference is within the 0.5 mm feature tolerance;
    // otherwise falls back to relative (ratio-based) similarity.
    // Tolerance is applied to full-precision values; display rounding is not used here.
    private static double DimensionSimilarity(double a, double b)
    {
        if (Math.Abs(a - b) <= FeatureToleranceM) return 1.0;
        return ScalarSimilarity(a, b);
    }

    private static double ScalarSimilarity(double a, double b)
    {
        if (a == 0 && b == 0) return 1.0;
        var max = Math.Max(Math.Abs(a), Math.Abs(b));
        if (max == 0) return 1.0;
        return Math.Max(0.0, 1.0 - Math.Abs(a - b) / max);
    }

    private static double TopologySimilarity(PartFingerprint a, PartFingerprint b)
    {
        int facesA = a.SuppressedFaceCount ?? a.FaceCount;
        int facesB = b.SuppressedFaceCount ?? b.FaceCount;
        int edgesA = a.SuppressedEdgeCount ?? a.EdgeCount;
        int edgesB = b.SuppressedEdgeCount ?? b.EdgeCount;
        int vertsA = a.SuppressedVertexCount ?? a.VertexCount;
        int vertsB = b.SuppressedVertexCount ?? b.VertexCount;
        return (ScalarSimilarity(facesA, facesB)
              + ScalarSimilarity(edgesA, edgesB)
              + ScalarSimilarity(vertsA, vertsB)) / 3.0;
    }

    private static double FeatureHistogramSimilarity(
        IReadOnlyDictionary<string, int> a,
        IReadOnlyDictionary<string, int> b)
    {
        var allKeys = new HashSet<string>(a.Keys);
        allKeys.UnionWith(b.Keys);
        if (allKeys.Count == 0) return 1.0;

        var dot = 0.0;
        var normA = 0.0;
        var normB = 0.0;
        foreach (var k in allKeys)
        {
            var va = a.TryGetValue(k, out var av) ? av : 0;
            var vb = b.TryGetValue(k, out var bv) ? bv : 0;
            dot += va * vb;
            normA += va * va;
            normB += vb * vb;
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 1.0 : dot / denom;
    }

    private static double MaterialSimilarity(string? a, string? b)
    {
        if (a == null && b == null) return 1.0;
        if (a == null || b == null) return 0.0;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    // Compares custom property bags with fraction/decimal normalisation.
    // Values that parse as numbers are compared at 2 decimal places so that
    // "1/7" (≈ 0.14) and "0.14" are treated as equal, and "0.55" and "0.553231"
    // are treated as equal, while full-precision values are preserved internally.
    // Score = (matching keys) / (union of all keys), Jaccard-style.
    // Neutral (0.5) when neither part has custom properties, to avoid false penalty.
    private static double CustomPropertiesSimilarity(
        IReadOnlyDictionary<string, string> a,
        IReadOnlyDictionary<string, string> b)
    {
        // Exclude Material — already covered by MaterialSimilarity.
        static bool NotMaterial(string k) =>
            !k.Equals("Material", StringComparison.OrdinalIgnoreCase);

        var keysA = new HashSet<string>(
            a.Keys.Where(NotMaterial), StringComparer.OrdinalIgnoreCase);
        var keysB = new HashSet<string>(
            b.Keys.Where(NotMaterial), StringComparer.OrdinalIgnoreCase);

        var union = new HashSet<string>(keysA, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(keysB);

        if (union.Count == 0) return 1.0; // no custom properties → no penalty

        var intersection = keysA.Intersect(keysB, StringComparer.OrdinalIgnoreCase).ToList();
        if (intersection.Count == 0) return 0.5; // disjoint property sets → neutral

        int matches = 0;
        foreach (var key in intersection)
        {
            var va = a.TryGetValue(key, out var av) ? av ?? "" : "";
            var vb = b.TryGetValue(key, out var bv) ? bv ?? "" : "";
            if (AreEquivalentPropertyValues(va, vb)) matches++;
        }

        // Penalise by union so extra keys on one side still lower the score.
        return (double)matches / union.Count;
    }

    // Two property values are equivalent when:
    //   • They are the same string (case-insensitive), OR
    //   • At least one side carries an explicit unit suffix (in/mm): both are converted
    //     to mm and compared with 0.5 mm tolerance or 2 dp equivalence.
    //     A unitless side inherits the unit of the explicit side — "3/8in" vs "0.375"
    //     treats "0.375" as 0.375 inches → 9.525 mm vs 9.525 mm → equal.
    //   • Both parse as unit-less numbers that round to the same two decimal places.
    // Full-precision doubles are used for comparison; rounding only determines equality.
    internal static bool AreEquivalentPropertyValues(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        var okA = MeasurementParser.TryParseToMm(a, out var aMm, out var unitA);
        var okB = MeasurementParser.TryParseToMm(b, out var bMm, out var unitB);

        // Unit-aware path: enter when at least one side has an explicit unit.
        // The unitless side inherits the explicit side's unit for conversion.
        if (okA && okB &&
            (unitA != MeasurementParser.LengthUnit.Unknown ||
             unitB != MeasurementParser.LengthUnit.Unknown))
        {
            var refUnit = unitA != MeasurementParser.LengthUnit.Unknown ? unitA : unitB;
            if (unitA == MeasurementParser.LengthUnit.Unknown &&
                refUnit == MeasurementParser.LengthUnit.Inch)
                aMm *= 25.4;
            if (unitB == MeasurementParser.LengthUnit.Unknown &&
                refUnit == MeasurementParser.LengthUnit.Inch)
                bMm *= 25.4;

            return Math.Abs(aMm - bMm) <= 0.5 ||
                   MeasurementParser.FormatDisplay(aMm) == MeasurementParser.FormatDisplay(bMm);
        }

        // Unit-less: compare as pure numbers at 2 decimal places.
        if (MeasurementParser.TryParseNumber(a, out var na) &&
            MeasurementParser.TryParseNumber(b, out var nb))
        {
            return MeasurementParser.FormatDisplay(na) == MeasurementParser.FormatDisplay(nb);
        }

        return false;
    }

    private static double FilenameSimilarity(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        if (tokensA.Count == 0 && tokensB.Count == 0) return 1.0;
        var intersection = tokensA.Intersect(tokensB).Count();
        var union = tokensA.Union(tokensB).Count();
        return union == 0 ? 1.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string s)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in s.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length > 1)
                tokens.Add(tok);
        return tokens;
    }
}
