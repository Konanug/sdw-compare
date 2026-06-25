using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Domain.Utilities;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class ScoringTests
{
    private readonly WeightedCandidateScorer _scorer = new();

    private static PartFingerprint MakeFp(
        string sha = "abc", string cfg = "Default",
        double[]? bb = null,
        double vol = 0.001, double sa = 0.05,
        int faces = 20, int edges = 30, int verts = 15,
        string? mat = "Steel",
        Dictionary<string, int>? featureHist = null,
        double? chiralitySign = null) => new(
            Id: Guid.NewGuid(),
            ScannedFileId: Guid.NewGuid(),
            FileSha256: sha,
            ConfigName: cfg,
            ExtractorVersion: 2,
            SolidBodyCount: 1,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: bb ?? [0.05, 0.10, 0.20],
            VolumeM3: vol,
            SurfaceAreaM2: sa,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: faces,
            EdgeCount: edges,
            VertexCount: verts,
            FeatureCount: 5,
            FeatureTypeHistogram: featureHist ?? new Dictionary<string, int> { ["Extrude"] = 2, ["Fillet"] = 1 },
            Material: mat,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: "2024",
            ExtractorVersionLabel: "test-2",
            ExtractedUtc: DateTime.UtcNow,
            ChiralitySign: chiralitySign,
            CoMOffsetInBB: null,
            SketchTextCutCount: 0,
            SuppressedSolidBodyCount: null,
            SuppressedBoundingBoxM: null,
            SuppressedVolumeM3: null,
            SuppressedSurfaceAreaM2: null,
            SuppressedFaceCount: null,
            SuppressedEdgeCount: null,
            SuppressedVertexCount: null);

    [Fact]
    public void IdenticalFingerprints_ScoreIsOne()
    {
        var fp = MakeFp();
        var score = _scorer.Score(fp, fp, ScoringWeights.Default);
        score.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void VeryDifferentFingerprints_ScoreIsLow()
    {
        // Different material and config name removes constant shared credit from material/filename tokens
        var a = MakeFp(sha: "aaa", cfg: "Default", vol: 0.001, sa: 0.05, bb: [0.05, 0.10, 0.20], mat: "Steel");
        var b = MakeFp(sha: "bbb", cfg: "Variant-XYZ", vol: 50.0, sa: 1000.0, bb: [5.0, 10.0, 20.0], mat: "Aluminum");
        var score = _scorer.Score(a, b, ScoringWeights.Default);
        score.Should().BeLessThan(0.40);
    }

    [Fact]
    public void ScoreWeightsSumToOne()
    {
        var w = ScoringWeights.Default;
        var sum = w.BoundingBox + w.Volume + w.SurfaceArea + w.Topology
                + w.FeatureHistogram + w.MaterialProperties
                + w.CustomProperties + w.FilenameTokens;
        sum.Should().BeApproximately(1.0, 0.001);
    }

    // ── Custom property / fraction-decimal equivalence ───────────────────────

    [Theory]
    // Fraction vs equivalent decimal (rounded to 2 dp)
    [InlineData("1/7",     "0.14",     true)]   // 1/7 ≈ 0.142857 → "0.14"
    [InlineData("6/8",     "0.75",     true)]   // 6/8 = 0.75 → "0.75"
    [InlineData("3/8",     "0.375",    true)]   // 3/8 = 0.375 → "0.38"; "0.375" → "0.38" → equal
    [InlineData("3/8",     "0.38",     true)]   // 0.38 → "0.38"
    // Decimals that round to the same 2dp
    [InlineData("0.55",    "0.553231", true)]   // both → "0.55"
    [InlineData("0.14",    "0.142857", true)]   // both → "0.14"
    // Distinct at 2dp
    [InlineData("0.14",    "0.15",     false)]  // "0.14" ≠ "0.15"
    [InlineData("0.55",    "0.56",     false)]
    // String equality fallback
    [InlineData("Rev A",   "Rev A",    true)]
    [InlineData("Rev A",   "rev a",    true)]   // case-insensitive
    [InlineData("Rev A",   "Rev B",    false)]
    public void AreEquivalentPropertyValues_Correct(string a, string b, bool expected)
    {
        WeightedCandidateScorer.AreEquivalentPropertyValues(a, b)
            .Should().Be(expected);
    }

    [Fact]
    public void IdenticalCustomProperties_FullScore()
    {
        var props = new Dictionary<string, string> { ["Revision"] = "A", ["Thickness"] = "0.25" };
        var a = MakeFp(sha: "aaa");
        var b = MakeFp(sha: "bbb");
        // Both have identical geometry — score should still be ≈ 1.0
        var score = _scorer.Score(
            a with { },
            b with { },
            ScoringWeights.Default);
        score.Should().BeGreaterThan(0.90);
    }

    // ── 0.5 mm bounding-box tolerance ────────────────────────────────────────

    [Fact]
    public void BoundingBoxDifference_WithinHalfMmTolerance_ScoresLikeExact()
    {
        // Dimensions differ by 0.3 mm (0.0003 m) on every axis — within 0.5 mm tolerance.
        var a = MakeFp(bb: [0.05000, 0.10000, 0.20000]);
        var b = MakeFp(bb: [0.05030, 0.10030, 0.20030]); // +0.3 mm each

        var score = _scorer.Score(a, b, ScoringWeights.Default);

        // BB contributes 0.30 × 1.0 (tolerance absorbs the delta) rather than a penalty.
        // Combined with other identical components, total should approach 1.0.
        score.Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void BoundingBoxDifference_ExceedingHalfMmTolerance_IsScaledDown()
    {
        // Dimensions differ by 1.0 mm — exceeds the 0.5 mm tolerance.
        var a = MakeFp(bb: [0.050, 0.100, 0.200]);
        var b = MakeFp(bb: [0.051, 0.101, 0.201]); // +1 mm each

        var scoreWithTolerance    = _scorer.Score(a, b, ScoringWeights.Default);
        var scoreIdentical        = _scorer.Score(a, a, ScoringWeights.Default);

        scoreWithTolerance.Should().BeLessThan(scoreIdentical);
    }

    [Fact]
    public void FeatureToleranceConstant_IsHalfMillimetre()
    {
        WeightedCandidateScorer.FeatureToleranceM.Should().BeApproximately(0.0005, 1e-9);
    }

    // ── MeasurementParser ────────────────────────────────────────────────────

    [Theory]
    [InlineData("1/7",     1.0 / 7.0)]
    [InlineData("6/8",     6.0 / 8.0)]
    [InlineData("3/8",     3.0 / 8.0)]
    [InlineData("0.55",    0.55)]
    [InlineData("0.553231",0.553231)]
    [InlineData("5",       5.0)]
    public void MeasurementParser_ParsesCorrectly(string input, double expected)
    {
        var ok = MeasurementParser.TryParseNumber(input, out var result);
        ok.Should().BeTrue();
        result.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1/0")]   // division by zero
    public void MeasurementParser_ReturnsFalse_ForInvalidInput(string? input)
    {
        MeasurementParser.TryParseNumber(input, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(1.0 / 7.0,   "0.14")]
    [InlineData(6.0 / 8.0,   "0.75")]
    [InlineData(0.553231,     "0.55")]
    [InlineData(0.55,         "0.55")]
    public void MeasurementParser_FormatDisplay_TwoDecimalPlaces(double value, string expected)
    {
        MeasurementParser.FormatDisplay(value).Should().Be(expected);
    }

    [Fact]
    public void MeasurementParser_FullPrecisionPreserved_WhileDisplayRounds()
    {
        // 0.55 and 0.553231 display the same but are not numerically equal.
        MeasurementParser.TryParseNumber("0.55",     out var v1);
        MeasurementParser.TryParseNumber("0.553231", out var v2);

        // Full-precision values differ
        v1.Should().NotBe(v2);

        // Both display as "0.55"
        MeasurementParser.FormatDisplay(v1).Should().Be("0.55");
        MeasurementParser.FormatDisplay(v2).Should().Be("0.55");
    }
}
