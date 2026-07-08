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
    // Real (OCCT) volume differs from zero — see AssemblyComponentDiff.VolumeDeltaPercent.
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
/// Diff result for one matched (or added/removed) component. <see cref="QuantityChanged"/> is
/// independent of <see cref="DiffType"/> — a part's own shape/volume is derived from its
/// canonical, un-instanced model and never affected by how many times it's instanced.
/// </summary>
public sealed record AssemblyComponentDiff(
    string MatchKey,
    AssemblyComponent? ComponentA,
    AssemblyComponent? ComponentB,
    AssemblyDiffType DiffType,
    bool QuantityChanged,
    int? InstanceCountA,
    int? InstanceCountB,
    // The sole "did this part change" signal — a real (OCCT) volume delta, not a bounding-box
    // estimate. Bounding box is deliberately not used for classification: a small local feature
    // can swing one bbox axis disproportionately while true volume barely moves, and vice versa,
    // which produced skewed/false classifications. Always populated for any matched pair (even
    // at 0%), never suppressed based on a "did it change enough to mention" threshold.
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
    // Whether any instance of this product sits in a different position in the assembly between
    // the two versions. Deliberately a coarse per-product boolean — NOT a count and NOT a
    // per-instance identification (that pinpointing was inconsistent and was removed). Reported
    // independently of DiffType (a part can change shape/volume and also move, or move without
    // any shape change). Null = not determined (no resolvable occurrence positions on one/both
    // sides — never treated as "unchanged"). True = at least one instance has no counterpart
    // within tolerance. False = every instance matched a counterpart within tolerance.
    bool? PositionChanged = null);

public sealed record AssemblyDiffSummary(
    string FileAPath,
    string FileBPath,
    DateTime ComparedUtc,
    IReadOnlyList<AssemblyComponentDiff> Components,
    IReadOnlyList<string> Warnings);
