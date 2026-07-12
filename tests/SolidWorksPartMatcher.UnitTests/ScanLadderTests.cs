using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Orchestration;
using SolidWorksPartMatcher.Infrastructure.Step;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// The pure-geometry classification ladder (<see cref="ScanOrchestrationService.ClassifyGeometrySignals"/>):
/// the initial verdict, then Stage 3.5 (face signature), 3.6 (STEP evidence vote), 3.7 (STEP engraving).
///
/// Two of these tests pin real bugs. Stage 3.5 used to overwrite the verdict unconditionally, and a
/// face descriptor encodes surface type/axis/radius but NOT position — with the axis SIGN erased by
/// CanonicalizeAxis. So it destroyed the two verdicts that Stages 4 and 4.5 were carefully guarding
/// against: it turned an engraved pair into Distinct (invisible), and — worse — it turned a MIRROR
/// pair into a confirmed ExactGeometryMatch, which is a false automatic merge.
/// </summary>
public sealed class ScanLadderTests
{
    private static PartFingerprint Fp(
        string sha = "aaa",
        string sourceFormat = "STEP",
        List<string>? signature = null,
        int? faces = null,
        double vol = 1.0e-5,
        double area = 2.0e-3,
        double[]? bb = null,
        int edges = 100,
        int verts = 70,
        int sketchTextCuts = 0,
        string? geometrySource = "occt",
        double? suppressedVolume = null,
        int? suppressedFaces = null,
        double[]? suppressedBb = null,
        int? suppressedEdges = null,
        int? suppressedVerts = null)
    {
        signature ??= ["PLANE|0|0|1", "PLANE|1|0|0"];
        return new PartFingerprint(
            Id: Guid.NewGuid(),
            ScannedFileId: Guid.NewGuid(),
            FileSha256: sha,
            ConfigName: "Default",
            ExtractorVersion: 1,
            SolidBodyCount: 1,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: bb ?? [0.010, 0.050, 0.080],
            VolumeM3: vol,
            SurfaceAreaM2: area,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: faces ?? signature.Count,
            EdgeCount: edges,
            VertexCount: verts,
            FeatureCount: 0,
            FeatureTypeHistogram: new Dictionary<string, int>(),
            Material: null,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: string.Empty,
            ExtractorVersionLabel: "test",
            ExtractedUtc: DateTime.UtcNow,
            ChiralitySign: null,
            CoMOffsetInBB: null,
            SketchTextCutCount: sketchTextCuts,
            SuppressedSolidBodyCount: suppressedVolume.HasValue ? 1 : null,
            SuppressedBoundingBoxM: suppressedBb,
            SuppressedVolumeM3: suppressedVolume,
            SuppressedSurfaceAreaM2: null,
            SuppressedFaceCount: suppressedFaces,
            SuppressedEdgeCount: suppressedEdges,
            SuppressedVertexCount: suppressedVerts,
            SourceFormat: sourceFormat,
            FaceGeometricSignature: signature,
            GeometrySource: geometrySource);
    }

    private static ScanOrchestrationService.GeometryVerdict Classify(
        PartFingerprint a, PartFingerprint b, double score = 0.95,
        bool isBinaryDup = false, bool isMirror = false, string? mirrorReason = null)
        => ScanOrchestrationService.ClassifyGeometrySignals(
            a, b, score, "a.step", "b.step", isBinaryDup, isMirror, mirrorReason);

    // ── Bug B: the false automatic merge ─────────────────────────────────────────────────────────

