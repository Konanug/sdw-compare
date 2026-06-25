namespace SolidWorksPartMatcher.Domain.Models;

public sealed record PartFingerprint(
    Guid Id,
    Guid ScannedFileId,
    string FileSha256,
    string ConfigName,
    int ExtractorVersion,
    int SolidBodyCount,
    int SurfaceBodyCount,
    double[] SortedBoundingBoxM,
    double VolumeM3,
    double SurfaceAreaM2,
    double? MassKg,
    double[]? CenterOfMassM,
    int FaceCount,
    int EdgeCount,
    int VertexCount,
    int FeatureCount,
    IReadOnlyDictionary<string, int> FeatureTypeHistogram,
    string? Material,
    IReadOnlyDictionary<string, string> CustomProperties,
    string SolidWorksVersion,
    string ExtractorVersionLabel,
    DateTime ExtractedUtc,
    // Sign of det([principal inertia axis 1 | axis 2 | axis 3]).
    // +1 = right-handed geometry, -1 = left-handed (mirror).
    // Null when mass properties are unavailable.
    double? ChiralitySign,
    // CoM position as a fraction of each bounding-box dimension (0..1),
    // paired with and sorted by BB dimension size (smallest first).
    // For a mirrored part exactly one axis flips: ratio_A + ratio_B ≈ 1.0.
    // Null when mass properties or bounding box are unavailable.
    double[]? CoMOffsetInBB,
    // Number of features (any type) whose sketch contains ISketchText segments.
    // Non-zero on a part with text sketches (annotations, engravings, logos, etc.).
    int SketchTextCutCount,
    // Body geometry after suppressing all text-engraving CutExtrusion/WrapSketch features
    // and rebuilding. All fields are null when no qualifying features were found or
    // suppression failed. Used as the "base part" for engraving comparison.
    int? SuppressedSolidBodyCount,
    double[]? SuppressedBoundingBoxM,
    double? SuppressedVolumeM3,
    double? SuppressedSurfaceAreaM2,
    int? SuppressedFaceCount,
    int? SuppressedEdgeCount,
    int? SuppressedVertexCount);
