using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// The blocker's <c>vol=</c> token used to be an exact quantized equality in EVERY bucket key — the
/// 26-neighbour loop offsets only the bounding box, never the volume. That meant two parts whose
/// volumes straddle a quantum boundary were never even generated as a candidate pair, so no amount
/// of downstream classification could ever see them.
///
/// This is not a corner case for an engraved pair. The quantum is 1e-7 m³ = 100 mm³, while a text
/// engraving removes roughly 5–100 mm³ — so the two volumes land in different quanta often, and for a
/// deep or large engraving they are guaranteed to.
/// </summary>
public sealed class BlockerVolumeNeighbourTests
{
    private const double VolQuantumM3 = 1e-7; // must match BucketCandidateBlocker

    private static PartFingerprint Fp(double vol, double[]? bb = null) => new(
        Id: Guid.NewGuid(),
        ScannedFileId: Guid.NewGuid(),
        FileSha256: Guid.NewGuid().ToString("N"),
        ConfigName: "Default",
        ExtractorVersion: 102,
        SolidBodyCount: 1,
        SurfaceBodyCount: 0,
        SortedBoundingBoxM: bb ?? [0.010, 0.050, 0.080],
        VolumeM3: vol,
        SurfaceAreaM2: 2.0e-3,
        MassKg: null,
        CenterOfMassM: null,
        FaceCount: 20,
        EdgeCount: 60,
        VertexCount: 40,
        FeatureCount: 0,
        FeatureTypeHistogram: new Dictionary<string, int>(),
        Material: null,
        CustomProperties: new Dictionary<string, string>(),
        SolidWorksVersion: string.Empty,
        ExtractorVersionLabel: "step-p21-3",
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
        FaceGeometricSignature: ["PLANE|0|0|1"],
        GeometrySource: "occt");

    [Fact]
    public void IdenticalBox_VolumesStraddlingAQuantumBoundary_StillGeneratesACandidate()
    {
        // Quantize() rounds v/1e-7, so 1.49 and 1.51 quanta round to 1 and 2 — different vol= tokens.
        // An engraving removing ~2 mm³ around that boundary is exactly this case.
        var plain = Fp(vol: 1.49 * VolQuantumM3);
        var engraved = Fp(vol: 1.51 * VolQuantumM3);

        var candidates = new BucketCandidateBlocker().GenerateCandidates([plain, engraved]);

        candidates.Should().HaveCount(1, because: "an engraved pair must reach the classifier at all");
    }

    [Fact]
    public void IdenticalBox_VolumesTwoQuantaApart_StillGeneratesACandidate()
    {
        // ±1 from BOTH sides gives a 2-quantum (200 mm³) reach — enough for any realistic engraving.
        var plain = Fp(vol: 10.0 * VolQuantumM3);
        var engraved = Fp(vol: 12.0 * VolQuantumM3);

        new BucketCandidateBlocker().GenerateCandidates([plain, engraved]).Should().HaveCount(1);
    }

    [Fact]
    public void IdenticalBox_VolumesFarApart_DoesNotGenerateACandidate()
    {
        // Proves the fix did NOT degenerate into a bounding-box-only bucket, which would have
        // collapsed a library of identically-sized plates with different cut-outs into one bucket.
        var thin = Fp(vol: 10.0 * VolQuantumM3);
        var solid = Fp(vol: 90.0 * VolQuantumM3);

        new BucketCandidateBlocker().GenerateCandidates([thin, solid]).Should().BeEmpty();
    }
}
