namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Geometry-estimation helpers shared between whole-file STEP part extraction
/// (<see cref="StepGeometryExtractor"/>) and per-component assembly extraction
/// (<see cref="Assembly.StepAssemblyStructureReader"/>). Callers pass an already-scoped
/// face/point list — the whole file for a single-part extraction, or a closure-filtered
/// subset for one assembly component — so this class has no notion of "the whole file".
/// </summary>
internal static class StepGeometryEstimator
{
    // ── Face descriptor ────────────────────────────────────────────────────

    public static string BuildFaceDescriptor(StepP21Reader reader, int surfId, string type)
    {
        return type switch
        {
            "CYLINDRICAL_SURFACE" => BuildCylinderDescriptor(reader, surfId),
            "PLANE" => BuildPlaneDescriptor(reader, surfId),
            "CONICAL_SURFACE" => BuildConeDescriptor(reader, surfId),
            "SPHERICAL_SURFACE" => BuildSphereDescriptor(reader, surfId),
            "TOROIDAL_SURFACE" => BuildTorusDescriptor(reader, surfId),
            _ => $"OTHER|{type}"
        };
    }

    private static string BuildCylinderDescriptor(StepP21Reader reader, int surfId)
    {
        if (!reader.TryGetCylinderParams(surfId, out double r, out var axis))
            return "CYLINDER|PARSE_ERROR";
        CanonicalizeAxis(axis);
        return $"CYLINDER|{r:R}|{axis[0]:F4}|{axis[1]:F4}|{axis[2]:F4}";
    }

    private static string BuildPlaneDescriptor(StepP21Reader reader, int surfId)
    {
        if (!reader.TryGetPlaneNormal(surfId, out var n))
            return "PLANE|PARSE_ERROR";
        CanonicalizeAxis(n);
        return $"PLANE|{n[0]:F4}|{n[1]:F4}|{n[2]:F4}";
    }

    private static string BuildConeDescriptor(StepP21Reader reader, int surfId)
    {
        if (!reader.TryGetConeParams(surfId, out double ha, out double r, out var axis))
            return "CONE|PARSE_ERROR";
        CanonicalizeAxis(axis);
        return $"CONE|{ha:F6}|{r:R}|{axis[0]:F4}|{axis[1]:F4}|{axis[2]:F4}";
    }

    private static string BuildSphereDescriptor(StepP21Reader reader, int surfId)
    {
        if (!reader.TryGetSphereRadius(surfId, out double r))
            return "SPHERE|PARSE_ERROR";
        return $"SPHERE|{r:R}";
    }

    private static string BuildTorusDescriptor(StepP21Reader reader, int surfId)
    {
        if (!reader.TryGetTorusParams(surfId, out double R, out double r))
            return "TORUS|PARSE_ERROR";
        return $"TORUS|{R:R}|{r:R}";
    }

    // Normalizes direction vector so the dominant component is positive.
    // Ensures two identical axes pointing in opposite directions produce the same descriptor.
    public static void CanonicalizeAxis(double[] axis)
    {
        if (axis.Length < 3) return;
        int dom = 0;
        for (int i = 1; i < 3; i++)
            if (Math.Abs(axis[i]) > Math.Abs(axis[dom])) dom = i;

        if (axis[dom] < 0)
            for (int i = 0; i < axis.Length; i++) axis[i] = -axis[i];

        // Suppress near-zero floating-point noise (treat −0 as 0)
        for (int i = 0; i < axis.Length; i++)
            if (Math.Abs(axis[i]) < 1e-9) axis[i] = 0.0;
    }

    // ── Geometric property estimation ──────────────────────────────────────

    public static double[] ComputeSortedBoundingBox(IReadOnlyList<double[]> points)
    {
        if (points.Count == 0) return [0, 0, 0];

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        foreach (var p in points)
        {
            if (p.Length < 3) continue;
            if (p[0] < minX) minX = p[0]; if (p[0] > maxX) maxX = p[0];
            if (p[1] < minY) minY = p[1]; if (p[1] > maxY) maxY = p[1];
            if (p[2] < minZ) minZ = p[2]; if (p[2] > maxZ) maxZ = p[2];
        }

        var bb = new[] { maxX - minX, maxY - minY, maxZ - minZ };
        Array.Sort(bb);
        return bb;
    }

