using FluentAssertions;
using SolidWorksPartMatcher.Application.Services;
using SolidWorksPartMatcher.Domain.Models;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class MatchReasonAggregatorTests
{
    private static readonly Guid ClusterA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ClusterB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Fp1 = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Fp2 = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Fp3 = new("00000000-0000-0000-0000-000000000003");

    private static CandidatePair Pair(
        Guid a, Guid b, double score, string? reason,
        PartClassification classification = PartClassification.PossibleMatch) => new(
        Id: Guid.NewGuid(),
        ScanRunId: Guid.Empty,
        FingerprintAId: a,
        FingerprintBId: b,
        CoarseScore: score,
        MatchedBuckets: [],
        Classification: classification,
        Confidence: null,
        ClassificationReason: reason,
        ComparatorVersion: null,
        ToleranceProfile: null);

    private static ClusterMember Member(Guid cluster, Guid fp) => new(cluster, fp, IsRepresentative: false);

    [Fact]
    public void OrdersByDescendingCoarseScore_AndDeduplicates()
    {
        var pairs = new[]
        {
            Pair(Fp1, Fp2, 0.50, "weak evidence"),
            Pair(Fp2, Fp3, 0.90, "SHA-256 match"),
            Pair(Fp1, Fp3, 0.70, "weak evidence"), // duplicate reason, middling score
        };
        var members = new[] { Member(ClusterA, Fp1), Member(ClusterA, Fp2), Member(ClusterA, Fp3) };

        var reasons = MatchReasonAggregator.ReasonsByCluster(pairs, members);

        reasons[ClusterA].Should().Equal("SHA-256 match", "weak evidence");
    }

    [Fact]
    public void IgnoresPairsSpanningTwoClusters()
    {
        // Fp2 and Fp3 are in different clusters, so their pair explains neither.
        var pairs = new[] { Pair(Fp2, Fp3, 0.9, "cross-cluster") };
        var members = new[] { Member(ClusterA, Fp2), Member(ClusterB, Fp3) };

        MatchReasonAggregator.ReasonsByCluster(pairs, members).Should().BeEmpty();
    }

    [Fact]
    public void SkipsBlankReasons_AndOmitsClustersWithNone()
    {
        var pairs = new[] { Pair(Fp1, Fp2, 0.9, null), Pair(Fp1, Fp2, 0.8, "   ") };
        var members = new[] { Member(ClusterA, Fp1), Member(ClusterA, Fp2) };

        MatchReasonAggregator.ReasonsByCluster(pairs, members).Should().NotContainKey(ClusterA);
    }

    // A cluster of three can be joined transitively (Fp1~Fp2, Fp2~Fp3) while the Fp1-Fp3 pair was
    // itself compared and came out Distinct or ComparisonFailed. Both pairs are persisted, so the
    // aggregator must not present a non-match reason as evidence that the group matched.
    [Theory]
    [InlineData(PartClassification.Distinct)]
    [InlineData(PartClassification.ComparisonFailed)]
    public void ExcludesNonMatchPairs_EvenWithinOneCluster(PartClassification nonMatch)
    {
        var pairs = new[]
        {
            Pair(Fp1, Fp2, 0.95, "SHA-256 match", PartClassification.BinaryDuplicate),
            Pair(Fp2, Fp3, 0.90, "rigid transform confirmed", PartClassification.ExactGeometryMatch),
            Pair(Fp1, Fp3, 0.99, "different face count", nonMatch), // highest score — would sort first
        };
        var members = new[] { Member(ClusterA, Fp1), Member(ClusterA, Fp2), Member(ClusterA, Fp3) };

        var reasons = MatchReasonAggregator.ReasonsByCluster(pairs, members);

        reasons[ClusterA].Should().Equal("SHA-256 match", "rigid transform confirmed");
        reasons[ClusterA].Should().NotContain("different face count");
    }

    [Fact]
    public void OmitsCluster_WhenItsOnlyReasonedPairIsANonMatch()
    {
        var pairs = new[] { Pair(Fp1, Fp2, 0.99, "different face count", PartClassification.Distinct) };
        var members = new[] { Member(ClusterA, Fp1), Member(ClusterA, Fp2) };

        MatchReasonAggregator.ReasonsByCluster(pairs, members).Should().NotContainKey(ClusterA);
    }
}
