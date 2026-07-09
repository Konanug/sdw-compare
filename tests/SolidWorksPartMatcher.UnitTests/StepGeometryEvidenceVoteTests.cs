using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class StepGeometryEvidenceVoteTests
{
    private static readonly StepMatchTolerances Tol = StepMatchTolerances.Default; // 5%, 1% radius, 95%, min 3

    // A cylinder + two planes (3 faces), typical simple STEP part.
    private static readonly string[] BaseSig =
    [
        "CYLINDER|0.005|0.0000|0.0000|1.0000",
        "PLANE|0.0000|0.0000|1.0000",
        "PLANE|0.0000|0.0000|1.0000",
    ];

    private static readonly Dictionary<string, int> BaseHist =
        new() { ["CYLINDRICAL_SURFACE"] = 1, ["PLANE"] = 2 };

    private static PartFingerprint Step(
        double volume,
        int faceCount = 3,
        Dictionary<string, int>? hist = null,
        string[]? sig = null) => new(
            Id: Guid.NewGuid(),
            ScannedFileId: Guid.NewGuid(),
            FileSha256: Guid.NewGuid().ToString("N"),
            ConfigName: "Default",
            ExtractorVersion: 101,
            SolidBodyCount: 1,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: [0.01, 0.01, 0.02],
            VolumeM3: volume,
            SurfaceAreaM2: 0.01,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: faceCount,
            EdgeCount: 0,
            VertexCount: 0,
            FeatureCount: 0,
            FeatureTypeHistogram: hist ?? new Dictionary<string, int>(BaseHist),
            Material: null,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: "",
            ExtractorVersionLabel: "step-p21-2",
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
            FaceGeometricSignature: (sig ?? BaseSig).OrderBy(s => s, StringComparer.Ordinal).ToList());

    [Fact]
    public void AllFourSignalsAgree_Escalates()
    {
        var a = Step(0.001);
        var b = Step(0.001); // identical

        var r = StepGeometryEvidenceVote.Evaluate(a, b, Tol);

        r.AgreeingFlags.Should().Be(4);
        r.Escalate.Should().BeTrue();
        r.Reason.Should().Contain("Under review");
    }

    [Fact]
    public void TinyRadiusDifferenceWithinTolerance_StillEscalates_ThisIsThePointFix()
    {
        // The exact `{r:R}` match fails on this, but the tolerant signature (1%) absorbs a 0.2%
        // radius difference — so a near-identical STEP part is now surfaced for review.
        var a = Step(0.001);
        var b = Step(0.001, sig:
        [
            "CYLINDER|0.005010|0.0000|0.0000|1.0000", // 0.2% larger radius
            "PLANE|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000",
        ]);

        var r = StepGeometryEvidenceVote.Evaluate(a, b, Tol);

        r.AgreeingFlags.Should().Be(4);
        r.Escalate.Should().BeTrue();
    }

    [Fact]
    public void VolumeOffButTopologyAndSignatureAgree_ThreeOfFour_Escalates()
    {
        var a = Step(0.001);
        var b = Step(0.002); // +100% volume → volume flag fails; other 3 agree

        var r = StepGeometryEvidenceVote.Evaluate(a, b, Tol);

        r.AgreeingFlags.Should().Be(3);
        r.Escalate.Should().BeTrue();
    }

    [Fact]
    public void SameVolumeSameTopologyDifferentRadii_Escalates_ForReviewOnly_NotAConfirmedMatch()
    {
        // The trap: two different cylinders can share volume + face count + face-type histogram.
        // Flag 4 (tolerant signature) fails on the very different radius, so it's 3/4 → surfaced for
        // REVIEW. It never becomes a confirmed/auto-merged match — that guarantee lives in the
        // orchestrator (result is always PossibleMatch) and is asserted by the vote returning a
        // plain Escalate, not any "exact" verdict.
        var a = Step(0.001);
        var b = Step(0.001, sig:
        [
            "CYLINDER|0.010|0.0000|0.0000|1.0000", // 2x radius — clearly different part
            "PLANE|0.0000|0.0000|1.0000",
            "PLANE|0.0000|0.0000|1.0000",
        ]);

        var r = StepGeometryEvidenceVote.Evaluate(a, b, Tol);

        r.AgreeingFlags.Should().Be(3); // volume + face count + histogram; signature fails
        r.Escalate.Should().BeTrue();
    }

    [Fact]
    public void OnlyTwoSignalsAgree_DoesNotEscalate()
    {
        // Different volume AND different topology (face count + histogram) — only the (coincidental)
        // signature bucket can't save it. Face count differs and histogram differs → 0-1 flags.
        var a = Step(0.001, faceCount: 3);
        var b = Step(0.005, faceCount: 8,
            hist: new Dictionary<string, int> { ["PLANE"] = 6, ["CONICAL_SURFACE"] = 2 },
            sig:
            [
                "CONE|0.100000|0.020|0.0000|0.0000|1.0000",
                "PLANE|1.0000|0.0000|0.0000",
            ]);

        var r = StepGeometryEvidenceVote.Evaluate(a, b, Tol);

        r.AgreeingFlags.Should().BeLessThan(3);
        r.Escalate.Should().BeFalse();
    }

    [Fact]
    public void MissingSignature_DoesNotCountThatFlag()
    {
        var a = Step(0.001) with { FaceGeometricSignature = null };
        var b = Step(0.001);

        var r = StepGeometryEvidenceVote.Evaluate(a, b, Tol);

        // Volume + face count + histogram agree (3); signature can't be evaluated → not counted.
        r.AgreeingFlags.Should().Be(3);
        r.Escalate.Should().BeTrue();
    }
}