    /// <summary>
    /// Re-expresses a point cloud in its own principal-axis frame (via eigen-decomposition of the
    /// point cloud's covariance matrix — a standard PCA/orientation-normalization technique),
    /// centered at the centroid and with axes ordered by ascending spread. Rotation-invariant by
    /// construction: rotating the entire input by any rotation matrix R transforms the covariance
    /// matrix as R·C·Rᵀ, whose eigenvectors become R·v_i with unchanged eigenvalues, so projecting
    /// the rotated points onto the rotated eigenvectors reproduces exactly the same ranges as the
    /// original — i.e. <see cref="ComputeSortedBoundingBox"/> called on the result is unaffected by
    /// whatever arbitrary rotation the STEP file's own local coordinate frame happened to use.
    ///
    /// This matters specifically for assembly components: unlike a standalone part file, an
    /// embedded assembly PRODUCT's raw geometry coordinates are not guaranteed to share a common
    /// canonical orientation between two assembly-file revisions (the same physical part can be
    /// authored, or simply referenced, at an arbitrary rotation) — without this step, a genuinely
    /// unchanged part can appear to have a wildly different (and physically nonsensical, e.g. a
    /// bounding box far larger than the part's own true size) bounding box purely because of how it
    /// happened to be oriented in the file, which previously caused real "same part, different
    /// orientation" cases to be misclassified as SuspiciousMatch/Modified.
    /// </summary>
    public static IReadOnlyList<double[]> AlignToPrincipalAxes(IReadOnlyList<double[]> points)
    {
        if (points.Count < 2) return points;

        double cx = 0, cy = 0, cz = 0;
        int n = 0;
        foreach (var p in points)
        {
            if (p.Length < 3) continue;
            cx += p[0]; cy += p[1]; cz += p[2]; n++;
        }
        if (n < 2) return points;
        cx /= n; cy /= n; cz /= n;

        double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        foreach (var p in points)
        {
            if (p.Length < 3) continue;
            double dx = p[0] - cx, dy = p[1] - cy, dz = p[2] - cz;
            xx += dx * dx; yy += dy * dy; zz += dz * dz;
            xy += dx * dy; xz += dx * dz; yz += dy * dz;
        }

        var (e0, e1, e2) = JacobiEigenvectors3x3(xx, yy, zz, xy, xz, yz);
        var axes = new[] { e0, e1, e2 };

        double minA = double.MaxValue, maxA = double.MinValue;
        double minB = double.MaxValue, maxB = double.MinValue;
        double minC = double.MaxValue, maxC = double.MinValue;

        var projected = new List<double[]>(points.Count);
        foreach (var p in points)
        {
            if (p.Length < 3) { projected.Add([0, 0, 0]); continue; }
            double dx = p[0] - cx, dy = p[1] - cy, dz = p[2] - cz;
            double a = dx * axes[0][0] + dy * axes[0][1] + dz * axes[0][2];
            double b = dx * axes[1][0] + dy * axes[1][1] + dz * axes[1][2];
            double c = dx * axes[2][0] + dy * axes[2][1] + dz * axes[2][2];
            projected.Add([a, b, c]);
            if (a < minA) minA = a; if (a > maxA) maxA = a;
            if (b < minB) minB = b; if (b > maxB) maxB = b;
            if (c < minC) minC = c; if (c > maxC) maxC = c;
        }

        // Sort axes by ascending spread so index 2 is always the longest extent — matches
        // ComputeSortedBoundingBox's own ascending convention and preserves EstimateVolume/
        // EstimateSurfaceArea's existing assumption that the "tall" axis is the last one,
        // regardless of how the STEP file's own raw axes happened to be laid out.
        var ranges = new[] { maxA - minA, maxB - minB, maxC - minC };
        var order = new[] { 0, 1, 2 }.OrderBy(i => ranges[i]).ToArray();

        var result = new List<double[]>(projected.Count);
        foreach (var p in projected)
            result.Add([p[order[0]], p[order[1]], p[order[2]]]);
        return result;
    }

    // Classic cyclic Jacobi eigenvalue algorithm specialized for a 3x3 symmetric matrix — small,
    // fixed-size, deterministic, and converges in a handful of sweeps; avoids pulling in a linear
    // algebra dependency for what's otherwise a three-line diagonalization.
    private static (double[] E0, double[] E1, double[] E2) JacobiEigenvectors3x3(
        double axx, double ayy, double azz, double axy, double axz, double ayz)
    {
        var a = new double[3, 3] { { axx, axy, axz }, { axy, ayy, ayz }, { axz, ayz, azz } };
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-14) break;

            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    double apq = a[p, q];
                    if (Math.Abs(apq) < 1e-15) continue;

