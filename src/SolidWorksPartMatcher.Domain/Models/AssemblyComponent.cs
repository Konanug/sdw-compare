namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// A single instance's placement within its immediate parent assembly — the relative transform
/// carried by the STEP NEXT_ASSEMBLY_USAGE_OCCURRENCE's ITEM_DEFINED_TRANSFORMATION, resolved
/// to an orthonormal frame (basis vectors already Gram-Schmidt orthogonalized). Compared between
/// two assembly versions to detect "same part, moved/rotated" independently of geometry identity
/// (which is derived from the part's own canonical shape and is unaffected by placement).
/// </summary>
public sealed record AssemblyComponentPlacement(
    double[] PositionM,
    double[] XAxis,
    double[] YAxis,
    double[] ZAxis);

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
    // This instance's placement within its immediate parent assembly. Only populated when the
    // product has exactly one instance in the assembly (InstanceCount == 1) — with multiple
    // instances there's no single unambiguous placement to report, so this stays null rather
    // than guessing which occurrence to compare. Null also when the transform chain couldn't
    // be resolved. Orientation/position comparison between assembly versions only happens when
    // both sides have a non-null placement.
    AssemblyComponentPlacement? Placement = null);
