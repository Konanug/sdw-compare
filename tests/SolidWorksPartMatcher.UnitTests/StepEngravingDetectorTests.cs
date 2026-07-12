using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// <see cref="StepEngravingDetector"/> — Stage 3.7. A STEP file has no feature tree, so an engraved
/// twin has to be recognised from geometry alone: same bounding box, volume barely moved, far more
/// faces, and every base face still present.
///
/// The gates that matter most here are the ones that stop this becoming a false-positive generator:
/// the 0.5% volume limit, and the refusal to run at all on estimated (non-kernel) geometry.
/// </summary>
public sealed class StepEngravingDetectorTests
{
    private const string PlateBase = "PLANE|0|0|1";

    /// <summary>A plain rectangular plate: 6 planes + 4 mounting holes (cylinders).</summary>
    private static List<string> PlainPlateSignature()
    {
        var sig = new List<string>
        {
            "PLANE|0|0|1", "PLANE|0|0|1", "PLANE|0|1|0", "PLANE|0|1|0", "PLANE|1|0|0", "PLANE|1|0|0",
            "CYLINDER|0.003|0|0|1", "CYLINDER|0.003|0|0|1",
            "CYLINDER|0.003|0|0|1", "CYLINDER|0.003|0|0|1",
        };
        sig.Sort(StringComparer.Ordinal);
        return sig;
    }

    /// <summary>
    /// The same plate, engraved: every original face survives (an engraving changes a face's trim
    /// loops, not its surface) plus <paramref name="addedPlanes"/> letter walls/floors and
    /// <paramref name="addedCylinders"/> rounded strokes.
    /// </summary>
    private static List<string> EngravedPlateSignature(int addedPlanes = 60, int addedCylinders = 4)
    {
        var sig = PlainPlateSignature();
        for (int i = 0; i < addedPlanes; i++)
            sig.Add(i % 3 == 0 ? "PLANE|0|0|1" : i % 3 == 1 ? "PLANE|0.7071|0.7071|0" : "PLANE|1|0|0");
        for (int i = 0; i < addedCylinders; i++)
            sig.Add("CYLINDER|0.0004|0|0|1");
        sig.Sort(StringComparer.Ordinal);
        return sig;
    }

    private static PartFingerprint Step(
        List<string> signature,
        double vol = 1.0e-5,
        double area = 2.0e-3,
        double[]? bb = null,
        int? faces = null,
        int edges = 100,
        int verts = 70,
        string? geometrySource = "occt",
        string sourceFormat = "STEP",
        int bodies = 1) => new(
            Id: Guid.NewGuid(),
            ScannedFileId: Guid.NewGuid(),
            FileSha256: Guid.NewGuid().ToString("N"),
            ConfigName: "Default",
            ExtractorVersion: StepGeometryExtractor.Version,
            SolidBodyCount: bodies,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: bb ?? [0.010, 0.050, 0.080],
            VolumeM3: vol,
            SurfaceAreaM2: area,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: faces ?? signature.Count,
            EdgeCount: edges,
            VertexCount: verts,
            FeatureCount: 0,
            FeatureTypeHistogram: new Dictionary<string, int>(),
            Material: null,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: string.Empty,
            ExtractorVersionLabel: StepGeometryExtractor.VersionLabel,
            ExtractedUtc: DateTime.UtcNow,
            ChiralitySign: null,
            CoMOffsetInBB: null,
            SketchTextCutCount: 0,
            SuppressedSolidBodyCount: null,
            SuppressedBoundingBoxM: null,
            SuppressedVolumeM3: null,
            SuppressedSurfaceAreaM2: null,
            SuppressedFaceCount: null,
            SuppressedEdgeCount: null,
            SuppressedVertexCount: null,
            SourceFormat: sourceFormat,
            FaceGeometricSignature: signature,
            GeometrySource: geometrySource);

    private static StepEngravingDetector.Result Detect(PartFingerprint a, PartFingerprint b)
        => StepEngravingDetector.Detect(a, b, StepEngravingTolerances.Default);

    // ── The happy path ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SameBox_TinyVolumeDelta_ManyMoreFaces_BaseFacesPreserved_IsEngravingVariant()
    {
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, area: 2.0e-3, edges: 24, verts: 16);
        // A shallow etch: removes ~0.02% of the material, adds ~0.8% surface area (letter walls).
        var engraved = Step(EngravedPlateSignature(), vol: 0.9998e-5, area: 2.016e-3,
            edges: 300, verts: 210);

