using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using SolidWorksPartMatcher.Infrastructure.Step;
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
/// Exception: a component with no measurable volume on one or both sides (a non-solid
/// shell/surface in the STEP file reports zero OCCT volume) has an undefined volume delta and
/// must never read as a "-100% Modified"; such a pair falls back to the geometric-similarity
/// score to decide Unchanged vs. Modified vs. Suspicious. See <see cref="ClassifyPair"/>.
///
/// Position tracking is additive and orthogonal to shape classification: whether any instance of
/// a part sits in a different place in the assembly is reported as its own "What changed" bullet
/// (see <see cref="OccurrencePositionComparer"/>), never influencing <see cref="AssemblyDiffType"/>
/// and never suppressed by it — a part can change shape/volume and also move, or move without any
/// shape change. It is a coarse per-product yes/no, never a per-instance identification. Only
/// position is compared; orientation/rotation remains out of scope.
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

    // A real solid always has strictly positive volume; a value below this floor means OCCT (or
    // the bounding-box estimate) could not measure a closed solid — the part is stored as a
    // non-solid shell/surface. 1e-12 m³ = 1e-3 mm³, far smaller than any real machined part, so it
    // only ever catches genuinely unmeasurable geometry, never a tiny-but-real part.
    private const double MinMeasurableVolumeM3 = 1e-12;

    // Radii within this relative tolerance count as the same, so tiny STEP export rounding doesn't
    // read as a shape difference (mirrors the main pipeline's StepMatchTolerances default).
    private const double RadiusRelativeTolerance = 0.01;

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
        double saDeltaPct = RelativeDeltaPercent(compA.SurfaceAreaM2, compB.SurfaceAreaM2);
        int faceDelta = compB.FaceCount - compA.FaceCount;

        bool quantityChanged = compA.InstanceCount.HasValue && compB.InstanceCount.HasValue
            && compA.InstanceCount.Value != compB.InstanceCount.Value;

        // A non-positive volume is not a real measurement — it means OCCT (or the bounding-box
        // estimate) found no closed solid, because the STEP file stores that part as a non-solid
        // shell/surface (the real Test6 CE26209H01 case). That is a file-representation detail, NOT
        // a physical change, so a part that measures zero on one side must never be reported as a
        // "-100% Modified". When either side is unmeasurable the volume delta is undefined; classify
        // on shape alone via the geometric-similarity score instead.
        bool volumeMeasurable = compA.VolumeM3 >= MinMeasurableVolumeM3
                             && compB.VolumeM3 >= MinMeasurableVolumeM3;
        double? volDeltaPct = volumeMeasurable
            ? RelativeDeltaPercent(compA.VolumeM3, compB.VolumeM3)
            : null;

        // Suspicion trigger: an EXACT-NAME match whose overall shape looks nothing alike
        // (suspiciousNameCollision) — "is this name being reused for a different part entirely?".
        // Not a volume-based judgment call; a confirmed rename with a big volume delta is just a
        // big revision (the same reasoning that removed bounding box from classification entirely).
        AssemblyDiffType diffType;
        bool suspiciousNameCollision = false;

        if (!volumeMeasurable)
        {
            // No usable volume signal — decide on shape alone. Identical shape → Unchanged (with a
            // "volume not comparable" note); clearly different shape → Suspicious; in between →
            // Modified. This is what stops an accurately-reported zero volume reading as Modified.
            double similarity = geometricSimilarity ?? GeometricSimilarity(compA, compB);
            if (similarity >= tol.GeometricSimilarityMatchThreshold)
                diffType = AssemblyDiffType.Unchanged;
            else if (similarity < tol.SuspiciousMatchThreshold)
            {
                diffType = AssemblyDiffType.SuspiciousMatch;
                suspiciousNameCollision = true;
            }
            else
                diffType = AssemblyDiffType.Modified;
        }
        else if (Math.Abs(volDeltaPct!.Value) <= tol.VolumeDeltaPercentThreshold)
        {
            // Volume is the SOLE change signal. tol.VolumeDeltaPercentThreshold is a near-zero
            // floating-point floor, not a real tolerance: any genuine volume difference, however
            // small, is reported as Modified.
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
                diffType = AssemblyDiffType.Modified;
        }

        // Position comparison is additive and independent of DiffType (see class doc): a coarse
        // per-product yes/no over the two versions' occurrence-position sets, never gated behind
        // the shape classification and never pinpointing which/how many instances moved.
        bool? positionChanged = OccurrencePositionComparer.PositionChanged(
            compA.OccurrencePositionsM, compB.OccurrencePositionsM, tol.PositionChangeMetersThreshold);

        var reasons = BuildReasons(
            quantityChanged, compA.InstanceCount, compB.InstanceCount,
            suspiciousNameCollision,
            geometricSimilarity.HasValue && !suspiciousNameCollision,
            diffType, volDeltaPct, positionChanged, volumeUnmeasurable: !volumeMeasurable);

        return new AssemblyComponentDiff(
            key, compA, compB, diffType, quantityChanged,
            compA.InstanceCount, compB.InstanceCount,
            volDeltaPct, saDeltaPct, faceDelta,
            geometricSimilarity, reasons, positionChanged);
    }

    // Builds short, plain-English bullet points — no raw internal measurements, no jargon.
    // Numeric detail (volume/surface-area deltas, similarity scores, etc.) still lives on the
    // AssemblyComponentDiff record itself for anyone who wants it (e.g. the Excel export).
    private static List<string> BuildReasons(
        bool quantityChanged, int? instanceCountA, int? instanceCountB,
        bool suspiciousNameCollision, bool matchedByGeometry,
        AssemblyDiffType diffType, double? volDeltaPct, bool? positionChanged,
        bool volumeUnmeasurable)
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

            if (diffType == AssemblyDiffType.Modified)
            {
                if (volDeltaPct is { } v)
                    reasons.Add(v > 0
                        ? $"Volume increased by {Math.Abs(v):0.####}%."
                        : $"Volume decreased by {Math.Abs(v):0.####}%.");
                else
                    // Modified on shape alone because the volume could not be measured.
                    reasons.Add("Shape changed (volume could not be measured — non-solid geometry).");
            }
            else if (volumeUnmeasurable && diffType == AssemblyDiffType.Unchanged)
            {
                // An accurately-reported zero/undefined volume is a non-solid file representation,
                // not a real change — say so instead of silently reporting "no differences".
                reasons.Add("Volume not comparable (non-solid geometry); shape is unchanged.");
            }
        }

        // Position is reported unconditionally — additive to (and never suppressed by) the shape
        // classification above. A coarse per-product yes/no: no count, no per-instance detail.
        if (positionChanged == true)
            reasons.Add("Position changed in the assembly.");

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
        // A fallback pairing must clear the coarse similarity bar AND an orientation-invariant
        // face-signature agreement (the actual surfaces must match, robust to how the part is
        // rotated). The signature gate is only applied when both components carry a signature —
        // always true for real B-Rep parts; a component with no faces would have been dropped
        // upstream, so an empty signature only occurs for hand-built test fixtures.
        double sigGate = tol.RenameSignatureAgreementThreshold;

        while (unmatchedA.Count > 0 && unmatchedB.Count > 0)
        {
            double bestScore = -1;
            int bestI = -1, bestJ = -1;
            for (int i = 0; i < unmatchedA.Count; i++)
            {
                for (int j = 0; j < unmatchedB.Count; j++)
                {
                    double score = GeometricSimilarity(unmatchedA[i], unmatchedB[j]);
                    if (score <= bestScore) continue;
                    if (score < tol.GeometricSimilarityMatchThreshold) continue;
                    if (sigGate > 0
                        && unmatchedA[i].FaceGeometricSignature is { Count: > 0 }
                        && unmatchedB[j].FaceGeometricSignature is { Count: > 0 }
                        && OrientationInvariantSignatureAgreement(
                            unmatchedA[i].FaceGeometricSignature,
                            unmatchedB[j].FaceGeometricSignature) < sigGate)
                        continue; // coarse stats agree but the real surfaces do not — not a rename
                    bestScore = score; bestI = i; bestJ = j;
                }
            }

            if (bestI < 0) break; // no remaining pair clears both gates → leave as Removed / Added

            var compA = unmatchedA[bestI];
            var compB = unmatchedB[bestJ];
            unmatchedA.RemoveAt(bestI);
            unmatchedB.RemoveAt(bestJ);

            string key = $"{compA.MatchKey} → {compB.MatchKey}";
            diffs.Add(ClassifyPair(key, compA, compB, tol, geometricSimilarity: bestScore));
        }
    }

    /// <summary>
    /// Fraction (0..1) of faces that agree between two components' signatures, ignoring orientation
    /// and position — surface TYPE plus radius/size only. Robust to a genuine rename that was stored
    /// rotated (whose exact, axis-bearing signatures would disagree), yet still separates two
    /// genuinely different shapes (their surface types and radii don't line up). Greedy multiset
    /// pairing over max(faceCountA, faceCountB); 0 when either signature is missing.
    /// </summary>
    internal static double OrientationInvariantSignatureAgreement(
        IReadOnlyList<string>? sigA, IReadOnlyList<string>? sigB)
        => FaceSignatureMatcher.AgreementFraction(
            sigA, sigB, FaceSignatureMatcher.OrientationInvariant, RadiusRelativeTolerance);

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
