namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Configurable thresholds for the STEP-only geometric-evidence vote (see the scan orchestrator's
/// Stage 3.6). The vote counts orientation-invariant signals that agree between two STEP parts and,
/// when enough do, escalates the pair to <see cref="PartClassification.PossibleMatch"/> so a human
/// reviews it — it never confirms an exact match or auto-merges. Bounding box is deliberately absent
/// (it is orientation-dependent and unreliable); every signal here is orientation-invariant.
///
/// Defaults are the user-agreed starting points, not calibrated final values.
/// </summary>
public sealed record StepMatchTolerances(
    // Flag 1 — real (OCCT) volume: raised when |Δvol| / max(volA, volB) is within this fraction.
    double VolumeDeltaFraction = 0.05,        // 5%
    // Flag 4 — tolerant face signature: two per-face radii count as equal within this relative
    // tolerance (the tolerant replacement for today's exact `{r:R}` radius match).
    double RadiusRelativeTolerance = 0.01,    // 1%
    // Flag 4 — minimum fraction of faces whose descriptors match (radii compared within the
    // tolerance above, everything else exact) for the signature to count as agreeing.
    double SignatureMatchFraction = 0.95,     // 95%
    // Escalate to review when at least this many of the four flags are raised.
    int MinimumAgreeingFlags = 3)
{
    public static readonly StepMatchTolerances Default = new();
}
