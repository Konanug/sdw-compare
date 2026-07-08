using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Orchestration;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class MirrorDetectionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PartFingerprint MakeFp(
        string sha = "abc",
        double? chiralitySign = null,
        Dictionary<string, int>? featureHist = null,
        double[]? comOffsetInBB = null) => new(
            Id: Guid.NewGuid(),
            ScannedFileId: Guid.NewGuid(),
            FileSha256: sha,
            ConfigName: "Default",
            ExtractorVersion: 2,
            SolidBodyCount: 1,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: [0.05, 0.10, 0.20],
            VolumeM3: 0.001,
            SurfaceAreaM2: 0.05,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: 20,
            EdgeCount: 30,
            VertexCount: 15,
            FeatureCount: 3,
            FeatureTypeHistogram: featureHist ?? new Dictionary<string, int> { ["Extrude"] = 2, ["Fillet"] = 1 },
            Material: "Steel",
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: "2024",
            ExtractorVersionLabel: "test-2",
            ExtractedUtc: DateTime.UtcNow,
            ChiralitySign: chiralitySign,
            CoMOffsetInBB: comOffsetInBB,
            SketchTextCutCount: 0,
            SuppressedSolidBodyCount: null,
            SuppressedBoundingBoxM: null,
            SuppressedVolumeM3: null,
            SuppressedSurfaceAreaM2: null,
            SuppressedFaceCount: null,
            SuppressedEdgeCount: null,
            SuppressedVertexCount: null);

    // ── Chirality-based detection (definitive path) ───────────────────────

    [Fact]
    public void OppositChiralitySigns_IsMirrorPair()
    {
        var original = MakeFp(sha: "aaa", chiralitySign: +1.0);
        var mirrored = MakeFp(sha: "bbb", chiralitySign: -1.0);

        var (isMirror, reason) = ScanOrchestrationService.ClassifyMirror(original, mirrored, score: 0.97);

        isMirror.Should().BeTrue();
        reason.Should().ContainEquivalentOf("chirality");
    }

    [Fact]
    public void SameChiralitySign_IsNotMirrorPair()
    {
        var a = MakeFp(sha: "aaa", chiralitySign: +1.0);
        var b = MakeFp(sha: "bbb", chiralitySign: +1.0);

        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.97);

        isMirror.Should().BeFalse();
    }

    [Fact]
    public void ZeroChiralitySign_TreatedAsUnavailable_FallsBackToHistogram()
    {
        // Zero determinant is degenerate — treated as null; falls back to heuristic.
        var original = MakeFp(sha: "aaa", chiralitySign: 0.0);
        var mirrored = MakeFp(sha: "bbb", chiralitySign: 0.0,
            featureHist: new Dictionary<string, int> { ["MirrorSolid"] = 1 });

        // Falls back to histogram: mirrored has MirrorSolid, original does not.
        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(original, mirrored, score: 0.95);

        isMirror.Should().BeTrue();
    }

    // ── Histogram-based detection (heuristic fallback) ────────────────────

    [Fact]
    public void OnePartHasMirrorFeature_OtherDoesNot_AboveThreshold_IsMirrorPair()
    {
        var original = MakeFp(sha: "aaa");
        var mirrored = MakeFp(sha: "bbb",
            featureHist: new Dictionary<string, int> { ["Extrude"] = 2, ["Fillet"] = 1, ["MirrorSolid"] = 1 });

        var (isMirror, reason) = ScanOrchestrationService.ClassifyMirror(original, mirrored, score: 0.95);

        isMirror.Should().BeTrue();
        reason.Should().ContainEquivalentOf("histogram");
    }

    [Fact]
    public void OnePartHasMirrorFeature_BelowScoreThreshold_IsNotMirrorPair()
    {
        var original = MakeFp(sha: "aaa");
        var mirrored = MakeFp(sha: "bbb",
            featureHist: new Dictionary<string, int> { ["MirrorSolid"] = 1 });

        // Score below the 0.70 heuristic threshold — insufficient evidence.
        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(original, mirrored, score: 0.50);

        isMirror.Should().BeFalse();
    }

    [Fact]
    public void BothPartsHaveMirrorFeature_ChiralityUnavailable_IsNotFlaggedAsMirror()
    {
        var hist = new Dictionary<string, int> { ["MirrorSolid"] = 1, ["Extrude"] = 2 };
        var a = MakeFp(sha: "aaa", featureHist: hist);
        var b = MakeFp(sha: "bbb", featureHist: hist);

        // Neither "mirrorA != mirrorB" nor chirality fires when both have mirror features.
        // Result should not be mirror-classified; let caller decide (PossibleMatch/ExactGeometryMatch).
        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.97);

        isMirror.Should().BeFalse();
    }

    [Fact]
    public void NeitherPartHasMirrorFeature_NoChirality_IsNotMirrorPair()
    {
        var a = MakeFp(sha: "aaa");
        var b = MakeFp(sha: "bbb");

        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.97);

        isMirror.Should().BeFalse();
    }

    // ── Chirality takes priority over histogram ───────────────────────────

    [Fact]
    public void OppositChirality_EvenWhenBothHaveMirrorFeature_IsMirrorPair()
    {
        var hist = new Dictionary<string, int> { ["MirrorSolid"] = 1 };
        var a = MakeFp(sha: "aaa", chiralitySign: +1.0, featureHist: hist);
        var b = MakeFp(sha: "bbb", chiralitySign: -1.0, featureHist: hist);

        var (isMirror, reason) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.95);

        isMirror.Should().BeTrue();
        reason.Should().ContainEquivalentOf("chirality");
    }

    // ── HasMirrorFeature helper ───────────────────────────────────────────

    [Theory]
    [InlineData("MirrorSolid")]
    [InlineData("Mirror")]
    [InlineData("BodyMirror")]
    [InlineData("mirrorsolid")]      // case-insensitive
    [InlineData("MirrorPattern")]    // substring variant — SW mirror-pattern feature
    [InlineData("MirrorComponent")]  // substring variant
    [InlineData("ImportedMirror")]   // substring variant — derived/imported mirror
    public void HasMirrorFeature_RecognisesKnownTypes(string featureType)
    {
        var hist = new Dictionary<string, int> { [featureType] = 1 };
        ScanOrchestrationService.HasMirrorFeature(hist).Should().BeTrue();
    }

    [Fact]
    public void HasMirrorFeature_DoesNotFire_ForOrdinaryFeatures()
    {
        // None of these contain the word "mirror", so the broadened substring check stays silent.
        var hist = new Dictionary<string, int> { ["Extrude"] = 2, ["Fillet"] = 3, ["Cut"] = 1 };
        ScanOrchestrationService.HasMirrorFeature(hist).Should().BeFalse();
    }

    // ── CoM-in-BB offset mirror detection ────────────────────────────────────

    [Fact]
    public void CoMOffset_ReflectedOnOneAxis_IsMirrorPair()
    {
        // Part A: CoM sits at 30% of smallest dim, 50% of mid, 60% of largest.
        // Part B: mirror across smallest dim → 70% on that axis (0.3+0.7=1.0).
        var a = MakeFp(sha: "aaa", comOffsetInBB: [0.30, 0.50, 0.60]);
        var b = MakeFp(sha: "bbb", comOffsetInBB: [0.70, 0.50, 0.60]);

        var (isMirror, reason) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.93);

        isMirror.Should().BeTrue();
        reason.Should().ContainEquivalentOf("CoM");
    }

    [Fact]
    public void CoMOffset_AllAxesMatch_IsNotMirrorPair()
    {
        // Identical CoM ratios → same part, not a mirror.
        var a = MakeFp(sha: "aaa", comOffsetInBB: [0.30, 0.50, 0.60]);
        var b = MakeFp(sha: "bbb", comOffsetInBB: [0.30, 0.50, 0.60]);

        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.93);

        isMirror.Should().BeFalse();
    }

    [Fact]
    public void CoMOffset_SymmetricOnMirrorAxis_NotFlagged()
    {
        // Axis 0: 0.51+0.49=1.0 but |0.51-0.5|=0.01 < 0.03 threshold → skipped as too symmetric.
        // Axis 1: 0.50+0.50=1.0 but also too symmetric → skipped.
        // Axis 2: 0.60+0.60=1.2 → does not sum to 1 → no match.
        // No reliable mirror axis → must NOT fire.
        var a = MakeFp(sha: "aaa", comOffsetInBB: [0.51, 0.50, 0.60]);
        var b = MakeFp(sha: "bbb", comOffsetInBB: [0.49, 0.50, 0.60]);

        var (isMirror, _) = ScanOrchestrationService.ClassifyMirror(a, b, score: 0.93);

        isMirror.Should().BeFalse();
    }

    [Fact]
    public void CoMOffset_Null_FallsThroughToHistogram()
    {
        // When CoM offset is unavailable, the histogram heuristic still works.
        var original = MakeFp(sha: "aaa", comOffsetInBB: null);
        var mirrored = MakeFp(sha: "bbb", comOffsetInBB: null,
            featureHist: new Dictionary<string, int> { ["MirrorSolid"] = 1, ["Extrude"] = 2 });

        var (isMirror, reason) = ScanOrchestrationService.ClassifyMirror(original, mirrored, score: 0.92);

        isMirror.Should().BeTrue();
        reason.Should().ContainEquivalentOf("histogram");
    }
}
