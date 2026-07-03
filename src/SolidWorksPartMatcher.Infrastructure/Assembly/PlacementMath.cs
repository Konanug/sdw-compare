using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// Small 3D vector/rotation helpers shared between placement extraction
/// (<see cref="Step.Assembly.StepAssemblyStructureReader"/>) and placement comparison
/// (<see cref="AssemblyComponentMatcher"/>). Deliberately minimal — just what's needed to
/// build an orthonormal frame from a STEP AXIS2_PLACEMENT_3D (axis + ref_direction) and to
/// measure the rotation/translation between two such frames.
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
    /// in frame1's own coordinate system". When frame1 is the identity (as it always was in the
    /// real Test6 files this was validated against), this reduces to frame2's own values.
    /// General composition is used anyway so a non-identity source frame doesn't silently break.
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
        var relX = RelColumn(x2);
        var relY = RelColumn(y2);
        var relZ = RelColumn(z2);

        return new AssemblyComponentPlacement(relPosition, relX, relY, relZ);
    }

    /// <summary>Euclidean distance between two placements' positions, in the same length units.</summary>
    public static double PositionDistance(AssemblyComponentPlacement a, AssemblyComponentPlacement b)
        => Norm(Subtract(b.PositionM, a.PositionM));

    /// <summary>
    /// Rotation angle (degrees) needed to align placement a's orientation to placement b's —
    /// via the standard trace-based formula for the angle of a rotation matrix
    /// R_rel = R_a^T * R_b: angle = arccos((trace(R_rel) - 1) / 2).
    /// </summary>
    public static double OrientationAngleDegrees(AssemblyComponentPlacement a, AssemblyComponentPlacement b)
    {
        double[] RelColumn(double[] col2) => [Dot(a.XAxis, col2), Dot(a.YAxis, col2), Dot(a.ZAxis, col2)];
        var relX = RelColumn(b.XAxis);
        var relY = RelColumn(b.YAxis);
        var relZ = RelColumn(b.ZAxis);
        double trace = relX[0] + relY[1] + relZ[2];
        double cosAngle = Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0);
        return Math.Acos(cosAngle) * (180.0 / Math.PI);
    }

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

    private static double[] Subtract(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Scale(double[] a, double s) => [a[0] * s, a[1] * s, a[2] * s];
}