        var result = Detect(plain, engraved);

        result.IsEngraving.Should().BeTrue(because: result.Reason);
        result.Reason.Should().Contain("Engraving variant");
    }

    [Fact]
    public void Detect_IsSymmetricInArgumentOrder()
    {
        // The greedy multiset matcher is order-sensitive by construction, and the orchestrator has no
        // defined A/B ordering — so the verdict must not depend on which side is passed first.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, area: 2.0e-3, edges: 24, verts: 16);
        var engraved = Step(EngravedPlateSignature(), vol: 0.9998e-5, area: 2.016e-3,
            edges: 300, verts: 210);

        Detect(plain, engraved).IsEngraving.Should().Be(Detect(engraved, plain).IsEngraving);
    }

    [Fact]
    public void HostFaceSplitByTheCut_IncreasingPlaneMultiplicity_StillContained()
    {
        // A cut can split the face it lands on into several coplanar fragments. Each fragment emits
        // the SAME descriptor, so the descriptor's multiplicity rises — containment must still hold.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, edges: 24, verts: 16);
        var engraved = Step(EngravedPlateSignature(addedPlanes: 40, addedCylinders: 0),
            vol: 0.9999e-5, area: 2.01e-3, edges: 200, verts: 140);

        Detect(plain, engraved).IsEngraving.Should().BeTrue();
    }

    // ── The gates that prevent false positives ───────────────────────────────────────────────────

    [Fact]
    public void EstimatedGeometry_NotKernelMeasured_IsRejected_EvenThoughEveryNumericGatePasses()
    {
        // THE important test. Without OCCT, StepGeometryEstimator's volume (0.55 × bbVolume) and
        // surface area (the box formula) are PURE FUNCTIONS OF THE BOUNDING BOX. So two parts that
        // merely share a box get bit-identical volume AND area, and the box/volume/area gates all
        // pass VACUOUSLY — the detector would collapse to "same box + more faces" and merge
        // genuinely different parts. It must refuse to run at all.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, area: 2.0e-3, geometrySource: "step-estimate");
        var engraved = Step(EngravedPlateSignature(), vol: 1.0e-5, area: 2.0e-3, geometrySource: "step-estimate");

        var result = Detect(plain, engraved);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("estimated");
    }

    [Fact]
    public void CachedFingerprintWithNoGeometrySource_IsTreatedAsEstimated_AndRejected()
    {
        // A pre-v9 cached row reads back GeometrySource = null. We do not know how its geometry was
        // obtained, so it must not be trusted as kernel-measured.
        var plain = Step(PlainPlateSignature(), geometrySource: null);
        var engraved = Step(EngravedPlateSignature(), vol: 0.9998e-5, geometrySource: null);

        Detect(plain, engraved).IsEngraving.Should().BeFalse();
    }

    [Fact]
    public void VolumeDeltaAboveHalfAPercent_IsNotAnEngraving()
    {
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, edges: 24, verts: 16);
        var engraved = Step(EngravedPlateSignature(), vol: 0.99e-5, edges: 300, verts: 210); // −1%

        var result = Detect(plain, engraved);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("volume");
    }

    [Fact]
    public void BoundingBoxDifferingByMoreThanHalfAMillimetre_IsNotAnEngraving()
    {
        var plain = Step(PlainPlateSignature(), bb: [0.010, 0.050, 0.080], edges: 24, verts: 16);
        var engraved = Step(EngravedPlateSignature(), vol: 0.9998e-5,
            bb: [0.010, 0.050, 0.081], edges: 300, verts: 210); // 1 mm longer

        var result = Detect(plain, engraved);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("bounding box");
    }

    [Fact]
    public void OnlyTwoExtraFaces_ASingleDrilledHole_IsNotAnEngraving()
    {
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, edges: 24, verts: 16);
        var withHole = Step(EngravedPlateSignature(addedPlanes: 0, addedCylinders: 2),
            vol: 0.9995e-5, edges: 30, verts: 20);

        var result = Detect(plain, withHole);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("too few");
    }

    [Fact]
    public void AddedFacesAreMostlyCurved_APerforationPattern_IsNotAnEngraving()
    {
        // A grid of small holes clears the bounding-box, volume, face-count AND containment gates —
        // the base faces are all still there and it has far more faces. Only the composition of the
        // ADDED faces separates it from an engraving: holes are cylinders, letters are planes.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, edges: 24, verts: 16);
        var perforated = Step(EngravedPlateSignature(addedPlanes: 2, addedCylinders: 30),
            vol: 0.9995e-5, area: 2.05e-3, edges: 200, verts: 140);

        var result = Detect(plain, perforated);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("curved");
        result.NearMiss.Should().BeTrue(because: "it cleared every size gate — worth logging");
    }

    [Fact]
    public void BaseFacesNotContained_DifferentShapeEntirely_IsNotAnEngraving()
    {
        // Same box, same volume, many more faces — but the base surfaces are nowhere to be found.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, edges: 24, verts: 16);

        var alien = new List<string>();
        for (int i = 0; i < 80; i++) alien.Add($"TORUS|0.00{i % 9 + 1}|0.0002");
        alien.Sort(StringComparer.Ordinal);
        var other = Step(alien, vol: 0.9999e-5, edges: 300, verts: 210);

        var result = Detect(plain, other);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("base shapes differ");
        result.NearMiss.Should().BeTrue();
    }

    [Fact]
    public void SurfaceAreaShrinksOnTheManyFacesSide_IsNotAnEngraving()
    {
        // An engraving ADDS surface (letter side-walls). Area going down while face count goes up is
        // some other transformation, not a marking.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, area: 2.0e-3, edges: 24, verts: 16);
        var shrunk = Step(EngravedPlateSignature(), vol: 0.9998e-5, area: 1.9e-3,
            edges: 300, verts: 210);

        var result = Detect(plain, shrunk);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("surface area");
    }

    [Fact]
    public void EdgeCountDecreasesOnTheManyFacesSide_IsNotAnEngraving()
    {
        // Adding a feature never removes edges.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, edges: 400, verts: 300);
        var fewerEdges = Step(EngravedPlateSignature(), vol: 0.9998e-5, edges: 30, verts: 20);

        var result = Detect(plain, fewerEdges);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("FEWER edges");
    }

    [Fact]
    public void UnmeasurableVolume_NonSolidGeometry_IsNotAnEngraving()
    {
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5);
        var nonSolid = Step(EngravedPlateSignature(), vol: 0.0);

        var result = Detect(plain, nonSolid);

        result.IsEngraving.Should().BeFalse();
        result.Reason.Should().Contain("unmeasurable");
    }

    [Fact]
    public void EqualFaceCounts_IsNotAnEngraving_ThatIsStage35sJob()
    {
        var sig = PlainPlateSignature();
        var a = Step(sig, vol: 1.0e-5);
        var b = Step(sig, vol: 1.0e-5);

        Detect(a, b).IsEngraving.Should().BeFalse();
    }

    [Fact]
    public void DifferentBodyCount_IsNotAnEngraving()
    {
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, bodies: 1);
        var engraved = Step(EngravedPlateSignature(), vol: 0.9998e-5, bodies: 2);

        Detect(plain, engraved).IsEngraving.Should().BeFalse();
    }

    [Fact]
    public void CrossFormat_SldprtVsStep_IsNotAnEngraving()
    {
        // Stage 3.7 is STEP-only: SLDPRT has a real feature tree and its own suppression-based
        // engraving check, which is strictly better evidence than this geometric inference.
        var sldprt = Step(PlainPlateSignature(), vol: 1.0e-5, sourceFormat: "SLDPRT");
        var step = Step(EngravedPlateSignature(), vol: 0.9998e-5);

        Detect(sldprt, step).IsEngraving.Should().BeFalse();
    }

    [Fact]
    public void UnrelatedParts_AreNotEvenNearMisses_SoTheyStayOutOfTheScanLog()
    {
        // The scan log must only carry pairs that genuinely looked like an engraving. Two unrelated
        // parts differ on a size gate and must not be reported.
        var plain = Step(PlainPlateSignature(), vol: 1.0e-5, bb: [0.010, 0.050, 0.080]);
        var unrelated = Step(EngravedPlateSignature(), vol: 5.0e-5, bb: [0.030, 0.070, 0.120]);

        var result = Detect(plain, unrelated);

        result.IsEngraving.Should().BeFalse();
        result.NearMiss.Should().BeFalse();
    }
}
