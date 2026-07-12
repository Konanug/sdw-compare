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

    // ── Effect of giving STEP real edge/vertex counts (extractor v102) ───────────────────────────
    // They were hardcoded to 0, and ScalarSimilarity(0, 0) returns a free 1.0 — so two of
    // TopologySimilarity's three terms were always perfect for a STEP pair, flooring it at 0.667
    // regardless of the geometry. Making them honest changes scores; these pin which way.

    [Fact]
    public void IdenticalStepPair_RealEdgeVertexCounts_DoNotChangeTheScore()
    {
        // The dominant true-positive class: the same part exported twice. Equal counts score 1.0
        // either way, so the fix cannot cost us a single genuine duplicate.
        var withZeros = MakeFp(faces: 40, edges: 0, verts: 0, mat: null);
        var withReal = MakeFp(faces: 40, edges: 120, verts: 80, mat: null);

        var zeroScore = _scorer.Score(withZeros, withZeros, ScoringWeights.Default);
        var realScore = _scorer.Score(withReal, withReal, ScoringWeights.Default);

        realScore.Should().BeApproximately(zeroScore, 1e-9);
    }

    [Fact]
    public void EngravedStepPair_WithHonestTopology_StillClearsTheCandidateThreshold()
    {
        // An engraving wrecks topology similarity (30 → 120 faces, 90 → 400 edges). With honest
        // counts that term collapses to near zero, costing ~0.077 of the 0.15 topology weight. The
        // pair must still clear CandidateThreshold = 0.40, or Stage 3.7 would never get to see it.
        var plain = MakeFp(vol: 1.0e-5, sa: 2.0e-3, bb: [0.010, 0.050, 0.080],
            faces: 30, edges: 90, verts: 60, mat: null);
        var engraved = MakeFp(vol: 0.9998e-5, sa: 2.016e-3, bb: [0.010, 0.050, 0.080],
            faces: 120, edges: 400, verts: 260, mat: null);

        var score = _scorer.Score(plain, engraved, ScoringWeights.Default);

        score.Should().BeGreaterThan(0.40);
    }

    [Fact]
    public void GenuinelyDifferentTopology_ScoresLowerOnceEdgeVertexCountsAreReal()
    {
        // The point of the change: topology becomes a real discriminator instead of contributing a
        // constant. Two parts with the same size but wildly different topology must now score lower
        // than they did when their edge/vertex counts were both a free 1.0.
        var aZeros = MakeFp(sha: "a", faces: 20, edges: 0, verts: 0, mat: null);
        var bZeros = MakeFp(sha: "b", faces: 200, edges: 0, verts: 0, mat: null);
        var aReal = MakeFp(sha: "a", faces: 20, edges: 60, verts: 40, mat: null);
        var bReal = MakeFp(sha: "b", faces: 200, edges: 700, verts: 500, mat: null);

        var inflated = _scorer.Score(aZeros, bZeros, ScoringWeights.Default);
        var honest = _scorer.Score(aReal, bReal, ScoringWeights.Default);

        honest.Should().BeLessThan(inflated);
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
    [InlineData("1/7", "0.14", true)]   // 1/7 ≈ 0.142857 → "0.14"
    [InlineData("6/8", "0.75", true)]   // 6/8 = 0.75 → "0.75"
    [InlineData("3/8", "0.375", true)]   // 3/8 = 0.375 → "0.38"; "0.375" → "0.38" → equal
    [InlineData("3/8", "0.38", true)]   // 0.38 → "0.38"
    // Decimals that round to the same 2dp
    [InlineData("0.55", "0.553231", true)]   // both → "0.55"
    [InlineData("0.14", "0.142857", true)]   // both → "0.14"
    // Distinct at 2dp
    [InlineData("0.14", "0.15", false)]  // "0.14" ≠ "0.15"
    [InlineData("0.55", "0.56", false)]
    // String equality fallback
    [InlineData("Rev A", "Rev A", true)]
    [InlineData("Rev A", "rev a", true)]   // case-insensitive
    [InlineData("Rev A", "Rev B", false)]
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

        var scoreWithTolerance = _scorer.Score(a, b, ScoringWeights.Default);
        var scoreIdentical = _scorer.Score(a, a, ScoringWeights.Default);

        scoreWithTolerance.Should().BeLessThan(scoreIdentical);
    }

    [Fact]
    public void FeatureToleranceConstant_IsHalfMillimetre()
    {
        WeightedCandidateScorer.FeatureToleranceM.Should().BeApproximately(0.0005, 1e-9);
    }

    // ── MeasurementParser ────────────────────────────────────────────────────

    [Theory]
    [InlineData("1/7", 1.0 / 7.0)]
    [InlineData("6/8", 6.0 / 8.0)]
    [InlineData("3/8", 3.0 / 8.0)]
    [InlineData("0.55", 0.55)]
    [InlineData("0.553231", 0.553231)]
    [InlineData("5", 5.0)]
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
    [InlineData(1.0 / 7.0, "0.14")]
    [InlineData(6.0 / 8.0, "0.75")]
    [InlineData(0.553231, "0.55")]
    [InlineData(0.55, "0.55")]
    public void MeasurementParser_FormatDisplay_TwoDecimalPlaces(double value, string expected)
    {
        MeasurementParser.FormatDisplay(value).Should().Be(expected);
    }

    [Fact]
    public void MeasurementParser_FullPrecisionPreserved_WhileDisplayRounds()
    {
        // 0.55 and 0.553231 display the same but are not numerically equal.
        MeasurementParser.TryParseNumber("0.55", out var v1);
        MeasurementParser.TryParseNumber("0.553231", out var v2);

        // Full-precision values differ
        v1.Should().NotBe(v2);

        // Both display as "0.55"
        MeasurementParser.FormatDisplay(v1).Should().Be("0.55");
        MeasurementParser.FormatDisplay(v2).Should().Be("0.55");
    }

    // ── MeasurementParser.TryParseToMm ──────────────────────────────────────

    [Theory]
    [InlineData("0.5in", MeasurementParser.LengthUnit.Inch, 0.5 * 25.4)]
    [InlineData("3/8in", MeasurementParser.LengthUnit.Inch, 0.375 * 25.4)]
    [InlineData("1.500in", MeasurementParser.LengthUnit.Inch, 1.5 * 25.4)]
    [InlineData("0.5 in", MeasurementParser.LengthUnit.Inch, 0.5 * 25.4)]   // space before suffix
    [InlineData("0.5IN", MeasurementParser.LengthUnit.Inch, 0.5 * 25.4)]   // uppercase
    [InlineData("12.7mm", MeasurementParser.LengthUnit.Millimetre, 12.7)]
    [InlineData("0.5", MeasurementParser.LengthUnit.Unknown, 0.5)]           // no suffix → raw value
    public void TryParseToMm_ParsesCorrectly(string input, MeasurementParser.LengthUnit expectedUnit, double expectedMm)
    {
        var ok = MeasurementParser.TryParseToMm(input, out var mm, out var unit);
        ok.Should().BeTrue();
        unit.Should().Be(expectedUnit);
        mm.Should().BeApproximately(expectedMm, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void TryParseToMm_ReturnsFalse_ForUnparseable(string? input)
    {
        MeasurementParser.TryParseToMm(input, out _, out _).Should().BeFalse();
    }

    // ── AreEquivalentPropertyValues — inch unit suffix ───────────────────────

    [Theory]
    // Same inch values
    [InlineData("0.5in", "0.5in", true)]
    [InlineData("0.50in", "0.5in", true)]   // trailing zero
    // Fraction/decimal equivalence in inches (|diff_mm| ≤ 0.5)
    [InlineData("3/8in", "0.38in", true)]   // |9.525 - 9.652| = 0.127 mm
    // 0.5 mm boundary in inches
    [InlineData("1.500in", "1.519in", true)]   // |38.100 - 38.583| ≈ 0.483 mm ≤ 0.5
    [InlineData("1.500in", "1.520in", false)]  // |38.100 - 38.608| ≈ 0.508 mm > 0.5
    // Cross-unit: same physical dimension
    [InlineData("0.5in", "12.7mm", true)]   // 0.5 × 25.4 = 12.7 mm
    [InlineData("1.0in", "25.4mm", true)]
    // Different inch values outside tolerance
    [InlineData("1.0in", "2.0in", false)]
    // Mixed: one side has "in" suffix, other is a bare decimal — the unit is inherited
    [InlineData("3/8in", "0.375", true)]   // exact: 9.525 mm == 9.525 mm
    [InlineData("3/8in", "0.38", true)]   // |9.525 - 9.652| = 0.127 mm ≤ 0.5
    [InlineData("0.5in", "0.5", true)]   // 12.7 mm == 12.7 mm
    [InlineData("1.500in", "1.519", true)]   // |38.100 - 38.583| ≈ 0.483 mm ≤ 0.5
    [InlineData("1.500in", "1.520", false)]  // |38.100 - 38.608| ≈ 0.508 mm > 0.5
    public void AreEquivalentPropertyValues_InchSuffix_Correct(string a, string b, bool expected)
    {
        WeightedCandidateScorer.AreEquivalentPropertyValues(a, b)
            .Should().Be(expected);
    }

    [Fact]
    public void AreEquivalentPropertyValues_UnitlessSideInheritsExplicitUnit()
    {
        // When one side has an explicit unit the bare-number side inherits it.
        // "0.5" alongside "0.5in" is treated as 0.5 inches = 12.7 mm → equal.
        WeightedCandidateScorer.AreEquivalentPropertyValues("0.5", "0.5in")
            .Should().BeTrue();
    }
}
