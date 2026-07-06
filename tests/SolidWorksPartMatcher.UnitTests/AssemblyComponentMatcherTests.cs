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
        string name, double[] bb, double volume, int faceCount = 6, int? instanceCount = 1)
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
            EntityClosure: []);

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
        var b = MakeComponent("PART-X", [0.02, 0.04, 0.06], 0.00008); // 8x volume

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.VolumeDeltaPercent.Should().BeApproximately(700.0, 1.0); // (8x - 1x)/1x
    }

    [Fact]
    public void BoundingBoxDiffers_ButVolumeIdentical_ClassifiedUnchanged()
    {
        // The core requirement: classification is driven SOLELY by volume — a bounding box that
        // differs (e.g. because a small local feature stretched one axis, or because the axes
        // got labeled differently between two exports) must NOT by itself cause a "Modified"
        // verdict when the real volume hasn't changed. This is exactly the "skewed results"
        // bounding-box classification used to produce.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("PART-X", [0.005, 0.005, 0.24], 0.00001); // wildly different box, same volume

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
    }

    [Fact]
    public void TinyNonZeroVolumeChange_StillClassifiedModified()
    {
        // "So long as it's not 0%, report it as modified" — there is no meaningful tolerance
        // band anymore, only a near-zero floor for floating-point noise. A genuine (if tiny)
        // real-volume difference must still be reported.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.001);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.001 * 1.0001); // 0.01% real change

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.Reasons.Should().Contain(r => r.StartsWith("Volume increased by"));
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
    public void FallbackMatch_WithHighBlendedSimilarityAndLargeVolumeDelta_StillClassifiedModified()
    {
        // Mirrors a real case found against actual assembly files: two differently-named
        // components with identical bounding boxes (so BB/topology/feature-histogram similarity
        // is perfect) but a 38.5% volume difference. Once the fallback matcher's blended
        // similarity score accepts this as a probable rename, there's no more reliable way to
        // independently second-guess that acceptance by volume size alone than there is for any
        // other pair (the same reasoning that removed bounding box from classification entirely)
        // — so this is just a big revision, not grounds for suspicion.
        var a = MakeComponent("OLD-NAME", [0.1, 0.15, 0.2], 0.002, faceCount: 10);
        var b = MakeComponent("NEW-NAME", [0.1, 0.15, 0.2], 0.002 * 1.385, faceCount: 10);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.GeometricSimilarityScore.Should().NotBeNull();
        diff.GeometricSimilarityScore!.Value.Should().BeGreaterThan(_tol.GeometricSimilarityMatchThreshold);
        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.Reasons.Should().Contain("Same shape, different name (likely renamed).");
        diff.Reasons.Should().Contain(r => r.StartsWith("Volume increased by"));
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
    public void DuplicateNameWithinOneAssembly_LogsWarning()
    {
        var a1 = MakeComponent("DUP", [0.01, 0.02, 0.03], 0.00001);
        var a2 = a1 with { ProductId = "DUP-2" }; // same MatchKey, distinct product
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
