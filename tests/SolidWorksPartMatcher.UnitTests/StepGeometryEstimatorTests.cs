using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Step;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class StepGeometryEstimatorTests
{
    // 10mm x 20mm x 30mm box, expressed as its 8 corners (half-extents below).
    private static List<double[]> BoxCorners(double hx, double hy, double hz)
    {
        var corners = new List<double[]>();
        foreach (var sx in new[] { -1.0, 1.0 })
            foreach (var sy in new[] { -1.0, 1.0 })
                foreach (var sz in new[] { -1.0, 1.0 })
                    corners.Add([sx * hx, sy * hy, sz * hz]);
        return corners;
    }

    // Arbitrary compound rotation (X, then Y, then Z) plus a translation — simulates the same
    // physical part being embedded in a STEP file's local coordinate frame at an arbitrary
    // orientation/offset, exactly the scenario that previously distorted assembly component
    // bounding boxes.
    private static double[] RotateAndTranslate(double[] p, double ax, double ay, double az, double[] t)
    {
        double x = p[0], y = p[1], z = p[2];

        double y1 = y * Math.Cos(ax) - z * Math.Sin(ax);
        double z1 = y * Math.Sin(ax) + z * Math.Cos(ax);
        double x1 = x;

        double x2 = x1 * Math.Cos(ay) + z1 * Math.Sin(ay);
        double z2 = -x1 * Math.Sin(ay) + z1 * Math.Cos(ay);
        double y2 = y1;

        double x3 = x2 * Math.Cos(az) - y2 * Math.Sin(az);
        double y3 = x2 * Math.Sin(az) + y2 * Math.Cos(az);
        double z3 = z2;

        return [x3 + t[0], y3 + t[1], z3 + t[2]];
    }

    [Fact]
    public void AlignToPrincipalAxes_AxisAlignedBox_PreservesBoundingBox()
    {
        var corners = BoxCorners(0.005, 0.01, 0.015); // 10x20x30mm

        var aligned = StepGeometryEstimator.AlignToPrincipalAxes(corners);
        var bb = StepGeometryEstimator.ComputeSortedBoundingBox(aligned);

        bb[0].Should().BeApproximately(0.01, 1e-9);
        bb[1].Should().BeApproximately(0.02, 1e-9);
        bb[2].Should().BeApproximately(0.03, 1e-9);
    }

    [Fact]
    public void AlignToPrincipalAxes_RotatedAndTranslatedBox_YieldsSameBoundingBoxAsUnrotated()
    {
        // The core regression case: the same physical part (10x20x30mm box), but its raw point
        // cloud is rotated and offset — as could happen when the same part is embedded at an
        // arbitrary orientation in a STEP assembly. Before this fix, ComputeSortedBoundingBox
        // applied directly to these raw points would report a distorted, much larger box (an
        // axis-aligned box around a tilted box is always >= the true box), which is exactly what
        // caused real "same part, different orientation" pairs to be misclassified as
        // SuspiciousMatch/Modified.
        var corners = BoxCorners(0.005, 0.01, 0.015);
        var rotated = corners
            .Select(p => RotateAndTranslate(p, 0.4, 0.7, 1.1, [2.5, -1.3, 0.8]))
            .ToList();

        var aligned = StepGeometryEstimator.AlignToPrincipalAxes(rotated);
        var bb = StepGeometryEstimator.ComputeSortedBoundingBox(aligned);

        bb[0].Should().BeApproximately(0.01, 1e-9);
        bb[1].Should().BeApproximately(0.02, 1e-9);
        bb[2].Should().BeApproximately(0.03, 1e-9);
    }

    [Fact]
    public void AlignToPrincipalAxes_WithoutAlignment_RotatedBoxWouldReportLargerBox()
    {
        // Sanity check that the rotated fixture used above actually exercises the bug it claims
        // to: measuring the RAW (unaligned) rotated points must give a larger/distorted box than
        // the true 10x20x30mm dimensions, proving AlignToPrincipalAxes is doing real work above,
        // not passing through a fixture that was already axis-aligned by coincidence.
        var corners = BoxCorners(0.005, 0.01, 0.015);
        var rotated = corners
            .Select(p => RotateAndTranslate(p, 0.4, 0.7, 1.1, [2.5, -1.3, 0.8]))
            .ToList();

        var rawBb = StepGeometryEstimator.ComputeSortedBoundingBox(rotated);

        (rawBb[0] * rawBb[1] * rawBb[2]).Should().BeGreaterThan(0.01 * 0.02 * 0.03 * 1.05);
    }

    // One case per surface type, so a regression names the shape that broke.
    //   Cylinder: radius is field 1; the axis (fields 2-4) stays in the key.
    //   Cone:     half-angle (field 1) + axis (fields 3-5) in the key; radius (field 2) extracted.
    //   Plane:    no radius — the whole descriptor is the key.
    [Theory]
    [InlineData("CYLINDER|0.005|0.0000|0.0000|1.0000", "CYLINDER|0.0000|0.0000|1.0000", new[] { 0.005 })]
    [InlineData("CONE|0.100000|0.02|0.0000|0.0000|1.0000", "CONE|0.100000|0.0000|0.0000|1.0000", new[] { 0.02 })]
    [InlineData("SPHERE|0.01", "SPHERE", new[] { 0.01 })]
    [InlineData("TORUS|0.02|0.005", "TORUS", new[] { 0.02, 0.005 })]
    [InlineData("PLANE|1.0000|0.0000|0.0000", "PLANE|1.0000|0.0000|0.0000", new double[0])]
    public void ParseDescriptor_SeparatesRadiiFromShapeKey(
        string descriptor, string expectedKey, double[] expectedRadii)
    {
        var (key, radii) = StepGeometryEstimator.ParseDescriptor(descriptor);

        key.Should().Be(expectedKey);
        radii.Should().Equal(expectedRadii);
    }

    [Fact]
    public void EstimateSurfaceArea_NonCylinderShape_FallsBackToBoxFormula_NotZero()
    {
        // Previously this fallback unconditionally returned 0 for any shape that wasn't exactly
        // 2 cylinders + 2 planes — clearly wrong (a 12-face part is not "zero surface area").
        var reader = StepP21Reader.ParseText(
            "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;");
        var bb = new[] { 0.01, 0.02, 0.03 };

        double sa = StepGeometryEstimator.EstimateSurfaceArea(reader, [], [], bb);

        double expected = 2 * (0.01 * 0.02 + 0.01 * 0.03 + 0.02 * 0.03);
        sa.Should().BeApproximately(expected, 1e-12);
        sa.Should().BeGreaterThan(0);
    }
}
