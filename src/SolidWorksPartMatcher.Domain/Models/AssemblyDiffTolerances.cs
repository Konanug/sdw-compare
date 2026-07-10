namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Configurable thresholds for classifying an assembly component pair as Unchanged vs. Modified,
/// and for the geometric fallback matcher used to pair renamed components. Defaults are starting
/// points, not calibrated final values — see AssemblyDiffTolerances.Default remarks.
/// </summary>
public sealed record AssemblyDiffTolerances(
    // The SOLE classification signal: real (OCCT) volume delta, as a percentage. Bounding box was
    // removed from classification entirely — it produced skewed/false results, since a small local
    // feature can swing one bbox axis disproportionately while true volume barely moves, and vice
    // versa. Set to the reported display precision (2 decimals → 0.005%): a delta that rounds to
    // 0.00% is treated as no change, so a part is never flagged Modified — nor ticked as a volume
    // change — while its shown volume delta reads "0%". Real OCCT volumes of an unchanged part
    // routinely differ by a hair between two exports; those sub-0.005% wobbles are noise, not a
    // revision. Any change that shows as a nonzero % (>= 0.005%) is still reported as Modified.
    double VolumeDeltaPercentThreshold = 0.005,
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
    double PositionChangeMetersThreshold = 0.0005,
    // Minimum orientation-invariant face-signature agreement (0..1) required to accept a
    // GEOMETRY-fallback rename pairing — two differently-named components matched purely by shape.
    // The coarse similarity score alone can rate two genuinely different parts as similar (shared
    // volume, inflated topology from STEP's missing edge/vertex counts, similar surface-type mix),
    // producing false renames (a flat gasket paired with an L-bracket at 0.62). Requiring the actual
    // surfaces to agree — orientation-invariant, so a genuine rename that was rotated still passes —
    // stops that. Calibrated against real client data, where a false pairing scored 28.6% agreement
    // (only planes matched, zero curved-face overlap). Set to 0 to disable the gate. Only applied
    // when both components have a face signature (always true for real B-Rep parts).
    double RenameSignatureAgreementThreshold = 0.65)
{
    public static readonly AssemblyDiffTolerances Default = new();
}
