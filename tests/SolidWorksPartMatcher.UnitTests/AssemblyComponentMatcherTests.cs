using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Assembly;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class AssemblyComponentMatcherTests
{
    private readonly AssemblyComponentMatcher _matcher = new(new WeightedCandidateScorer());
    private readonly AssemblyDiffTolerances _tol = AssemblyDiffTolerances.Default;

    private static AssemblyComponent MakeComponent(
        string name, double[] bb, double volume, int faceCount = 6, int? instanceCount = 1,
        AssemblyComponentPlacement? placement = null)
        => new(
            ProductId: name,
            ProductName: name,
            MatchKey: name,
            InstanceCount: instanceCount,
            SortedBoundingBoxM: bb,
            VolumeM3: volume,
            SurfaceAreaM2: volume / bb.Min() * 2, // arbitrary but consistent-ish
            FaceCount: faceCount,
            FaceTypeHistogram: new Dictionary<string, int> { ["PLANE"] = faceCount },
            FaceGeometricSignature: [],
            EntityClosure: [],
            Placement: placement);

    private static readonly AssemblyComponentPlacement IdentityPlacement =
        new([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]);

    // Same position, rotated 90° about Z relative to IdentityPlacement.
    private static readonly AssemblyComponentPlacement RotatedPlacement =
        new([0, 0, 0], [0, 1, 0], [-1, 0, 0], [0, 0, 1]);

    // Rotation unchanged, but shifted 10mm along X relative to IdentityPlacement.
    private static readonly AssemblyComponentPlacement TranslatedPlacement =
        new([0.01, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]);

    private AssemblyStructure Struct(params AssemblyComponent[] components) => new(components, []);

    [Fact]
    public void IdenticalGeometrySameName_ClassifiedUnchanged()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
        diff.QuantityChanged.Should().BeFalse();
    }

    [Fact]
    public void SignificantSizeChangeSameName_ClassifiedModified()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("PART-X", [0.02, 0.04, 0.06], 0.00008); // 2x each dimension

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.BoundingBoxDeltaPercent.Should().NotBeNull();
        diff.VolumeDeltaPercent.Should().BeApproximately(700.0, 1.0); // (8x - 1x)/1x
    }

    [Fact]
    public void IdenticalGeometryDifferentInstanceCount_UnchangedButQuantityFlagged()
    {
        var a = MakeComponent("BOLT", [0.005, 0.005, 0.02], 0.0000005, instanceCount: 4);
        var b = MakeComponent("BOLT", [0.005, 0.005, 0.02], 0.0000005, instanceCount: 6);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
        diff.QuantityChanged.Should().BeTrue();
        diff.InstanceCountA.Should().Be(4);
        diff.InstanceCountB.Should().Be(6);
    }

    [Fact]
    public void GeometryChangedAndQuantityChanged_BothFlagsSet()
    {
        var a = MakeComponent("BRACKET", [0.01, 0.02, 0.03], 0.00001, instanceCount: 2);
        var b = MakeComponent("BRACKET", [0.02, 0.04, 0.06], 0.00008, instanceCount: 3);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.QuantityChanged.Should().BeTrue();
    }

    [Fact]
    public void NoMatch_ClassifiedRemovedAndAdded()
    {
        var a = MakeComponent("OLD-PART", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("NEW-PART", [0.5, 0.6, 0.7], 0.2, faceCount: 40);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        summary.Components.Should().HaveCount(2);
        summary.Components.Should().Contain(d => d.MatchKey == "OLD-PART" && d.DiffType == AssemblyDiffType.Removed);
        summary.Components.Should().Contain(d => d.MatchKey == "NEW-PART" && d.DiffType == AssemblyDiffType.Added);
    }

    [Fact]
    public void RemovedPart_QuantityShowsZero_NotNull()
    {
        // A missing part is quantity 0, never an unresolved "?" — the two mean very different
        // things (definitely doesn't exist vs. we couldn't determine the count).
        var a = MakeComponent("GONE-PART", [0.01, 0.02, 0.03], 0.00001, instanceCount: 5);
        var b = MakeComponent("OTHER-PART", [0.5, 0.6, 0.7], 0.2, faceCount: 40);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var removed = summary.Components.Single(d => d.MatchKey == "GONE-PART");
        removed.InstanceCountA.Should().Be(5);
        removed.InstanceCountB.Should().Be(0);
    }

    [Fact]
    public void AddedPart_QuantityShowsZero_NotNull()
    {
        var a = MakeComponent("OTHER-PART", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("NEW-PART", [0.5, 0.6, 0.7], 0.2, faceCount: 40, instanceCount: 3);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var added = summary.Components.Single(d => d.MatchKey == "NEW-PART");
        added.InstanceCountA.Should().Be(0);
        added.InstanceCountB.Should().Be(3);
    }

    [Fact]
    public void RenamedPart_MatchedByGeometryFallback()
    {
        var a = MakeComponent("OLD-NAME", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("NEW-NAME", [0.01, 0.02, 0.03], 0.00001);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.GeometricSimilarityScore.Should().NotBeNull();
        diff.GeometricSimilarityScore!.Value.Should().BeGreaterThan(0.9);
        diff.ComponentA.Should().NotBeNull();
        diff.ComponentB.Should().NotBeNull();
    }

    [Fact]
    public void SameNameWildlyDifferentGeometry_ClassifiedSuspiciousMatch()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, faceCount: 6);
        var b = MakeComponent("PART-X", [1.0, 2.0, 3.0], 6.0, faceCount: 200);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.SuspiciousMatch);
    }

    [Fact]
    public void FallbackMatch_WithHighBlendedSimilarityButLargeVolumeDelta_ClassifiedSuspiciousNotModified()
    {
        // Mirrors a real case found against actual assembly files: two differently-named
        // components with identical bounding boxes (so BB/topology/feature-histogram similarity
        // is perfect) but a 38.5% volume difference. The blended fallback score is still high
        // (~0.89) because BB/topology dominate the weighting, but reporting "possible rename"
        // for a 38.5% volume swing is self-contradictory — a renamed-but-unmodified part should
        // look nearly identical. This must be downgraded to SuspiciousMatch, not Modified.
        var a = MakeComponent("OLD-NAME", [0.1, 0.15, 0.2], 0.002, faceCount: 10);
        var b = MakeComponent("NEW-NAME", [0.1, 0.15, 0.2], 0.002 * 1.385, faceCount: 10);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.GeometricSimilarityScore.Should().NotBeNull();
        diff.GeometricSimilarityScore!.Value.Should().BeGreaterThan(_tol.GeometricSimilarityMatchThreshold);
        diff.DiffType.Should().Be(AssemblyDiffType.SuspiciousMatch);
        diff.Reasons.Should().Contain("Matched by shape, but sizes are very different.");
        // Simple, non-technical language only — no raw percentages or "not a real revision"-style
        // internal jargon in the user-facing reasons (see BuildReasons).
        diff.Reasons.Should().NotContain(r => r.Contains('%') || r.Contains("revision"));
    }

    [Fact]
    public void QuantityOnlyChange_SortsBeforePlainUnchanged()
    {
        var qtyA = MakeComponent("Q-PART", [0.01, 0.02, 0.03], 0.00001, instanceCount: 2);
        var qtyB = qtyA with { InstanceCount = 3 };
        var plainUnchanged = MakeComponent("Z-PLAIN", [0.01, 0.02, 0.03], 0.00001);

        var summary = _matcher.Diff(
            Struct(qtyA, plainUnchanged), Struct(qtyB, plainUnchanged), _tol, "A.step", "B.step");

        summary.Components.Select(c => c.MatchKey).Should().ContainInOrder("Q-PART", "Z-PLAIN");
    }

    [Fact]
    public void IdenticalGeometrySameOrientation_OrientationNotFlagged()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, placement: IdentityPlacement);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, placement: IdentityPlacement);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
        diff.OrientationChanged.Should().BeFalse();
        diff.PositionChanged.Should().BeFalse();
    }

    [Fact]
    public void IdenticalGeometryDifferentOrientation_FlaggedSeparatelyFromShapeIdentity()
    {
        // The core requirement: a part rotated within the assembly must still be recognized as
        // the SAME part (geometry identity is derived from the part's own canonical shape, never
        // from assembly placement) — but the orientation difference must still be reported.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, placement: IdentityPlacement);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, placement: RotatedPlacement);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged); // same part — geometry identity unaffected
        diff.OrientationChanged.Should().BeTrue();
        diff.PositionChanged.Should().BeFalse();
        diff.Reasons.Should().Contain("Orientation changed in the assembly.");
    }

    [Fact]
    public void IdenticalGeometryDifferentPosition_FlaggedSeparatelyFromOrientation()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, placement: IdentityPlacement);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, placement: TranslatedPlacement);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.OrientationChanged.Should().BeFalse();
        diff.PositionChanged.Should().BeTrue();
        diff.Reasons.Should().Contain("Position changed in the assembly.");
    }

    [Fact]
    public void MultiInstanceParts_OrientationNotDetermined_NeverGuessed()
    {
        // With more than one instance there's no single unambiguous placement to compare, so
        // Placement stays null on the component and orientation/position must stay null (not
        // determined) rather than silently comparing the wrong occurrence.
        var a = MakeComponent("BOLT", [0.005, 0.005, 0.02], 0.0000005, instanceCount: 4);
        var b = MakeComponent("BOLT", [0.005, 0.005, 0.02], 0.0000005, instanceCount: 4);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.OrientationChanged.Should().BeNull();
        diff.PositionChanged.Should().BeNull();
    }

    [Fact]
    public void SuspiciousMatch_OrientationStillReported_WhenPlacementsAvailable()
    {
        // Orientation/position are placement facts, independent of shape-identity confidence —
        // even a SuspiciousMatch pairing (likely two different parts) should still surface that
        // the compared placements differ, rather than silently dropping the signal. Wording is
        // diffType-neutral ("Orientation changed…", not "Same part…") so it doesn't imply an
        // identity conclusion the classification doesn't actually support.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, faceCount: 6, placement: IdentityPlacement);
        var b = MakeComponent("PART-X", [1.0, 2.0, 3.0], 6.0, faceCount: 200, placement: RotatedPlacement);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.SuspiciousMatch);
        diff.OrientationChanged.Should().BeTrue();
        diff.PositionChanged.Should().BeFalse();
        diff.Reasons.Should().Contain("Orientation changed in the assembly.");
    }

    [Fact]
    public void DuplicateNameWithinOneAssembly_LogsWarning()
    {
        var a1 = MakeComponent("DUP", [0.01, 0.02, 0.03], 0.00001);
        var a2 = a1 with { EntityClosure = [] }; // same MatchKey, distinct instance
        var b = MakeComponent("DUP", [0.01, 0.02, 0.03], 0.00001);

        var summary = _matcher.Diff(Struct(a1, a2), Struct(b), _tol, "A.step", "B.step");

        summary.Warnings.Should().Contain(w => w.Contains("Duplicate component name"));
    }

    [Fact]
    public void Output_IsDeterministicallyOrdered()
    {
        var removed  = MakeComponent("Z-REMOVED", [0.01, 0.01, 0.01], 0.000001, faceCount: 6);
        var added    = MakeComponent("A-ADDED", [0.9, 1.1, 1.3], 0.5, faceCount: 60);
        var modified = MakeComponent("M-MODIFIED", [0.01, 0.02, 0.03], 0.00001);
        var modifiedB = modified with { SortedBoundingBoxM = [0.02, 0.04, 0.06], VolumeM3 = 0.00008 };
        var unchanged = MakeComponent("U-UNCHANGED", [0.01, 0.02, 0.03], 0.00001);

        var structA = Struct(removed, modified, unchanged);
        var structB = Struct(added, modifiedB, unchanged);

        var summary = _matcher.Diff(structA, structB, _tol, "A.step", "B.step");

        summary.Components.Select(c => c.DiffType).Should().ContainInOrder(
            AssemblyDiffType.Removed, AssemblyDiffType.Added,
            AssemblyDiffType.Modified, AssemblyDiffType.Unchanged);
    }
}
