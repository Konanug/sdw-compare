namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Outcome of comparing one matched (or unmatched) component pair between two assembly STEP
/// files. Deliberately NOT <see cref="PartClassification"/> — that enum means a rigid-body
/// verified cross-file identity conclusion produced by the main scan/cluster/review pipeline.
/// This is a heuristic size/shape-delta comparison between two named parts across assembly
/// versions and must never be confused with, or leak into, that pipeline.
/// </summary>
public enum AssemblyDiffType
{
    Unchanged,
    // Geometry (bounding box / volume / face count) differs beyond tolerance.
    Modified,
    // Present only in assembly B.
    Added,
    // Present only in assembly A.
    Removed,
    // Same name matched, but geometry is wildly different — flagged instead of silently
    // reported as an ordinary Modified, since this usually means a name collision between
    // two unrelated parts rather than a real revision.
    SuspiciousMatch
}

/// <summary>
/// Diff result for one matched (or added/removed) component. <see cref="QuantityChanged"/>,
/// <see cref="OrientationChanged"/>, and <see cref="PositionChanged"/> are all independent of
/// <see cref="DiffType"/> and of each other — a part's own geometry (shape/size) is derived from
/// its canonical, un-instanced model and never affected by how an instance is placed in the
/// assembly, so "same part, different orientation" and "shape changed" are reported completely
/// separately, never conflated.
/// </summary>
public sealed record AssemblyComponentDiff(
    string MatchKey,
    AssemblyComponent? ComponentA,
    AssemblyComponent? ComponentB,
    AssemblyDiffType DiffType,
    bool QuantityChanged,
    int? InstanceCountA,
    int? InstanceCountB,
    // Per-axis delta as a fraction of ComponentA's dimension (sorted small→large, matching
    // SortedBoundingBoxM's own ordering). Null when either side is missing (Added/Removed).
    double[]? BoundingBoxDeltaPercent,
    // Two different "volume changed" signals, both worth showing since they're derived
    // differently: BoundingBoxVolumeDeltaPercent is the delta of the raw L×W×H box product —
    // exact/deterministic, computed straight from measured axis lengths, no estimation involved.
    // VolumeDeltaPercent is the existing heuristic body-volume estimate (StepGeometryEstimator —
    // analytic for a simple cylinder, ~55% of the bounding-box volume otherwise), which is a
    // closer guess at true solid volume but carries its own estimation error. Both are always
    // populated for any matched pair (even at 0%), never suppressed based on a "did it change
    // enough to mention" threshold — that threshold still applies to the prose in Reasons.
    double? BoundingBoxVolumeDeltaPercent,
    double? VolumeDeltaPercent,
    double? SurfaceAreaDeltaPercent,
    int? FaceCountDelta,
    // Populated only for pairs found via the geometric fallback matcher (renamed parts);
    // null for exact-name pairs, so the UI/report can show match provenance.
    double? GeometricSimilarityScore,
    // Short, plain-English bullet points a non-technical user can scan at a glance — no raw
    // measurements or internal jargon (those live in the numeric fields above for anyone who
    // wants them, e.g. the Excel export).
    IReadOnlyList<string> Reasons,
    // Null = not determined (multi-instance part, or the placement transform couldn't be
    // resolved) — never guessed. True/false only when both sides have exactly one instance
    // with a resolved placement.
    bool? OrientationChanged = null,
    bool? PositionChanged = null);

public sealed record AssemblyDiffSummary(
    string FileAPath,
    string FileBPath,
    DateTime ComparedUtc,
    IReadOnlyList<AssemblyComponentDiff> Components,
    IReadOnlyList<string> Warnings);