    [Fact]
    public void Stage35_DoesNotTurnAMirrorIntoAConfirmedExactGeometryMatch()
    {
        // A part chiral only by hole PLACEMENT: descriptors encode no position, and CanonicalizeAxis
        // erases the axis sign, so a mirrored copy produces BYTE-IDENTICAL sorted descriptors.
        // Stage 3.5 therefore returned ExactGeometryMatch — and Stages 4/4.5, which exist precisely to
        // settle mirrors with a det(R) test, are gated !isMirror and never ran to correct it.
        // A left-handed and a right-handed bracket were presented as the same CONFIRMED part.
        List<string> identicalDescriptors =
            ["CYLINDER|0.003|0|0|1", "PLANE|0|0|1", "PLANE|0|1|0", "PLANE|1|0|0"];

        var left = Fp(sha: "L", sourceFormat: "SLDPRT", signature: identicalDescriptors);
        var right = Fp(sha: "R", sourceFormat: "SLDPRT", signature: identicalDescriptors);

        var verdict = Classify(left, right, score: 0.97,
            isMirror: true, mirrorReason: "Chirality sign differs (+1 vs -1)");

        verdict.Cls.Should().Be(PartClassification.MirrorOrHandedVariant,
            because: "a mirror must never be presented as a confirmed match — it is left for Stage 4 "
                   + "and for human review");
        verdict.Cls.Should().NotBe(PartClassification.ExactGeometryMatch);
        verdict.ComparatorVersion.Should().Be("coarse-1", because: "Stage 3.5 must not have run");
    }

    // ── Bug A: SLDPRT engraving detection destroyed one stage after it succeeded ──────────────────

    [Fact]
    public void Stage35_DoesNotClobberAnEngravingVariantEstablishedBySuppression()
    {
        // The suppression detector compares the two parts' BASE geometry (engraving features
        // suppressed) and concludes EngravingVariant. Stage 3.5 then compared the FULL signatures,
        // saw different face counts (that is what an engraving is), and overwrote the verdict with
        // Distinct — which is filtered out of the UI. The engraved pair disappeared.
        var plain = Fp(sha: "P", sourceFormat: "SLDPRT",
            signature: ["PLANE|0|0|1", "PLANE|1|0|0"],
            suppressedVolume: 1.0e-5, suppressedFaces: 2, suppressedBb: [0.010, 0.050, 0.080],
            suppressedEdges: 12, suppressedVerts: 8);

        var engraved = Fp(sha: "E", sourceFormat: "SLDPRT",
            signature: ["PLANE|0|0|1", "PLANE|0|0|1", "PLANE|0|1|0", "PLANE|1|0|0"],
            sketchTextCuts: 1,
            suppressedVolume: 1.0e-5, suppressedFaces: 2, suppressedBb: [0.010, 0.050, 0.080],
            suppressedEdges: 12, suppressedVerts: 8);

        var verdict = Classify(plain, engraved, score: 0.93);

        verdict.Cls.Should().Be(PartClassification.EngravingVariant,
            because: "the suppression detector proved the base geometry is identical; Stage 3.5 seeing "
                   + "a different FULL face count is the engraving, not evidence against it");
    }

    // ── Stage 3.7: the STEP engraving rescue ─────────────────────────────────────────────────────

    private static List<string> PlainSig()
    {
        var s = new List<string>
        {
            "PLANE|0|0|1", "PLANE|0|0|1", "PLANE|0|1|0", "PLANE|0|1|0", "PLANE|1|0|0", "PLANE|1|0|0",
            "CYLINDER|0.003|0|0|1", "CYLINDER|0.003|0|0|1",
        };
        s.Sort(StringComparer.Ordinal);
        return s;
    }

    private static List<string> EngravedSig()
    {
        var s = PlainSig();
        for (int i = 0; i < 60; i++)
            s.Add(i % 2 == 0 ? "PLANE|0|0|1" : "PLANE|0.7071|0.7071|0");
        s.Sort(StringComparer.Ordinal);
        return s;
    }

