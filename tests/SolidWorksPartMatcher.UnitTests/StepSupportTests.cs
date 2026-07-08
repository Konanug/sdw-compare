using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Orchestration;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class StepSupportTests
{
    // ── Fixture helpers ──────────────────────────────────────────────────────

    private static PartFingerprint MakeFp(
        string sourceFormat = "SLDPRT",
        Dictionary<string, int>? featureHist = null,
        double vol = 0.001, double sa = 0.05,
        int faces = 20, int edges = 30, int verts = 15,
        IReadOnlyList<string>? faceSig = null) => new(
            Id: Guid.NewGuid(),
            ScannedFileId: Guid.NewGuid(),
            FileSha256: "deadbeef",
            ConfigName: "Default",
            ExtractorVersion: sourceFormat == "STEP" ? 100 : 7,
            SolidBodyCount: 1,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: [0.05, 0.10, 0.20],
            VolumeM3: vol,
            SurfaceAreaM2: sa,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: faces,
            EdgeCount: edges,
            VertexCount: verts,
            FeatureCount: featureHist?.Values.Sum() ?? 0,
            FeatureTypeHistogram: featureHist ?? new Dictionary<string, int>(),
            Material: null,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: "2024",
            ExtractorVersionLabel: sourceFormat == "STEP" ? "step-p21-1" : "sw2024-real-7",
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
            SourceFormat: sourceFormat,
            FaceGeometricSignature: faceSig);

    // ── STEP header regex ────────────────────────────────────────────────────

    [Theory]
    [InlineData("#1=PRODUCT('PART-001','Bracket Assembly','',());", "Bracket Assembly")]
    [InlineData("#42=PRODUCT('id','','description',());", "id")]
    [InlineData("#123=PRODUCT('bolt','M8 Hex Bolt','DIN 933',());", "M8 Hex Bolt")]
    public void StepProductRegex_ExtractsPartName(string line, string expected)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"#\d+=PRODUCT\('([^']*)','([^']*)','([^']*)'");
        var m = rx.Match(line);
        m.Success.Should().BeTrue();
        var name = m.Groups[2].Value.Trim();
        var result = string.IsNullOrEmpty(name) ? m.Groups[1].Value.Trim() : name;
        result.Should().Be(expected);
    }

    // ── CompareStepFaceSignatures ────────────────────────────────────────────

    [Fact]
    public void CompareStepFaceSignatures_IdenticalSignatures_ReturnsExactMatch()
    {
        var sig = new List<string>
        {
            "CYLINDER|7.142857142857143E-05|0.0000|0.0000|1.0000",
            "CYLINDER|7.142857142857143E-05|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000"
        };

        var fpA = MakeFp(sourceFormat: "STEP", faceSig: sig, faces: 4);
        var fpB = MakeFp(sourceFormat: "STEP", faceSig: new List<string>(sig), faces: 4);

        var (cls, reason) = ScanOrchestrationService.CompareStepFaceSignatures(fpA, fpB, 0.95);

        cls.Should().Be(PartClassification.ExactGeometryMatch);
        reason.Should().Contain("match exactly");
        reason.Should().Contain("4 faces");
    }

    [Fact]
    public void CompareStepFaceSignatures_DifferentRadius_ReturnsPossibleMatch()
    {
        var sigA = new List<string>
        {
            "CYLINDER|7.142857142857143E-05|0.0000|0.0000|1.0000",
            "CYLINDER|7.142857142857143E-05|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000"
        };
        var sigB = new List<string>
        {
            "CYLINDER|7E-05|0.0000|0.0000|1.0000",   // different radius
            "CYLINDER|7E-05|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000"
        };

        var fpA = MakeFp(sourceFormat: "STEP", faceSig: sigA, faces: 4);
        var fpB = MakeFp(sourceFormat: "STEP", faceSig: sigB, faces: 4);

        var (cls, reason) = ScanOrchestrationService.CompareStepFaceSignatures(fpA, fpB, 0.90);

        cls.Should().Be(PartClassification.PossibleMatch,
            "same face count but differing parameters should be flagged for review, not auto-merged");
        reason.Should().Contain("differ");
    }

    [Fact]
    public void CompareStepFaceSignatures_DifferentFaceCount_ReturnsDistinct()
    {
        var sigA = new List<string> { "CYLINDER|7E-05|0.0000|0.0000|1.0000" };
        var sigB = new List<string>
        {
            "CYLINDER|7E-05|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000"
        };

        var fpA = MakeFp(sourceFormat: "STEP", faceSig: sigA, faces: 1);
        var fpB = MakeFp(sourceFormat: "STEP", faceSig: sigB, faces: 2);

        var (cls, reason) = ScanOrchestrationService.CompareStepFaceSignatures(fpA, fpB, 0.80);

        cls.Should().Be(PartClassification.Distinct,
            "different face count means different topology — must not auto-merge");
        reason.Should().Contain("face count");
    }

    [Fact]
    public void CompareStepFaceSignatures_EmptySignatures_ReturnsExactMatch()
    {
        // Two STEP files where no faces were extracted (e.g. unknown surface types)
        // should be treated as matching each other (degenerate case).
        var fpA = MakeFp(sourceFormat: "STEP", faceSig: new List<string>(), faces: 0);
        var fpB = MakeFp(sourceFormat: "STEP", faceSig: new List<string>(), faces: 0);

        var (cls, _) = ScanOrchestrationService.CompareStepFaceSignatures(fpA, fpB, 0.85);

        cls.Should().Be(PartClassification.ExactGeometryMatch);
    }

    // ── Default scoring weights sum ──────────────────────────────────────────

    [Fact]
    public void DefaultScoringWeights_SumToOne()
    {
        var w = ScoringWeights.Default;
        var sum = w.BoundingBox + w.Volume + w.SurfaceArea + w.Topology
                + w.FeatureHistogram + w.MaterialProperties
                + w.CustomProperties + w.FilenameTokens;
        sum.Should().BeApproximately(1.0, 0.001,
            "scoring weights must sum to 1.0 so the total score remains in [0,1]");
    }
}
