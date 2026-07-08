using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class OccurrencePositionComparerTests
{
    private const double Tol = 0.5;

    private static IReadOnlyList<double[]> Positions(params double[][] p) => p;
    private static double[] P(double x, double y = 0, double z = 0) => [x, y, z];

    [Fact]
    public void IdenticalPositionSets_ReportNotChanged()
    {
        var a = Positions(P(0), P(1));
        var b = Positions(P(0), P(1));

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeFalse();
    }

    [Fact]
    public void OneInstanceMovedBeyondTolerance_ReportsChanged()
    {
        var a = Positions(P(0), P(1));
        var b = Positions(P(0), P(5)); // second instance moved far

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeTrue();
    }

    [Fact]
    public void SameBooleanRegardlessOfHowManyInstancesMoved()
    {
        // The output is a coarse yes/no — one-moved and all-moved both just say "true". We never
        // pinpoint how many (that count was inconsistent and is deliberately not reported).
        var baseline = Positions(P(0), P(1), P(2));

        var oneMoved = Positions(P(0), P(1), P(9));
        var allMoved = Positions(P(9), P(10), P(11));

        OccurrencePositionComparer.PositionChanged(baseline, oneMoved, Tol).Should().BeTrue();
        OccurrencePositionComparer.PositionChanged(baseline, allMoved, Tol).Should().BeTrue();
    }

    [Fact]
    public void SwapOfTwoIdenticalInstances_IsNotAChange()
    {
        // Two identical instances swapping positions produces a geometrically identical assembly —
        // the set of positions is unchanged, so this is correctly reported as not changed.
        var a = Positions(P(0), P(1));
        var b = Positions(P(1), P(0));

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeFalse();
    }

    [Fact]
    public void TwoInstancesCollapsingOntoOnePosition_ReportsChanged()
    {
        // Both A-instances sit at the origin; in B one stays and one moves away. Only one B-slot
        // is near the origin, so the two A-instances cannot both claim a within-tolerance
        // counterpart — correctly flagged as changed (a nearest-neighbour check ignoring
        // multiplicity would wrongly match both to the single near slot and say unchanged).
        var a = Positions(P(0), P(0));
        var b = Positions(P(0), P(5));

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeTrue();
    }

    [Fact]
    public void EmptyOnEitherSide_ReturnsNull_NotFalse()
    {
        OccurrencePositionComparer.PositionChanged(Positions(), Positions(P(0)), Tol).Should().BeNull();
        OccurrencePositionComparer.PositionChanged(Positions(P(0)), Positions(), Tol).Should().BeNull();
    }

    [Fact]
    public void DifferentCounts_StationaryOverlap_ReportsNotChanged()
    {
        // 1 instance in A, 2 in B (a quantity change, reported separately). The one shared
        // instance did not move, so position is unchanged — the extra instance is an addition,
        // not a relocation.
        var a = Positions(P(0));
        var b = Positions(P(0), P(5));

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeFalse();
    }

    [Fact]
    public void DifferentCounts_OverlapMoved_ReportsChanged()
    {
        var a = Positions(P(0));
        var b = Positions(P(5), P(6)); // nothing near the original position

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeTrue();
    }

    [Fact]
    public void WithinToleranceButNotExact_ReportsNotChanged()
    {
        var a = Positions(P(0));
        var b = Positions(P(0.4)); // 0.4 < Tol 0.5 — export rounding, not a real move

        OccurrencePositionComparer.PositionChanged(a, b, Tol).Should().BeFalse();
    }
}
