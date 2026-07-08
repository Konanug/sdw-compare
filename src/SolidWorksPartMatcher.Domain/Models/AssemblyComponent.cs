namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// One unique part (PRODUCT with real B-Rep geometry) parsed out of a STEP assembly file.
/// One record per distinct part identity — repeated occurrences of the same part within the
/// assembly are folded into <see cref="InstanceCount"/>, not represented as separate records.
/// </summary>
public sealed record AssemblyComponent(
    string ProductId,
    string ProductName,
    // ProductName if non-empty, else ProductId — the key used to pair this component
    // against the other assembly version's components.
    string MatchKey,
    // Total instances of this product across the whole assembly tree (root-to-leaf NAUO
    // path count, correctly multiplied through nested sub-assembly repetition).
    // Null when the NAUO graph could not be resolved for this product (malformed/cyclic
    // reference) — excluded from quantity comparison but still shape-diffed.
    int? InstanceCount,
    double[] SortedBoundingBoxM,
    double VolumeM3,
    double SurfaceAreaM2,
    int FaceCount,
    IReadOnlyDictionary<string, int> FaceTypeHistogram,
    IReadOnlyList<string> FaceGeometricSignature,
    // Full transitive P21 entity-id closure backing this component's shape — reused by
    // StepComponentSnippetWriter so it never needs recomputing for 3D-diff drill-down.
    IReadOnlyList<int> EntityClosure,
    // Fully-composed (root-frame) 3D position of every occurrence of this product across the
    // whole assembly tree — one entry per instance, position only (orientation is deliberately
    // out of scope). Composed through every ancestor sub-assembly's transform, so a part that
    // moved because its containing sub-assembly moved is captured. Empty when no occurrence's
    // transform chain could be resolved. Compared as an unordered set between two assembly
    // versions (see OccurrencePositionComparer) — never matched by index or persistent id.
    IReadOnlyList<double[]> OccurrencePositionsM);
