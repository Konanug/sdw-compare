namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// One rigid pose (orthonormal rotation frame + origin), used only internally while resolving and
/// composing assembly occurrence transforms. The rotation (X/Y/Z basis columns) is needed to
/// compose positions correctly through nested, possibly-rotated sub-assemblies — but only the
/// resulting <see cref="PositionM"/> is ever persisted or compared; orientation is deliberately
/// out of scope for the current position-only feature.
/// </summary>
internal sealed record AssemblyComponentPlacement(
    double[] PositionM,
    double[] XAxis,
    double[] YAxis,
    double[] ZAxis)
{
    public static readonly AssemblyComponentPlacement Identity =
        new([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]);
}

/// <summary>
/// Small 3D vector/rotation helpers for resolving and composing assembly occurrence placements
/// (used by <see cref="Step.Assembly.StepAssemblyStructureReader"/>). Deliberately minimal — just
/// what's needed to build an orthonormal frame from a STEP AXIS2_PLACEMENT_3D, compute one
/// occurrence's relative hop transform, compose hops root-to-leaf into a global pose, and measure
/// distance between two positions.
/// </summary>
internal static class PlacementMath
{
    /// <summary>
    /// Builds an orthonormal (X, Y, Z) basis from a STEP-style axis (Z direction) + ref_direction
    /// (approximate X direction, Gram-Schmidt orthogonalized against Z) pair.
    /// </summary>
    public static (double[] X, double[] Y, double[] Z) OrthonormalBasis(double[] axis, double[] refDir)
    {
        var z = Normalize(axis);
        var xRaw = Subtract(refDir, Scale(z, Dot(refDir, z)));
        var x = Normalize(xRaw);
        var y = Cross(z, x);
        return (x, y, z);
    }

    /// <summary>
    /// Computes frame2's placement relative to frame1 — i.e. "where frame2 sits when expressed
    /// in frame1's own coordinate system". This is one NAUO hop's local transform (the child
    /// occurrence's pose within its immediate parent's frame).
    /// </summary>
    public static AssemblyComponentPlacement ComputeRelativePlacement(
        double[] origin1, double[] axis1, double[] refDir1,
        double[] origin2, double[] axis2, double[] refDir2)
    {
        var (x1, y1, z1) = OrthonormalBasis(axis1, refDir1);
        var (x2, y2, z2) = OrthonormalBasis(axis2, refDir2);

        // R1^T * (origin2 - origin1) — position of frame2's origin expressed in frame1's basis.
        var delta = Subtract(origin2, origin1);
        var relPosition = new[] { Dot(x1, delta), Dot(y1, delta), Dot(z1, delta) };

        // R1^T * R2, column by column (R1^T rows are x1/y1/z1; R2 columns are x2/y2/z2).
        double[] RelColumn(double[] col2) => [Dot(x1, col2), Dot(y1, col2), Dot(z1, col2)];
        return new AssemblyComponentPlacement(relPosition, RelColumn(x2), RelColumn(y2), RelColumn(z2));
    }

    /// <summary>
    /// Composes a child hop (expressed in its parent's local frame) onto its parent's already-
    /// resolved global pose, yielding the child's global pose:
    /// <code>R_global = R_parent · R_hop ;  t_global = t_parent + R_parent · t_hop</code>
    /// Full rigid composition (rotation included) is required even though only position is
    /// reported: a child's local offset must be rotated into the parent's orientation before
    /// adding the parent origin, or any non-identity intermediate rotation (a sub-assembly
    /// mounted at an angle) yields a silently wrong global position.
    /// </summary>
    public static AssemblyComponentPlacement ComposeGlobal(
        AssemblyComponentPlacement parentGlobal, AssemblyComponentPlacement localHop)
    {
        // Re-express a vector given in the parent's local coordinates using the parent's global
        // basis: v_x·X_parent + v_y·Y_parent + v_z·Z_parent.
        double[] IntoParent(double[] v) =>
        [
            v[0] * parentGlobal.XAxis[0] + v[1] * parentGlobal.YAxis[0] + v[2] * parentGlobal.ZAxis[0],
            v[0] * parentGlobal.XAxis[1] + v[1] * parentGlobal.YAxis[1] + v[2] * parentGlobal.ZAxis[1],
            v[0] * parentGlobal.XAxis[2] + v[1] * parentGlobal.YAxis[2] + v[2] * parentGlobal.ZAxis[2],
        ];

        var globalX = IntoParent(localHop.XAxis);
        var globalY = IntoParent(localHop.YAxis);
        var globalZ = IntoParent(localHop.ZAxis);
        var globalPos = Add(parentGlobal.PositionM, IntoParent(localHop.PositionM));

        return new AssemblyComponentPlacement(globalPos, globalX, globalY, globalZ);
    }

    /// <summary>Euclidean distance between two positions, in the same length units.</summary>
    public static double PositionDistance(double[] a, double[] b) => Norm(Subtract(a, b));

    private static double[] Normalize(double[] v)
    {
        double n = Norm(v);
        return n < 1e-12 ? v : [v[0] / n, v[1] / n, v[2] / n];
    }

    private static double Norm(double[] v) => Math.Sqrt(Dot(v, v));

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Cross(double[] a, double[] b) =>
    [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    ];

    private static double[] Add(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

    private static double[] Subtract(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];
}
