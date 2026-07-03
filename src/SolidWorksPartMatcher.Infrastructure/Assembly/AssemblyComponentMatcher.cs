using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;

namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// Matches components between two parsed assembly versions and classifies each pair.
/// Pure logic — no P21/file dependency — so it's easy to unit test against hand-built
/// <see cref="AssemblyComponent"/> fixtures.
/// </summary>
public sealed class AssemblyComponentMatcher(ICandidateScorer scorer)
{
    // Fallback-matcher weights: MaterialProperties/CustomProperties/FilenameTokens are always
    // neutral/empty for pure-STEP components, so they're zeroed here and the remaining weights
    // rescaled to sum to 1.0 (each original ScoringWeights.Default value divided by 0.90, the
    // sum of the kept weights) rather than reusing ScoringWeights.Default, which would give
    // nonzero weight to signals that can never carry information for this data.
    internal static readonly ScoringWeights FallbackScoringWeights = new(
        BoundingBox: 0.30 / 0.90,
        Volume: 0.25 / 0.90,
        SurfaceArea: 0.10 / 0.90,
        Topology: 0.15 / 0.90,
        FeatureHistogram: 0.10 / 0.90,
        MaterialProperties: 0.0,
        CustomProperties: 0.0,
        FilenameTokens: 0.0);

    public AssemblyDiffSummary Diff(
        AssemblyStructure a,
        AssemblyStructure b,
        AssemblyDiffTolerances tolerances,
        string fileAPath,
        string fileBPath)
    {
        var warnings = new List<string>(a.Warnings);
        warnings.AddRange(b.Warnings);

        var byKeyA = BuildKeyIndex(a.Components, warnings, "A");
        var byKeyB = BuildKeyIndex(b.Components, warnings, "B");

        var diffs = new List<AssemblyComponentDiff>();
        var unmatchedA = new List<AssemblyComponent>();
        var unmatchedB = new List<AssemblyComponent>();

        foreach (var (key, compA) in byKeyA)
        {
            if (byKeyB.TryGetValue(key, out var compB))
                diffs.Add(ClassifyPair(key, compA, compB, tolerances, geometricSimilarity: null));
            else
                unmatchedA.Add(compA);
        }
        foreach (var (key, compB) in byKeyB)
            if (!byKeyA.ContainsKey(key))
                unmatchedB.Add(compB);

        // Fallback geometric pass for renamed/unmatched components.
        MatchByGeometry(unmatchedA, unmatchedB, tolerances, diffs);

        foreach (var compA in unmatchedA)
            diffs.Add(new AssemblyComponentDiff(
                MatchKey: compA.MatchKey, ComponentA: compA, ComponentB: null,
                DiffType: AssemblyDiffType.Removed, QuantityChanged: false,
                InstanceCountA: compA.InstanceCount, InstanceCountB: 0,
                BoundingBoxDeltaPercent: null, BoundingBoxVolumeDeltaPercent: null,
                VolumeDeltaPercent: null, SurfaceAreaDeltaPercent: null,
                FaceCountDelta: null, GeometricSimilarityScore: null,
                Reasons: ["Part removed."]));
        foreach (var compB in unmatchedB)
            diffs.Add(new AssemblyComponentDiff(
                MatchKey: compB.MatchKey, ComponentA: null, ComponentB: compB,
                DiffType: AssemblyDiffType.Added, QuantityChanged: false,
                InstanceCountA: 0, InstanceCountB: compB.InstanceCount,
                BoundingBoxDeltaPercent: null, BoundingBoxVolumeDeltaPercent: null,
                VolumeDeltaPercent: null, SurfaceAreaDeltaPercent: null,
                FaceCountDelta: null, GeometricSimilarityScore: null,
                Reasons: ["Part added."]));

        var ordered = diffs
            .OrderBy(d => SortPriority(d))
            .ThenBy(d => d.MatchKey, StringComparer.Ordinal)
            .ToList();

        return new AssemblyDiffSummary(fileAPath, fileBPath, DateTime.UtcNow, ordered, warnings);
    }