    [Fact]
    public void Stage37_RescuesAnEngravedStepPair_FromDistinct()
    {
        // Without Stage 3.7 this pair is Distinct (Stage 3.5: different face count) and Stage 3.6's
        // vote structurally cannot save it — three of its four flags are face-count-sensitive.
        var plain = Fp(sha: "P", signature: PlainSig(), vol: 1.0e-5, area: 2.0e-3, edges: 24, verts: 16);
        var engraved = Fp(sha: "E", signature: EngravedSig(), vol: 0.9998e-5, area: 2.016e-3,
            edges: 300, verts: 210);

        var verdict = Classify(plain, engraved, score: 0.85);

        verdict.Cls.Should().Be(PartClassification.EngravingVariant);
        verdict.ComparatorVersion.Should().Be("step-engraving-1");
        verdict.Engraving!.IsEngraving.Should().BeTrue();
    }

    [Fact]
    public void Stage37_NeverDowngradesABinaryDuplicate()
    {
        var a = Fp(sha: "same", signature: PlainSig());
        var b = Fp(sha: "same", signature: EngravedSig(), vol: 0.9998e-5);

        var verdict = Classify(a, b, score: 1.0, isBinaryDup: true);

        verdict.Cls.Should().Be(PartClassification.BinaryDuplicate);
    }

    [Fact]
    public void Stage37_NeverDowngradesAnExactGeometryMatch()
    {
        // Identical signatures → Stage 3.5 confirms ExactGeometryMatch. Stage 3.7's gate only admits
        // Distinct/PossibleMatch, so it must not touch this.
        var sig = PlainSig();
        var a = Fp(sha: "A", signature: sig);
        var b = Fp(sha: "B", signature: sig);

        var verdict = Classify(a, b, score: 0.99);

        verdict.Cls.Should().Be(PartClassification.ExactGeometryMatch);
        verdict.ComparatorVersion.Should().Be("face-sig-1");
    }

    [Fact]
    public void Stage37_DoesNotRunCrossFormat()
    {
        // SLDPRT has a real feature tree and its own suppression-based engraving check — strictly
        // better evidence than this geometric inference. Stage 3.7 is STEP-only.
        var sldprt = Fp(sha: "S", sourceFormat: "SLDPRT", signature: PlainSig(), vol: 1.0e-5, edges: 24, verts: 16);
        var step = Fp(sha: "T", signature: EngravedSig(), vol: 0.9998e-5, edges: 300, verts: 210);

        var verdict = Classify(sldprt, step, score: 0.85);

        verdict.Cls.Should().NotBe(PartClassification.EngravingVariant);
        verdict.Engraving.Should().BeNull(because: "the detector must not even be consulted");
    }

    [Fact]
    public void Stage37_DoesNotRescueAPairOnEstimatedGeometry()
    {
        // Without OCCT the volume and area are pure functions of the bounding box, so the size gates
        // would pass vacuously. The pair stays Distinct — the same as today's behaviour, which is the
        // correct failure direction: a missed engraving, never a false merge.
        var plain = Fp(sha: "P", signature: PlainSig(), geometrySource: "step-estimate",
            vol: 1.0e-5, edges: 24, verts: 16);
        var engraved = Fp(sha: "E", signature: EngravedSig(), geometrySource: "step-estimate",
            vol: 1.0e-5, edges: 300, verts: 210);

        var verdict = Classify(plain, engraved, score: 0.85);

        verdict.Cls.Should().NotBe(PartClassification.EngravingVariant);
        verdict.Engraving!.IsEngraving.Should().BeFalse();
    }

    [Fact]
    public void Stage37_DoesNotRunForAMirror()
    {
        var plain = Fp(sha: "P", signature: PlainSig(), vol: 1.0e-5, edges: 24, verts: 16);
        var engraved = Fp(sha: "E", signature: EngravedSig(), vol: 0.9998e-5, edges: 300, verts: 210);

        var verdict = Classify(plain, engraved, score: 0.85,
            isMirror: true, mirrorReason: "chirality differs");

        verdict.Cls.Should().Be(PartClassification.MirrorOrHandedVariant);
    }
}
