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

            var descriptor = StepGeometryEstimator.BuildFaceDescriptor(reader, surfId, type);
            descriptors.Add(descriptor);
        }

        descriptors.Sort(StringComparer.Ordinal);

        // Bounding box from all CARTESIAN_POINT coordinates
        var points = reader.GetAllCartesianPoints();
        var bb = StepGeometryEstimator.ComputeSortedBoundingBox(points);

        // Volume estimate: analytical for pure cylinder, BB volume otherwise
        double volumeM3   = StepGeometryEstimator.EstimateVolume(reader, faces, bb);
        double surfAreaM2 = StepGeometryEstimator.EstimateSurfaceArea(reader, faces, points, bb);

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

}
