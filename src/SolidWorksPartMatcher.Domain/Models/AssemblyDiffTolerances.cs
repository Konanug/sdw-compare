namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Configurable thresholds for classifying an assembly component pair as Unchanged vs. Modified,
/// and for the geometric fallback matcher used to pair renamed components. Defaults are starting
/// points, not calibrated final values — see AssemblyDiffTolerances.Default remarks.
/// </summary>
public sealed record AssemblyDiffTolerances(
    // A component counts as "changed" on an axis when the delta exceeds BOTH the relative
    // percentage AND the absolute floor (the floor wins for small parts, where a fixed-percent
    // threshold would be unrealistically tight). Mirrors WeightedCandidateScorer's existing
    // FeatureToleranceM absolute-floor pattern.
    double BoundingBoxDeltaPercentThreshold = 1.0,
    double BoundingBoxAbsoluteFloorM = 0.0005,
    // Wider than the bounding-box threshold because EstimateVolume/EstimateSurfaceArea are
    // already heuristic (55%-of-bounding-box fallback) with their own error band — a tight
    // volume threshold here would just be estimation noise, not a real signal.
    double VolumeDeltaPercentThreshold = 2.0,
    // Geometric-similarity score (WeightedCandidateScorer-based) above which two unmatched
    // components (different names) are still paired as a probable rename.
    double GeometricSimilarityMatchThreshold = 0.55,
    // Below this similarity, a name match is reclassified SuspiciousMatch instead of Modified.
    double SuspiciousMatchThreshold = 0.35,
    // A fallback (geometry-only) match is a genuine "possible rename" only when the two parts
    // are actually close in size — the blended similarity score used to find the best candidate
    // pair can still be high (e.g. 0.80) even when volume differs by 30-40%, since it also
    // weighs topology/face-histogram/surface-area. Reporting "possible rename" alongside a huge
    // volume delta is self-contradictory (a renamed-but-unmodified part should look almost
    // identical), so pairs found via the fallback matcher are downgraded to SuspiciousMatch
    // instead of Modified when the volume delta exceeds this — much tighter than
    // VolumeDeltaPercentThreshold, which governs genuine same-part Modified deltas, not
    // whether a fallback pairing is even plausible as "the same part" in the first place.
    double FallbackSuspiciousVolumeDeltaPercent = 15.0,
    // Two single-instance placements are "the same orientation" when the rotation needed to
    // align one to the other is below this many degrees — accommodates STEP export rounding
    // noise, not a real reorientation.
    double OrientationChangeDegreesThreshold = 2.0,
    // Two single-instance placements are "the same position" when they're within this many
    // metres of each other — wider than BoundingBoxAbsoluteFloorM since position is compounded
    // through an extra transform-resolution step (source frame → target frame) with its own
    // small rounding error budget.
    double PositionChangeMetersThreshold = 0.001)
{
    public static readonly AssemblyDiffTolerances Default = new();
}
