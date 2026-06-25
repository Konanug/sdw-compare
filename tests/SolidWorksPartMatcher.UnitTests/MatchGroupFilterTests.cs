using FluentAssertions;
using SolidWorksPartMatcher.Application.Services;
using SolidWorksPartMatcher.Domain.Models;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class MatchGroupFilterTests
{
    // ── Label mapping ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PartClassification.BinaryDuplicate,              "Geometry Match (Identical Copy)")]
    [InlineData(PartClassification.ExactGeometryMatch,           "Geometry Match")]
    [InlineData(PartClassification.GeometryMatchMetadataVariant, "Geometry Match (Metadata Variant)")]
    [InlineData(PartClassification.MirrorOrHandedVariant,        "Geometry Match (Mirror Variant)")]
    [InlineData(PartClassification.RevisionFamily,               "Geometry Match (Revision Family)")]
    [InlineData(PartClassification.PossibleMatch,                "Possible Match")]
    [InlineData(PartClassification.Distinct,                     "Distinct")]
    [InlineData(PartClassification.ComparisonFailed,             "Comparison Failed")]
    public void ToLabel_ReturnsHumanReadableString(PartClassification cls, string expected)
        => MatchGroupFilter.ToLabel(cls).Should().Be(expected);

    // ── Display name ordering ────────────────────────────────────────────────

    [Fact]
    public void BuildDisplayNames_ReturnsDeterministicNumbering()
    {
        var clusters = new[]
        {
            MakeCluster("Bracket-Left"),
            MakeCluster("Bracket-Right"),
            MakeCluster("Motor Plate"),
        };
        var names = MatchGroupFilter.BuildDisplayNames(clusters);

        names.Should().BeEquivalentTo(
            ["Match 1", "Match 2", "Match 3"],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void BuildDisplayNames_SortedAlphabeticallyByCanonicalName()
    {
        var clusters = new[]
        {
            MakeCluster("Z-Part"),
            MakeCluster("A-Part"),
            MakeCluster("M-Part"),
        };
        var names = MatchGroupFilter.BuildDisplayNames(clusters);

        names.Should().HaveCount(3);
        // A-Part → Match 1, M-Part → Match 2, Z-Part → Match 3
        names[0].Should().Be("Match 1");
        names[1].Should().Be("Match 2");
        names[2].Should().Be("Match 3");
    }

    [Fact]
    public void BuildDisplayNames_IsDeterministicAcrossMultipleCalls()
    {
        var clusters = new[]
        {
            MakeCluster("Beta"),
            MakeCluster("Alpha"),
        };
        var first  = MatchGroupFilter.BuildDisplayNames(clusters);
        var second = MatchGroupFilter.BuildDisplayNames(clusters);

        first.Should().BeEquivalentTo(second, o => o.WithStrictOrdering());
    }

    // ── Filter: no filters active ────────────────────────────────────────────

    [Fact]
    public void MatchesFilter_NoFilters_AlwaysTrue()
    {
        var files = new[] { ("Part.SLDPRT", @"C:\Parts\Part.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            "Canonical", PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, null, null, null)
            .Should().BeTrue();
    }

    // ── Filter: classification ────────────────────────────────────────────────

    [Fact]
    public void MatchesFilter_ClassificationMatch_ReturnsTrue()
    {
        var files = new[] { ("A.SLDPRT", @"C:\A.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.ExactGeometryMatch, ReviewStatus.Pending,
            files, null, PartClassification.ExactGeometryMatch, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_ClassificationMismatch_ReturnsFalse()
    {
        var files = new[] { ("A.SLDPRT", @"C:\A.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.PossibleMatch, ReviewStatus.Pending,
            files, null, PartClassification.ExactGeometryMatch, null)
            .Should().BeFalse();
    }

    // ── Filter: review status ─────────────────────────────────────────────────

    [Fact]
    public void MatchesFilter_ReviewStatusMatch_ReturnsTrue()
    {
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Approved,
            [], null, null, ReviewStatus.Approved)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_ReviewStatusMismatch_ReturnsFalse()
    {
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            [], null, null, ReviewStatus.Approved)
            .Should().BeFalse();
    }

    // ── Filter: search text ───────────────────────────────────────────────────

    [Fact]
    public void MatchesFilter_SearchMatchesFilename_ReturnsTrue()
    {
        var files = new[] { ("MotorPlate.SLDPRT", @"C:\Parts\MotorPlate.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, "motor", null, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_SearchMatchesFilename_CaseInsensitive()
    {
        var files = new[] { ("MotorPlate.SLDPRT", @"C:\Parts\MotorPlate.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, "MOTORPLATE", null, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_SearchMatchesPath_ReturnsTrue()
    {
        var files = new[] { ("Part.SLDPRT", @"C:\Engineering\Brackets\Part.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, "Brackets", null, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_SearchMatchesCanonicalName_ReturnsTrue()
    {
        MatchGroupFilter.MatchesFilter(
            "Left Bracket Assembly", PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            [], "bracket", null, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_SearchNoMatch_ReturnsFalse()
    {
        var files = new[] { ("MotorPlate.SLDPRT", @"C:\Parts\MotorPlate.SLDPRT") };
        MatchGroupFilter.MatchesFilter(
            "Motor Plate", PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, "COMPLETELY_DIFFERENT_TOKEN", null, null)
            .Should().BeFalse();
    }

    // ── Filter: group visible when ANY child matches ──────────────────────────

    [Fact]
    public void MatchesFilter_OneChildMatchesSearch_GroupIsVisible()
    {
        var files = new[]
        {
            ("AlphaWidget.SLDPRT",  @"C:\A\AlphaWidget.SLDPRT"),
            ("BetaPlate.SLDPRT",    @"C:\B\BetaPlate.SLDPRT"),
        };
        // Only the second file matches.
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, "BetaPlate", null, null)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_NoChildMatchesSearch_GroupHidden()
    {
        var files = new[]
        {
            ("AlphaWidget.SLDPRT",  @"C:\A\AlphaWidget.SLDPRT"),
            ("BetaPlate.SLDPRT",    @"C:\B\BetaPlate.SLDPRT"),
        };
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.BinaryDuplicate, ReviewStatus.Pending,
            files, "ZZZNOMATCH", null, null)
            .Should().BeFalse();
    }

    // ── Filter: combined classification + review status ───────────────────────

    [Fact]
    public void MatchesFilter_BothFiltersMatch_ReturnsTrue()
    {
        MatchGroupFilter.MatchesFilter(
            "Canonical", PartClassification.MirrorOrHandedVariant, ReviewStatus.Approved,
            [], null, PartClassification.MirrorOrHandedVariant, ReviewStatus.Approved)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesFilter_ClassificationMatchButStatusMismatch_ReturnsFalse()
    {
        MatchGroupFilter.MatchesFilter(
            null, PartClassification.MirrorOrHandedVariant, ReviewStatus.Pending,
            [], null, PartClassification.MirrorOrHandedVariant, ReviewStatus.Approved)
            .Should().BeFalse();
    }

    // ── Filter: clear filters (all null) ─────────────────────────────────────

    [Fact]
    public void MatchesFilter_ClearFilters_AllGroupsPassWithEmptySearch()
    {
        var groups = new[]
        {
            (PartClassification.BinaryDuplicate,    ReviewStatus.Approved),
            (PartClassification.PossibleMatch,      ReviewStatus.Rejected),
            (PartClassification.MirrorOrHandedVariant, ReviewStatus.Pending),
        };

        foreach (var (cls, status) in groups)
        {
            MatchGroupFilter.MatchesFilter(
                null, cls, status, [], null, null, null)
                .Should().BeTrue($"after clear, {cls} with {status} should pass");
        }
    }

    // ── Mirror/PossibleMatch identity ────────────────────────────────────────

    [Fact]
    public void ToLabel_MirrorVariant_LabelNotEqualToExactMatch()
    {
        MatchGroupFilter.ToLabel(PartClassification.MirrorOrHandedVariant)
            .Should().NotBe(MatchGroupFilter.ToLabel(PartClassification.ExactGeometryMatch));
    }

    [Fact]
    public void ToLabel_PossibleMatch_LabelNotEqualToExactMatch()
    {
        MatchGroupFilter.ToLabel(PartClassification.PossibleMatch)
            .Should().NotBe(MatchGroupFilter.ToLabel(PartClassification.ExactGeometryMatch));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PartCluster MakeCluster(string canonicalName) => new(
        Id: Guid.NewGuid(),
        ScanRunId: Guid.NewGuid(),
        CanonicalName: canonicalName,
        Classification: PartClassification.BinaryDuplicate,
        RepresentativeFingerprintId: Guid.NewGuid(),
        ReviewStatus: ReviewStatus.Pending,
        ReviewerNote: null,
        ReviewedUtc: null,
        ReviewerName: null);
}
