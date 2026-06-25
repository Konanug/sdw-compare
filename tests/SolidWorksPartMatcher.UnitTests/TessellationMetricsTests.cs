using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using Xunit;

// Pure math tests for the tessellation HD-metric thresholds used in Stage 4.5.
// TessellationToleranceComparator lives in the SolidWorks project (COM dependency) and
// cannot be imported here. Instead, the thresholds are kept as literals — if they change
// in TessellationToleranceComparator, update the literals below to match.

namespace SolidWorksPartMatcher.UnitTests;

public sealed class TessellationMetricsTests
{
    // Thresholds mirrored from TessellationToleranceComparator.ClassifyFromMetrics.
    private const double DefaultToleranceM = 0.0005; // 0.5 mm
    private const double HalfTol = DefaultToleranceM * 0.5; // 0.00025 m = 0.25 mm
    private const double CoverageExact = 0.95;
    private const double CoverageEngraving = 0.90;

    // ── Percentile helper (matches Percentile in the comparator) ─────────────

    private static double Percentile(List<float> sorted, int pct)
    {
        if (sorted.Count == 0) return double.MaxValue;
        int idx = Math.Clamp((int)Math.Ceiling(sorted.Count * pct / 100.0) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    [Fact]
    public void Percentile_50_ReturnsMidpoint()
    {
        var values = new List<float> { 1, 2, 3, 4, 5 };
        Percentile(values, 50).Should().BeApproximately(3.0, 1e-6);
    }

    [Fact]
    public void Percentile_95_ReturnsHighValue()
    {
        // 100 values: 95th percentile should be the 95th element (0-indexed: 94).
        var values = Enumerable.Range(1, 100).Select(i => (float)i).ToList();
        Percentile(values, 95).Should().BeApproximately(95.0, 1e-6);
    }

    [Fact]
    public void Percentile_EmptyList_ReturnsDoubleMaxValue()
    {
        Percentile([], 50).Should().Be(double.MaxValue);
    }

    // ── Classification decision logic ────────────────────────────────────────

    private static PartClassification Classify(double coverage, double hd50, bool sketchTextDiffers)
    {
        if (coverage >= CoverageExact && hd50 <= HalfTol)
            return PartClassification.ExactGeometryMatch;
        if (coverage >= CoverageEngraving && hd50 <= HalfTol && sketchTextDiffers)
            return PartClassification.EngravingVariant;
        return PartClassification.PossibleMatch;
    }

    [Fact]
    public void HighCoverageAndLowHd50_NoEngravingDiff_IsExactGeometryMatch()
    {
        // Both coverage (97%) and HD50 (0.2mm) exceed the exact-match thresholds.
        var cls = Classify(coverage: 0.97, hd50: 0.0002, sketchTextDiffers: false);
        cls.Should().Be(PartClassification.ExactGeometryMatch);
    }

    [Fact]
    public void HighCoverageAndLowHd50_WithEngravingDiff_IsStillExactGeometryMatch()
    {
        // ExactGeometryMatch takes priority even when engraving differs.
        var cls = Classify(coverage: 0.97, hd50: 0.0002, sketchTextDiffers: true);
        cls.Should().Be(PartClassification.ExactGeometryMatch);
    }

    [Fact]
    public void ModerateCoverageAndLowHd50_WithEngravingDiff_IsEngravingVariant()
    {
        // 92% coverage, 0.2mm HD50 — engraving variant threshold met.
        var cls = Classify(coverage: 0.92, hd50: 0.0002, sketchTextDiffers: true);
        cls.Should().Be(PartClassification.EngravingVariant);
    }

    [Fact]
    public void ModerateCoverageAndLowHd50_NoEngravingDiff_IsPossibleMatch()
    {
        // Coverage between engraving and exact thresholds, no sketch-text difference.
        var cls = Classify(coverage: 0.92, hd50: 0.0002, sketchTextDiffers: false);
        cls.Should().Be(PartClassification.PossibleMatch);
    }

    [Fact]
    public void LowCoverageRegardlessOfHd50_IsPossibleMatch()
    {
        // Coverage too low for any auto-match.
        var cls = Classify(coverage: 0.80, hd50: 0.0001, sketchTextDiffers: true);
        cls.Should().Be(PartClassification.PossibleMatch);
    }

    [Fact]
    public void Hd50ExceedingHalfTol_IsPossibleMatch()
    {
        // HD50 = 0.3mm > halfTol (0.25mm) — parts are too far apart.
        var cls = Classify(coverage: 0.97, hd50: 0.0003, sketchTextDiffers: false);
        cls.Should().Be(PartClassification.PossibleMatch);
    }

    [Fact]
    public void Hd50ExactlyAtHalfTol_Qualifies()
    {
        // HD50 exactly at the 0.25mm boundary — should still qualify.
        var cls = Classify(coverage: 0.96, hd50: HalfTol, sketchTextDiffers: false);
        cls.Should().Be(PartClassification.ExactGeometryMatch);
    }

    // ── Tolerance constant ────────────────────────────────────────────────────

    [Fact]
    public void HalfToleranceConstant_IsQuarterMillimetre()
    {
        HalfTol.Should().BeApproximately(0.00025, 1e-10); // 0.25 mm in metres
    }

    [Fact]
    public void CoverageExact_Is95Percent()
    {
        CoverageExact.Should().BeApproximately(0.95, 1e-10);
    }

    [Fact]
    public void CoverageEngraving_Is90Percent()
    {
        CoverageEngraving.Should().BeApproximately(0.90, 1e-10);
    }
}
