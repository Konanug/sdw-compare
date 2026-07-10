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
        IReadOnlyList<double[]>? occurrencePositions = null)
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
            OccurrencePositionsM: occurrencePositions ?? []);

    private static AssemblyComponent MakeComponentWithSig(
        string name, double[] bb, double volume, string[] signature)
        => MakeComponent(name, bb, volume, faceCount: signature.Length)
            with { FaceGeometricSignature = signature };

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
    public void VolumeChange_AtOrAboveDisplayPrecision_ClassifiedModified()
    {
        // Anything that shows as a nonzero % (>= 0.005%, the 2-decimal display precision) is still
        // reported as Modified. 0.01% clears the band.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.001);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.001 * 1.0001); // 0.01% real change

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.Reasons.Should().Contain(r => r.StartsWith("Volume increased by"));
    }

    [Fact]
    public void SubDisplayPrecisionVolumeChange_ClassifiedUnchanged()
    {
        // A volume delta that rounds to 0.00% at the reported (2-decimal) precision is noise, not a
        // change — it must NOT be flagged Modified nor ticked as a volume change, since it would
        // show the contradictory "Volume: 0%" against a change tick. Real OCCT volumes of an
        // unchanged part differ by a hair between two exports.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.001);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.001 * 1.00001); // 0.001% — shows as 0%

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        var diff = summary.Components.Single();
        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
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

    private static readonly double[][] AtOrigin = [[0, 0, 0]];
    private static readonly double[][] MovedOneMetre = [[1, 0, 0]];

    [Fact]
    public void PositionChanged_ReportedEvenWhenVolumeUnchanged()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, occurrencePositions: AtOrigin);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, occurrencePositions: MovedOneMetre);

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged); // volume/shape identical
        diff.PositionChanged.Should().BeTrue();
        diff.Reasons.Should().Contain("Position changed in the assembly.");
    }

    [Fact]
    public void PositionChanged_ReportedAlongsideModifiedVolume()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, occurrencePositions: AtOrigin);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00002, occurrencePositions: MovedOneMetre);

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.DiffType.Should().Be(AssemblyDiffType.Modified);
        diff.PositionChanged.Should().BeTrue();
        diff.Reasons.Should().Contain("Position changed in the assembly.");
        diff.Reasons.Should().Contain(r => r.StartsWith("Volume")); // both facts reported, additively
    }

    [Fact]
    public void PositionChanged_ReportedAlongsideSuspiciousMatch()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, faceCount: 6, occurrencePositions: AtOrigin);
        var b = MakeComponent("PART-X", [1.0, 2.0, 3.0], 6.0, faceCount: 200, occurrencePositions: MovedOneMetre);

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.DiffType.Should().Be(AssemblyDiffType.SuspiciousMatch);
        diff.PositionChanged.Should().BeTrue();
        diff.Reasons.Should().Contain("Position changed in the assembly.");
    }

    [Fact]
    public void NoPositionReporting_WhenOccurrencePositionsAbsent()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.PositionChanged.Should().BeNull();
        diff.Reasons.Should().NotContain(r => r.Contains("Position changed"));
    }

    [Fact]
    public void PositionUnchanged_WhenOccurrencePositionsMatch()
    {
        double[][] positions = [[0, 0, 0], [1, 0, 0]];
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, occurrencePositions: positions);
        var b = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, occurrencePositions: positions);

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.PositionChanged.Should().BeFalse();
        diff.Reasons.Should().NotContain(r => r.Contains("Position changed"));
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
    public void ZeroVolumeOnOneSide_SameShape_NotReportedAsModified()
    {
        // Real Test6 case (CE26209H01): a part stored as a non-solid shell in one revision reports
        // zero OCCT volume. That is a file-representation detail, not a physical change, so it must
        // NOT be classified as a -100% "Modified". Same shape → Unchanged, with a clear note, and
        // the volume delta is left undefined (null) rather than a misleading -100%.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001);
        var b = a with { VolumeM3 = 0.0 }; // non-solid in revision B: zero volume, same shell

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
        diff.VolumeDeltaPercent.Should().BeNull();
        diff.Reasons.Should().Contain(r => r.Contains("non-solid geometry"));
    }

    [Fact]
    public void ZeroVolumeOnBothSides_SameShape_Unchanged()
    {
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001) with { VolumeM3 = 0.0 };
        var b = a; // both non-solid, identical shell

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.DiffType.Should().Be(AssemblyDiffType.Unchanged);
        diff.VolumeDeltaPercent.Should().BeNull();
    }

    [Fact]
    public void ZeroVolumeOnOneSide_WildlyDifferentShape_StillSurfacesAsChange()
    {
        // The non-solid guard relaxes the volume signal; it must not blind the comparison to a
        // genuine geometry change. A zero-volume side whose shape is nothing alike still flags.
        var a = MakeComponent("PART-X", [0.01, 0.02, 0.03], 0.00001, faceCount: 6);
        var b = MakeComponent("PART-X", [1.0, 2.0, 3.0], 0.0, faceCount: 200);

        var diff = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step").Components.Single();

        diff.DiffType.Should().Be(AssemblyDiffType.SuspiciousMatch);
        diff.VolumeDeltaPercent.Should().BeNull();
    }

    [Fact]
    public void OrientationInvariantSignatureAgreement_IdenticalOrRotated_IsOne()
    {
        string[] a = ["PLANE|0|0|1", "CYLINDER|0.005|0|0|1", "CYLINDER|0.003|1|0|0"];
        string[] rotated = ["PLANE|1|0|0", "CYLINDER|0.005|0|1|0", "CYLINDER|0.003|0|0|1"];

        AssemblyComponentMatcher.OrientationInvariantSignatureAgreement(a, a).Should().Be(1.0);
        // Same surfaces (a plane + two cylinders of the same radii) but different axes: the exact
        // signatures disagree, the orientation-invariant agreement must not.
        AssemblyComponentMatcher.OrientationInvariantSignatureAgreement(a, rotated).Should().Be(1.0);
    }

    [Fact]
    public void OrientationInvariantSignatureAgreement_DifferentShapes_IsLow()
    {
        string[] gasket =
            ["PLANE|0|0|1", "PLANE|0|0|1", "CYLINDER|0.05|0|0|1", "CYLINDER|0.002|0|0|1", "CYLINDER|0.002|0|0|1"];
        string[] bracket =
            ["PLANE|1|0|0", "PLANE|0|1|0", "PLANE|0|0|1", "PLANE|0|0|1", "CYLINDER|0.006|1|0|0"];

        AssemblyComponentMatcher.OrientationInvariantSignatureAgreement(gasket, bracket)
            .Should().BeLessThan(AssemblyDiffTolerances.Default.RenameSignatureAgreementThreshold);
    }

    [Fact]
    public void GeometryFallback_LowSignatureAgreement_NotPairedAsRename_WhenGated()
    {
        // Two differently-named parts with identical coarse stats (bbox/volume) but disagreeing
        // surfaces (10x cylinder radius) must NOT be paired as a rename once the signature gate is
        // on — they surface as Removed + Added, not a false Modified.
        var a = MakeComponentWithSig("OLD", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.005|0|0|1"]);
        var b = MakeComponentWithSig("NEW", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.050|0|0|1"]);
        var gated = _tol with { RenameSignatureAgreementThreshold = 0.65 };

        var summary = _matcher.Diff(Struct(a), Struct(b), gated, "A.step", "B.step");

        summary.Components.Should().Contain(d => d.MatchKey == "OLD" && d.DiffType == AssemblyDiffType.Removed);
        summary.Components.Should().Contain(d => d.MatchKey == "NEW" && d.DiffType == AssemblyDiffType.Added);
    }

    [Fact]
    public void GeometryFallback_HighSignatureAgreement_StillPaired_WhenGated()
    {
        // Same surfaces + radii, differently named and rotated: must still pair as a rename with the
        // gate on — the orientation-invariant check sees through the rotation.
        var a = MakeComponentWithSig("OLD", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.005|0|0|1"]);
        var b = MakeComponentWithSig("NEW", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|1|0|0", "CYLINDER|0.005|0|1|0"]);
        var gated = _tol with { RenameSignatureAgreementThreshold = 0.65 };

        var diff = _matcher.Diff(Struct(a), Struct(b), gated, "A.step", "B.step").Components.Single();

        diff.GeometricSimilarityScore.Should().NotBeNull(); // matched via fallback (rename)
        diff.ComponentA.Should().NotBeNull();
        diff.ComponentB.Should().NotBeNull();
    }

    [Fact]
    public void GeometryFallback_ThresholdZero_PairsRegardlessOfSignature()
    {
        // Setting the gate to 0 restores the pure coarse-similarity fallback — a low-signature pair
        // is still paired. Pins that the gate is a single tunable threshold, 0 = off.
        var a = MakeComponentWithSig("OLD", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.005|0|0|1"]);
        var b = MakeComponentWithSig("NEW", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.050|0|0|1"]);
        var ungated = _tol with { RenameSignatureAgreementThreshold = 0.0 };

        var diff = _matcher.Diff(Struct(a), Struct(b), ungated, "A.step", "B.step").Components.Single();

        diff.GeometricSimilarityScore.Should().NotBeNull();
    }

    [Fact]
    public void GeometryFallback_LowSignature_DefaultGate_NotPaired()
    {
        // The default tolerance now gates renames (0.65), so a low-agreement pair is split into
        // Removed + Added by default — no explicit tolerance needed.
        var a = MakeComponentWithSig("OLD", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.005|0|0|1"]);
        var b = MakeComponentWithSig("NEW", [0.01, 0.02, 0.03], 0.00001,
            ["PLANE|0|0|1", "CYLINDER|0.050|0|0|1"]);

        var summary = _matcher.Diff(Struct(a), Struct(b), _tol, "A.step", "B.step");

        summary.Components.Should().Contain(d => d.MatchKey == "OLD" && d.DiffType == AssemblyDiffType.Removed);
        summary.Components.Should().Contain(d => d.MatchKey == "NEW" && d.DiffType == AssemblyDiffType.Added);
    }

    [Fact]
    public void Output_IsDeterministicallyOrdered()
    {
        var removed = MakeComponent("Z-REMOVED", [0.01, 0.01, 0.01], 0.000001, faceCount: 6);
        var added = MakeComponent("A-ADDED", [0.9, 1.1, 1.3], 0.5, faceCount: 60);
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