    // Quantity/placement-only changes (geometry Unchanged, but instance count or assembly
    // position/orientation differs) get their own sort slots between Modified and true
    // Unchanged — they're distinct, worth-a-look categories, not noise mixed into "nothing
    // changed."
    private static int SortPriority(AssemblyComponentDiff d) => d.DiffType switch
    {
        AssemblyDiffType.Removed => 0,
        AssemblyDiffType.Added => 1,
        AssemblyDiffType.SuspiciousMatch => 2,
        AssemblyDiffType.Modified => 3,
        AssemblyDiffType.Unchanged => d.QuantityChanged ? 4
            : (d.OrientationChanged == true || d.PositionChanged == true) ? 5
            : 6,
        _ => 7
    };

    private static Dictionary<string, AssemblyComponent> BuildKeyIndex(
        IReadOnlyList<AssemblyComponent> components, List<string> warnings, string side)
    {
        var byKey = new Dictionary<string, AssemblyComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in components.OrderBy(c => c.ProductId, StringComparer.Ordinal))
        {
            if (!byKey.TryAdd(c.MatchKey, c))
                warnings.Add($"Duplicate component name '{c.MatchKey}' in assembly {side} " +
                             $"(product id '{c.ProductId}') — kept the lowest-id occurrence, others ignored for matching.");
        }
        return byKey;
    }

    private AssemblyComponentDiff ClassifyPair(
        string key, AssemblyComponent compA, AssemblyComponent compB,
        AssemblyDiffTolerances tol, double? geometricSimilarity)
    {
        var bbDelta = BoundingBoxDeltaPercent(compA.SortedBoundingBoxM, compB.SortedBoundingBoxM);
        double bbVolA = compA.SortedBoundingBoxM[0] * compA.SortedBoundingBoxM[1] * compA.SortedBoundingBoxM[2];
        double bbVolB = compB.SortedBoundingBoxM[0] * compB.SortedBoundingBoxM[1] * compB.SortedBoundingBoxM[2];
        double bbVolDeltaPct = RelativeDeltaPercent(bbVolA, bbVolB);
        double volDeltaPct = RelativeDeltaPercent(compA.VolumeM3, compB.VolumeM3);
        double saDeltaPct  = RelativeDeltaPercent(compA.SurfaceAreaM2, compB.SurfaceAreaM2);
        int faceDelta      = compB.FaceCount - compA.FaceCount;

        bool quantityChanged = compA.InstanceCount.HasValue && compB.InstanceCount.HasValue
            && compA.InstanceCount.Value != compB.InstanceCount.Value;

        bool withinTolerance = IsWithinTolerance(compA.SortedBoundingBoxM, bbDelta, volDeltaPct, tol);

        AssemblyDiffType diffType;
        bool suspiciousNameCollision = false, suspiciousSizeMismatch = false;

        if (withinTolerance)
        {
            diffType = AssemblyDiffType.Unchanged;
        }
        else
        {
            double similarity = geometricSimilarity ?? GeometricSimilarity(compA, compB);
            if (similarity < tol.SuspiciousMatchThreshold)
            {
                diffType = AssemblyDiffType.SuspiciousMatch;
                suspiciousNameCollision = true;
            }
            else if (geometricSimilarity.HasValue && Math.Abs(volDeltaPct) > tol.FallbackSuspiciousVolumeDeltaPercent)
            {
                // A confident-looking blended similarity score (bounding box + volume + surface
                // area + topology + feature histogram together) can still coexist with a large
                // volume delta on its own — reporting that pairing as "possible rename" would be
                // self-contradictory (a renamed-but-unmodified part should look nearly identical).
                // Flag it as suspicious instead of asserting an identity we can't actually back up.
                diffType = AssemblyDiffType.SuspiciousMatch;
                suspiciousSizeMismatch = true;
            }
            else
            {
                diffType = AssemblyDiffType.Modified;
            }
        }

        // Orientation/position are about the assembly PLACEMENT, entirely separate from shape
        // identity — reported whenever both sides have exactly one, unambiguous instance to
        // compare (see AssemblyComponent's Placement doc), regardless of how the pair was
        // classified. Even a SuspiciousMatch or Modified pairing is worth telling the user
        // "and it also moved/rotated" — orientation is a placement fact, not a claim about shape
        // identity, so it's never gated on how confident the shape classification is.
        bool? orientationChanged = null, positionChanged = null;
        if (compA.Placement is { } placementA && compB.Placement is { } placementB)
        {
            orientationChanged = PlacementMath.OrientationAngleDegrees(placementA, placementB)
                > tol.OrientationChangeDegreesThreshold;
            positionChanged = PlacementMath.PositionDistance(placementA, placementB)
                > tol.PositionChangeMetersThreshold;
        }

        var reasons = BuildReasons(
            quantityChanged, compA.InstanceCount, compB.InstanceCount,
            suspiciousNameCollision, suspiciousSizeMismatch,
            geometricSimilarity.HasValue && !suspiciousNameCollision && !suspiciousSizeMismatch,
            diffType, volDeltaPct, faceDelta, orientationChanged, positionChanged);

        return new AssemblyComponentDiff(
            key, compA, compB, diffType, quantityChanged,
            compA.InstanceCount, compB.InstanceCount,
            bbDelta, bbVolDeltaPct, volDeltaPct, saDeltaPct, faceDelta,
            geometricSimilarity, reasons, orientationChanged, positionChanged);
    }

    // Builds short, plain-English bullet points — no raw internal measurements, no jargon.
    // Numeric detail (exact bounding-box axes, similarity scores, etc.) still lives on the
    // AssemblyComponentDiff record itself for anyone who wants it (e.g. the Excel export).
    private static List<string> BuildReasons(
        bool quantityChanged, int? instanceCountA, int? instanceCountB,
        bool suspiciousNameCollision, bool suspiciousSizeMismatch, bool matchedByGeometry,
        AssemblyDiffType diffType, double volDeltaPct, int faceDelta,
        bool? orientationChanged, bool? positionChanged)
    {
        var reasons = new List<string>();

        if (quantityChanged)
            reasons.Add($"Quantity changed from {instanceCountA} to {instanceCountB}.");

        if (suspiciousNameCollision)
        {
            reasons.Add("Same name, but geometry is very different.");
            reasons.Add("Likely two different parts.");
        }
        else if (suspiciousSizeMismatch)
        {
            reasons.Add("Matched by shape, but sizes are very different.");
            reasons.Add("Likely two different parts.");
        }
        else
        {
            if (matchedByGeometry)
                reasons.Add("Same shape, different name (likely renamed).");

            if (diffType == AssemblyDiffType.Modified)
            {
                if (Math.Abs(volDeltaPct) > 0.5)
                    reasons.Add(volDeltaPct > 0
                        ? $"Volume increased by {Math.Abs(volDeltaPct):F0}%."
                        : $"Volume decreased by {Math.Abs(volDeltaPct):F0}%.");
                else if (faceDelta != 0)
                    reasons.Add("Geometry changed.");
                else
                    reasons.Add("Different overall size.");
            }
        }

        // Orientation/position are reported uniformly regardless of shape classification — a
        // suspicious or modified pairing can still have moved/rotated in the assembly, and that's
        // useful, independent signal the user should see either way (never gated behind an early
        // return, and worded to not presuppose "same part" for non-Unchanged classifications).
        if (orientationChanged == true)
            reasons.Add("Orientation changed in the assembly.");
        if (positionChanged == true)
            reasons.Add("Position changed in the assembly.");

        if (reasons.Count == 0)
            reasons.Add("No differences found.");

        return reasons;
    }

    private static bool IsWithinTolerance(
        double[] bbA, double[] bbDeltaPercent, double volDeltaPct, AssemblyDiffTolerances tol)
    {
        for (int i = 0; i < bbDeltaPercent.Length; i++)
        {
            double absDeltaM = Math.Abs(bbDeltaPercent[i] / 100.0 * bbA[i]);
            if (absDeltaM <= tol.BoundingBoxAbsoluteFloorM) continue;
            if (Math.Abs(bbDeltaPercent[i]) > tol.BoundingBoxDeltaPercentThreshold) return false;
        }
        return Math.Abs(volDeltaPct) <= tol.VolumeDeltaPercentThreshold;
    }

    private static double[] BoundingBoxDeltaPercent(double[] a, double[] b)
    {
        var delta = new double[Math.Min(a.Length, b.Length)];
        for (int i = 0; i < delta.Length; i++)
            delta[i] = RelativeDeltaPercent(a[i], b[i]);
        return delta;
    }

    private static double RelativeDeltaPercent(double a, double b)
    {
        double baseline = Math.Abs(a);
        if (baseline < 1e-12) return b == 0 ? 0.0 : 100.0;
        return (b - a) / baseline * 100.0;
    }

    // ── Fallback geometric matching for unmatched (renamed) components ─────────────────────

    private void MatchByGeometry(
        List<AssemblyComponent> unmatchedA,
        List<AssemblyComponent> unmatchedB,
        AssemblyDiffTolerances tol,
        List<AssemblyComponentDiff> diffs)
    {
        while (unmatchedA.Count > 0 && unmatchedB.Count > 0)
        {
            double bestScore = -1;
            int bestI = -1, bestJ = -1;
            for (int i = 0; i < unmatchedA.Count; i++)
            {
                for (int j = 0; j < unmatchedB.Count; j++)
                {
                    double score = GeometricSimilarity(unmatchedA[i], unmatchedB[j]);
                    if (score > bestScore) { bestScore = score; bestI = i; bestJ = j; }
                }
            }

            if (bestScore < tol.GeometricSimilarityMatchThreshold) break;

            var compA = unmatchedA[bestI];
            var compB = unmatchedB[bestJ];
            unmatchedA.RemoveAt(bestI);
            unmatchedB.RemoveAt(bestJ);

            string key = $"{compA.MatchKey} → {compB.MatchKey}";
            diffs.Add(ClassifyPair(key, compA, compB, tol, geometricSimilarity: bestScore));
        }
    }

    private double GeometricSimilarity(AssemblyComponent a, AssemblyComponent b)
        => scorer.Score(ToSyntheticFingerprint(a), ToSyntheticFingerprint(b), FallbackScoringWeights);

    private static PartFingerprint ToSyntheticFingerprint(AssemblyComponent c) => new(
        Id: Guid.Empty,
        ScannedFileId: Guid.Empty,
        FileSha256: string.Empty,
        ConfigName: c.MatchKey,
        ExtractorVersion: 0,
        SolidBodyCount: 1,
        SurfaceBodyCount: 0,
        SortedBoundingBoxM: c.SortedBoundingBoxM,
        VolumeM3: c.VolumeM3,
        SurfaceAreaM2: c.SurfaceAreaM2,
        MassKg: null,
        CenterOfMassM: null,
        FaceCount: c.FaceCount,
        EdgeCount: 0,
        VertexCount: 0,
        FeatureCount: 0,
        FeatureTypeHistogram: c.FaceTypeHistogram,
        Material: null,
        CustomProperties: new Dictionary<string, string>(),
        SolidWorksVersion: string.Empty,
        ExtractorVersionLabel: string.Empty,
        ExtractedUtc: DateTime.UtcNow,
        ChiralitySign: null,
        CoMOffsetInBB: null,
        SketchTextCutCount: 0,
        SuppressedSolidBodyCount: null,
        SuppressedBoundingBoxM: null,
        SuppressedVolumeM3: null,
        SuppressedSurfaceAreaM2: null,
        SuppressedFaceCount: null,
        SuppressedEdgeCount: null,
        SuppressedVertexCount: null,
        SourceFormat: "STEP",
        FaceGeometricSignature: c.FaceGeometricSignature);
}
