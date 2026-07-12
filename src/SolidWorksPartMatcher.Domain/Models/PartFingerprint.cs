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
    int? SuppressedVertexCount,
    // "SLDPRT" | "STEP" — determines comparison routing and feature availability.
    string SourceFormat = "SLDPRT",
    // Sorted list of canonical face descriptors. Populated for BOTH formats — STEP from the P21
    // geometry (StepGeometryExtractor), SLDPRT from the SW COM face enumeration — in the same
    // grammar, so the two are directly comparable. Two fingerprints with identical sorted signatures
    // have the same B-Rep surface types/axes/radii (but NOT necessarily the same surface positions).
    IReadOnlyList<string>? FaceGeometricSignature = null,
    // How VolumeM3 / SurfaceAreaM2 / SortedBoundingBoxM were obtained, for STEP:
    //   "occt"          — measured by the CAD kernel. Trustworthy for fine-grained deltas.
    //   "step-estimate" — derived from the raw P21 point cloud (OCCT unavailable). NOT trustworthy
    //                     for fine-grained deltas: StepGeometryEstimator's volume (0.55 × bbVolume)
    //                     and surface area (the box formula) are PURE FUNCTIONS OF THE BOUNDING BOX,
    //                     so two different parts sharing a box get bit-identical volume AND area.
    //                     Any comparison that gates on "same box + same volume + same area" would
    //                     pass vacuously — see StepEngravingDetector, which refuses to run on these.
    //   null            — SLDPRT (always kernel-measured by SolidWorks), or a pre-v9 cached row.
    //
    // Deliberately nullable with a trailing default so the SLDPRT extractor needs no change and its
    // version stays at sw2024-real-8 — bumping it would force a full SolidWorks re-open of every
    // SLDPRT file for a field SLDPRT does not use. Do not "tidy" this into a required parameter.
    string? GeometrySource = null);
