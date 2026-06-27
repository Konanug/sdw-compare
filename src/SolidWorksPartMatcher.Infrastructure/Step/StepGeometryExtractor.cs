using System.Globalization;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Extracts a <see cref="PartFingerprint"/> from a STEP AP214/AP242 file by parsing the P21
/// DATA section directly — no SolidWorks open required.
///
/// Inspired by the diff3d approach (github.com/bdlucas1/diff3d): represent geometry as a
/// canonical set of surface descriptors rather than relying on entity ordering or CAD system
/// APIs.  The <see cref="FaceGeometricSignature"/> (sorted list of face descriptor strings)
/// is the primary comparison key; the geometric properties (volume, bounding box, face counts)
/// feed the existing candidate blocker and coarse scorer.
/// </summary>
public sealed class StepGeometryExtractor(ILogger<StepGeometryExtractor> logger)
{
    // Bump when the face descriptor format changes so cached signatures are invalidated.
    public const string VersionLabel = "step-p21-1";
    public const int    Version      = 100;

    public PartFingerprint? Extract(ScannedFile file)
    {
        if (file.Sha256 is null)
        {
            logger.LogWarning("Cannot extract STEP fingerprint: file {File} has no SHA-256", file.FileName);
            return null;
        }

        StepP21Reader reader;
        try
        {
            reader = StepP21Reader.ParseFile(file.NormalizedPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "STEP P21 parse failed for {File}", file.FileName);
            return null;
        }

        var faces = reader.GetAdvancedFaces();
        if (faces.Count == 0)
        {
            logger.LogWarning("No ADVANCED_FACE entities found in {File} — not a solid-body STEP?", file.FileName);
            return null;
        }

        // Build face descriptors and face-type histogram
        var descriptors  = new List<string>(faces.Count);
        var faceTypeHist = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (surfId, _) in faces)
        {
            var type = reader.GetEntityType(surfId);
            if (type == null) continue;

            // Track face type counts (stored in FeatureTypeHistogram for blocking/scoring)
            faceTypeHist.TryGetValue(type, out int cnt);
            faceTypeHist[type] = cnt + 1;

            var descriptor = BuildFaceDescriptor(reader, surfId, type);
            descriptors.Add(descriptor);
        }

        descriptors.Sort(StringComparer.Ordinal);

        // Bounding box from all CARTESIAN_POINT coordinates
        var points = reader.GetAllCartesianPoints();
        var bb = ComputeSortedBoundingBox(points);

        // Volume estimate: analytical for pure cylinder, BB volume otherwise
        double volumeM3   = EstimateVolume(reader, faces, bb);
        double surfAreaM2 = EstimateSurfaceArea(reader, faces);

        int solidBodyCount = Math.Max(1, reader.GetManifoldSolidCount());

        logger.LogInformation(
            "STEP {File}: {Faces} faces, {Types} surface types, vol≈{Vol:E3}m³, sig[0]={Sig0}",
            file.FileName, faces.Count, faceTypeHist.Count, volumeM3,
            descriptors.Count > 0 ? descriptors[0] : "(none)");

        return new PartFingerprint(
            Id: Guid.NewGuid(),
            ScannedFileId: file.Id,
            FileSha256: file.Sha256,
            ConfigName: "Default",
            ExtractorVersion: Version,
            SolidBodyCount: solidBodyCount,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: bb,
            VolumeM3: volumeM3,
            SurfaceAreaM2: surfAreaM2,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: faces.Count,
            EdgeCount: 0,
            VertexCount: 0,
            FeatureCount: 0,
            FeatureTypeHistogram: faceTypeHist,   // face-type distribution for cross-STEP scoring
            Material: null,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: string.Empty,
            ExtractorVersionLabel: VersionLabel,
            ExtractedUtc: DateTime.UtcNow,
            ChiralitySign: null,
            CoMOffsetInBB: null,
            SketchTextCutCount: 0,
            SuppressedSolidBodyCount: null,
            SuppressedBoundingBoxM: null,
            SuppressedVolumeM3: null,
            SuppressedSurfaceAreaM2: null,
            SuppressedFaceCount: null,
            SuppressedEdgeCount: null,
            SuppressedVertexCount: null,
            SourceFormat: "STEP",
            FaceGeometricSignature: descriptors.AsReadOnly());
    }

    // ── Face descriptor ────────────────────────────────────────────────────

    private static string BuildFaceDescriptor(StepP21Reader reader, int surfId, string type)
    {
        return type switch
        {
            "CYLINDRICAL_SURFACE" => BuildCylinderDescriptor(reader, surfId),
            "PLANE"               => BuildPlaneDescriptor(reader, surfId),
            "CONICAL_SURFACE"     => BuildConeDescriptor(reader, surfId),
            "SPHERICAL_SURFACE"   => BuildSphereDescriptor(reader, surfId),
            "TOROIDAL_SURFACE"    => BuildTorusDescriptor(reader, surfId),
            _                     => $"OTHER|{type}"
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
    private static void CanonicalizeAxis(double[] axis)
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

    private static double[] ComputeSortedBoundingBox(IReadOnlyList<double[]> points)
    {
        if (points.Count == 0) return [0, 0, 0];

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        foreach (var p in points)
        {
            if (p.Length < 3) continue;
            if (p[0] < minX) minX = p[0];  if (p[0] > maxX) maxX = p[0];
            if (p[1] < minY) minY = p[1];  if (p[1] > maxY) maxY = p[1];
            if (p[2] < minZ) minZ = p[2];  if (p[2] > maxZ) maxZ = p[2];
        }

        var bb = new[] { maxX - minX, maxY - minY, maxZ - minZ };
        Array.Sort(bb);
        return bb;
    }

    /// <summary>
    /// Volume estimate. For a shape composed entirely of one cylinder + two planes (simple
    /// extrusion), computes π·r²·h analytically. Falls back to 55% of bounding-box volume
    /// (empirical average fill factor for machined parts) for other shapes.
    /// </summary>
    private static double EstimateVolume(
        StepP21Reader reader,
        IReadOnlyList<(int SurfaceId, bool Outward)> faces,
        double[] sortedBB)
    {
        var cylinders = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "CYLINDRICAL_SURFACE").ToList();
        var planes    = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "PLANE").ToList();

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
    /// Falls back to 3× bounding-box cross-section area for other shapes.
    /// </summary>
    private static double EstimateSurfaceArea(
        StepP21Reader reader,
        IReadOnlyList<(int SurfaceId, bool Outward)> faces)
    {
        var cylinders = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "CYLINDRICAL_SURFACE").ToList();
        var planes    = faces.Where(f => reader.GetEntityType(f.SurfaceId) == "PLANE").ToList();

        if (cylinders.Count >= 2 && planes.Count >= 2)
        {
            if (reader.TryGetCylinderParams(cylinders[0].SurfaceId, out double r, out _))
            {
                // Need height: get all CARTESIAN_POINT Z values along cylinder axis
                // Use max extent of all points as height approximation
                var pts = reader.GetAllCartesianPoints();
                double minZ = pts.Min(p => p[2]), maxZ = pts.Max(p => p[2]);
                double h = maxZ - minZ;
                return 2 * Math.PI * r * h + 2 * Math.PI * r * r;
            }
        }

        return 0; // unknown
    }
}
