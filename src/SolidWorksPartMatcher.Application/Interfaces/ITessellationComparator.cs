using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

/// <summary>
/// Stage 4.5 — tessellation-based tolerance comparison.
/// Extracts surface vertex clouds from both bodies via IBody2.GetTessellation,
/// aligns them by centre-of-mass translation and principal-axis orientation,
/// then classifies the pair using HD50 and surface-coverage metrics with a
/// configurable tolerance (default 0.5 mm).
/// </summary>
public interface ITessellationComparator
{
    Task<BodyEquivalenceResult> CompareAsync(
        ScannedFile fileA, PartFingerprint fpA,
        ScannedFile fileB, PartFingerprint fpB,
        double toleranceM,
        CancellationToken ct);
}