                    double app = a[p, p], aqq = a[q, q];
                    double theta = (aqq - app) / (2 * apq);
                    double t = (theta >= 0 ? 1.0 : -1.0) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1);
                    double s = t * c;

                    a[p, p] = app - t * apq;
                    a[q, q] = aqq + t * apq;
                    a[p, q] = 0; a[q, p] = 0;

                    for (int k = 0; k < 3; k++)
                    {
                        if (k == p || k == q) continue;
                        double akp = a[k, p], akq = a[k, q];
                        double newAkp = c * akp - s * akq;
                        double newAkq = s * akp + c * akq;
                        a[k, p] = newAkp; a[p, k] = newAkp;
                        a[k, q] = newAkq; a[q, k] = newAkq;
                    }

                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }

        return (
            [v[0, 0], v[1, 0], v[2, 0]],
            [v[0, 1], v[1, 1], v[2, 1]],
            [v[0, 2], v[1, 2], v[2, 2]]);
    }

    /// <summary>
    /// Volume estimate. For a shape composed entirely of one cylinder + two planes (simple
    /// extrusion), computes π·r²·h analytically. Falls back to 55% of bounding-box volume
    /// (empirical average fill factor for machined parts) for other shapes.
    /// </summary>
    public static double EstimateVolume(
        StepP21Reader reader,
        IReadOnlyList<(int SurfaceId, bool Outward)> faces,
        double[] sortedBB)
    {
        var cylinders = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "CYLINDRICAL_SURFACE").ToList();
        var planes = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "PLANE").ToList();

        // Simple extruded cylinder: exactly 1 unique cylinder radius + 2 plane caps
        if (cylinders.Count == 2 && planes.Count == 2)
        {
            if (reader.TryGetCylinderParams(cylinders[0].SurfaceId, out double r, out _))
            {
                // Height is the largest BB dimension for an axis-aligned cylinder
                double h = sortedBB[2]; // largest extent
                return Math.PI * r * r * h;
            }
        }

        // Fallback: use 55% of bounding-box volume
        double bbVol = sortedBB[0] * sortedBB[1] * sortedBB[2];
        return bbVol * 0.55;
    }

    /// <summary>
    /// Surface area estimate. For a simple cylinder, computes 2πrh + 2πr² analytically.
    /// Falls back to a rectangular-box surface-area formula (2×(lw+lh+wh)) over
    /// <paramref name="sortedBB"/> for other shapes — mirrors <see cref="EstimateVolume"/>'s
    /// bounding-box-derived fallback rather than returning an unconditional (and simply wrong)
    /// zero for anything that isn't a two-cylinder-two-plane shape.
    /// <paramref name="points"/> must already be scoped to the same face set as
    /// <paramref name="faces"/> — callers must not pass whole-file points for a
    /// per-component estimate.
    /// </summary>
    public static double EstimateSurfaceArea(
        StepP21Reader reader,
        IReadOnlyList<(int SurfaceId, bool Outward)> faces,
        IReadOnlyList<double[]> points,
        double[] sortedBB)
    {
        var cylinders = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "CYLINDRICAL_SURFACE").ToList();
        var planes = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "PLANE").ToList();

        if (cylinders.Count >= 2 && planes.Count >= 2 && points.Count > 0)
        {
            if (reader.TryGetCylinderParams(cylinders[0].SurfaceId, out double r, out _))
            {
                // Need height: get all CARTESIAN_POINT Z values along cylinder axis
                // Use max extent of all points as height approximation
                double minZ = points.Min(p => p[2]), maxZ = points.Max(p => p[2]);
                double h = maxZ - minZ;
                return 2 * Math.PI * r * h + 2 * Math.PI * r * r;
            }
        }

        double l = sortedBB.Length > 0 ? sortedBB[0] : 0;
        double w = sortedBB.Length > 1 ? sortedBB[1] : 0;
        double hgt = sortedBB.Length > 2 ? sortedBB[2] : 0;
        return 2 * (l * w + l * hgt + w * hgt);
    }
}
