using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;

namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// Matches components between two parsed assembly versions and classifies each pair.
/// Pure logic — no P21/file dependency — so it's easy to unit test against hand-built
/// <see cref="AssemblyComponent"/> fixtures.
///
/// Classification is driven SOLELY by real (OCCT) volume — bounding box is no longer part of
/// the "did this part change" decision at all (it produced skewed/false results: a small local
/// feature could swing one bbox axis disproportionately while true volume barely moved, and
/// vice versa). Bounding box is still used, unchanged, as one input among several to the
/// geometric-fallback SIMILARITY score (<see cref="GeometricSimilarity"/>) that identifies
/// probable renames — that's a different concern (which parts correspond to which) from
/// deciding whether a confirmed-same part changed.
///
/// Orientation/position tracking has been removed entirely for now (to be revisited later) —
/// this matcher only ever reports shape/volume and quantity differences.
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
                VolumeDeltaPercent: null, SurfaceAreaDeltaPercent: null,
                FaceCountDelta: null, GeometricSimilarityScore: null,
                Reasons: ["Part removed."]));
        foreach (var compB in unmatchedB)
            diffs.Add(new AssemblyComponentDiff(
                MatchKey: compB.MatchKey, ComponentA: null, ComponentB: compB,
                DiffType: AssemblyDiffType.Added, QuantityChanged: false,
                InstanceCountA: 0, InstanceCountB: compB.InstanceCount,
                VolumeDeltaPercent: null, SurfaceAreaDeltaPercent: null,
                FaceCountDelta: null, GeometricSimilarityScore: null,
                Reasons: ["Part added."]));

        var ordered = diffs
            .OrderBy(d => SortPriority(d))
            .ThenBy(d => d.MatchKey, StringComparer.Ordinal)
            .ToList();

        return new AssemblyDiffSummary(fileAPath, fileBPath, DateTime.UtcNow, ordered, warnings);
    }

    private static int SortPriority(AssemblyComponentDiff d) => d.DiffType switch
    {
        AssemblyDiffType.Removed => 0,
        AssemblyDiffType.Added => 1,
        AssemblyDiffType.SuspiciousMatch => 2,
        AssemblyDiffType.Modified => 3,
        AssemblyDiffType.Unchanged => d.QuantityChanged ? 4 : 5,
        _ => 6
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
        double volDeltaPct = RelativeDeltaPercent(compA.VolumeM3, compB.VolumeM3);
        double saDeltaPct  = RelativeDeltaPercent(compA.SurfaceAreaM2, compB.SurfaceAreaM2);
        int faceDelta      = compB.FaceCount - compA.FaceCount;

        bool quantityChanged = compA.InstanceCount.HasValue && compB.InstanceCount.HasValue
            && compA.InstanceCount.Value != compB.InstanceCount.Value;

        // Volume is the SOLE "did this part change" signal — see this class's own doc comment
        // for why bounding box was removed from this decision entirely. tol.VolumeDeltaPercentThreshold
        // is a near-zero floor (floating-point noise only), not a real tolerance: any genuine
        // volume difference, however small, is reported as Modified.
        bool volumeUnchanged = Math.Abs(volDeltaPct) <= tol.VolumeDeltaPercentThreshold;

        // Only one suspicion trigger remains: an EXACT-NAME match whose overall shape looks
        // nothing alike (suspiciousNameCollision) — a real, distinct signal ("is this name being
        // reused for a different part entirely?"), not a volume-based judgment call. The former
        // second trigger ("matched by shape, but sizes are very different", gating a fallback
        // rename-match's acceptance on its volume delta) was removed: once a fallback pairing
        // clears the geometric-similarity bar, we have no more reliable way to independently
        // second-guess it via size alone than we do for any other pair — that's the same reason
        // bounding box was dropped from classification entirely (see this class's own doc
        // comment). A confirmed rename with a big volume delta is just a big revision.
        AssemblyDiffType diffType;
        bool suspiciousNameCollision = false;

        if (volumeUnchanged)
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
            else
            {
                diffType = AssemblyDiffType.Modified;
            }
        }

        var reasons = BuildReasons(
            quantityChanged, compA.InstanceCount, compB.InstanceCount,
            suspiciousNameCollision,
            geometricSimilarity.HasValue && !suspiciousNameCollision,
            diffType, volDeltaPct);

        return new AssemblyComponentDiff(
            key, compA, compB, diffType, quantityChanged,
            compA.InstanceCount, compB.InstanceCount,
            volDeltaPct, saDeltaPct, faceDelta,
            geometricSimilarity, reasons);
    }

    // Builds short, plain-English bullet points — no raw internal measurements, no jargon.
    // Numeric detail (volume/surface-area deltas, similarity scores, etc.) still lives on the
    // AssemblyComponentDiff record itself for anyone who wants it (e.g. the Excel export).
    private static List<string> BuildReasons(
        bool quantityChanged, int? instanceCountA, int? instanceCountB,
        bool suspiciousNameCollision, bool matchedByGeometry,
        AssemblyDiffType diffType, double volDeltaPct)
    {
        var reasons = new List<string>();

        if (quantityChanged)
            reasons.Add($"Quantity changed from {instanceCountA} to {instanceCountB}.");

        if (suspiciousNameCollision)
        {
            reasons.Add("Same name, but geometry is very different.");
            reasons.Add("Likely two different parts.");
        }
        else
        {
            if (matchedByGeometry)
                reasons.Add("Same shape, different name (likely renamed).");

            // Modified is now reachable ONLY via a nonzero volume delta, so this always has
            // something real to say — no more "different overall size" fallback for a change
            // that couldn't otherwise be explained.
            if (diffType == AssemblyDiffType.Modified)
                reasons.Add(volDeltaPct > 0
                    ? $"Volume increased by {Math.Abs(volDeltaPct):0.####}%."
                    : $"Volume decreased by {Math.Abs(volDeltaPct):0.####}%.");
        }

        if (reasons.Count == 0)
            reasons.Add("No differences found.");

        return reasons;
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
