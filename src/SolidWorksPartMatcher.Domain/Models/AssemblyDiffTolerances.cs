namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Configurable thresholds for classifying an assembly component pair as Unchanged vs. Modified,
/// and for the geometric fallback matcher used to pair renamed components. Defaults are starting
/// points, not calibrated final values — see AssemblyDiffTolerances.Default remarks.
/// </summary>
public sealed record AssemblyDiffTolerances(
    // The SOLE classification signal: real (OCCT) volume delta. Bounding box was removed from
    // classification entirely — it produced skewed/false results, since a small local feature
    // can swing one bbox axis disproportionately while true volume barely moves, and vice versa.
    // This is a near-zero floor purely to absorb floating-point noise when re-measuring a
    // bit-identical part — not a real tolerance: any genuine volume difference, however small,
    // is reported as Modified.
    double VolumeDeltaPercentThreshold = 1e-6,
    // Geometric-similarity score (WeightedCandidateScorer-based) above which two unmatched
    // components (different names) are still paired as a probable rename.
    double GeometricSimilarityMatchThreshold = 0.55,
    // Below this similarity, a name match is reclassified SuspiciousMatch instead of Modified.
    // (A separate "fallback match with a big volume delta" suspicion trigger was considered and
    // removed: once a rename candidate clears this similarity bar, there's no more reliable way
    // to second-guess it by size alone than for any other pair — a confirmed rename with a big
    // volume delta is just a big revision, not grounds for suspicion.)
    double SuspiciousMatchThreshold = 0.35,
    // Two occurrence positions count as "the same place" when within this many metres of each
    // other. An instance is considered moved only if it has no counterpart position within this
    // distance on the other side (see OccurrencePositionComparer). Uncalibrated starting point,
    // to be tuned against real files — wider than a bit-exact match to absorb STEP export
    // rounding, tighter than any real relocation.
    double PositionChangeMetersThreshold = 0.0005)
{
    public static readonly AssemblyDiffTolerances Default = new();
}
