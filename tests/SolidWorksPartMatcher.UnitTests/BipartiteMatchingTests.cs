using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class BipartiteMatchingTests
{
    [Fact]
    public void GreedyWouldStrandAVertex_ButAugmentingFindsPerfectMatching()
    {
        // A0 is adjacent to B0 and B1; A1 is adjacent to B0 only. A naive greedy pass that takes
        // A0→B0 first strands A1 (its only option B0 is gone) and reports a matching of size 1 —
        // wrongly concluding "no valid full pairing". Augmenting paths re-route A0 to B1, freeing
        // B0 for A1, and find the true perfect matching of size 2. This is exactly why the
        // position comparer must use proper matching, not greedy nearest-neighbour.
        var adjacency = new[,]
        {
            { true, true },
            { true, false },
        };

        BipartiteMatching.MaxMatchingSize(adjacency).Should().Be(2);
    }

    [Fact]
    public void NoEdges_MatchingIsZero()
    {
        var adjacency = new[,]
        {
            { false, false },
            { false, false },
        };

        BipartiteMatching.MaxMatchingSize(adjacency).Should().Be(0);
    }

    [Fact]
    public void FullyConnected_MatchesEverySmallerSideVertex()
    {
        var adjacency = new[,]
        {
            { true, true, true },
            { true, true, true },
        };

        BipartiteMatching.MaxMatchingSize(adjacency).Should().Be(2); // min(2, 3)
    }

    [Fact]
    public void NoPerfectMatching_WhenTwoLeftVerticesShareTheirOnlyPartner()
    {
        // A0 and A1 both only reach B0 — at most one can be matched.
        var adjacency = new[,]
        {
            { true, false },
            { true, false },
        };

        BipartiteMatching.MaxMatchingSize(adjacency).Should().Be(1);
    }

    [Fact]
    public void IsDeterministic_AcrossRepeatedRuns()
    {
        var adjacency = new[,]
        {
            { true, true, false },
            { false, true, true },
            { true, false, true },
        };

        int first = BipartiteMatching.MaxMatchingSize(adjacency);
        for (int i = 0; i < 5; i++)
            BipartiteMatching.MaxMatchingSize(adjacency).Should().Be(first);
        first.Should().Be(3);
    }
}
