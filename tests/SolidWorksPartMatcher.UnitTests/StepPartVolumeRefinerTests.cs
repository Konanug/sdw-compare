using System.Text.Json;
using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Step;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// Exercises <see cref="StepPartVolumeRefiner"/> against the real, shipped OCCT tool and committed
/// known-volume STEP fixtures. When the tool (Python/build123d or the bundled exe) is unavailable
/// the refiner returns an empty map and the test passes vacuously — mirroring how the rest of the
/// suite handles OCCT/SW-COM-absent environments.
/// </summary>
public sealed class StepPartVolumeRefinerTests
{
    private static readonly string BoxFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "known-box.step");
    private static readonly string CylinderFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "known-cylinder.step");

    [Fact]
    public async Task RefineAsync_EmptyList_ReturnsEmpty_NoSubprocess()
    {
        (await StepPartVolumeRefiner.RefineAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task RefineAsync_KnownFixtures_ReturnsRealVolumes_OrVacuousWhenToolAbsent()
    {
        var result = await StepPartVolumeRefiner.RefineAsync([BoxFixture, CylinderFixture]);

        if (result.Count == 0) return; // OCCT tool unavailable in this environment — vacuous pass

        result.Should().ContainKey(BoxFixture);
        result[BoxFixture].VolumeM3.Should().BeApproximately(6e-06, 1e-9);          // 10×20×30 mm box

        result.Should().ContainKey(CylinderFixture);
        result[CylinderFixture].VolumeM3.Should()
            .BeApproximately(Math.PI * 25 * 20 * 1e-9, 1e-9);                        // r=5, h=20 mm
    }

    [Fact]
    public async Task RefineAsync_KnownBox_AlsoReturnsRealAreaAndBoundingBox_OrVacuousWhenToolAbsent()
    {
        // The whole point of the --with-bbox switch: the STEP fingerprint's bounding box and surface
        // area must come from the same kernel as its volume. Without this, both are pure functions of
        // the P21 point cloud's box, and StepEngravingDetector's size gates would pass vacuously.
        var result = await StepPartVolumeRefiner.RefineAsync([BoxFixture]);

        if (result.Count == 0) return; // OCCT tool unavailable in this environment — vacuous pass

        var box = result[BoxFixture];

        // 10×20×30 mm box → sorted ascending, in metres.
        box.BboxM.Should().NotBeNull();
        var bbox = box.BboxM!;
        bbox.Should().HaveCount(3);
        bbox[0].Should().BeApproximately(0.010, 1e-5);
        bbox[1].Should().BeApproximately(0.020, 1e-5);
        bbox[2].Should().BeApproximately(0.030, 1e-5);

        // 2(10×20 + 10×30 + 20×30) = 2200 mm² = 2.2e-3 m²
        box.AreaM2.Should().NotBeNull();
        box.AreaM2!.Value.Should().BeApproximately(2.2e-3, 1e-6);
    }

    [Fact]
    public void ParseMeasurements_WithBboxPayload_ReadsVolumeAreaAndBox()
    {
        const string json = """
            {"a.step": {"volume_m3": 6e-06, "area_m2": 0.0022, "bbox_m": [0.01, 0.02, 0.03]}}
            """;

        var result = StepPartVolumeRefiner.ParseMeasurements(json);

        result.Should().ContainKey("a.step");
        result["a.step"].VolumeM3.Should().BeApproximately(6e-06, 1e-12);
        result["a.step"].AreaM2!.Value.Should().BeApproximately(0.0022, 1e-9);
        result["a.step"].BboxM.Should().Equal(0.01, 0.02, 0.03);
    }

    [Fact]
    public void ParseMeasurements_NullEntryOrNonPositiveVolume_IsNotAMeasurement()
    {
        // A null entry means that one file failed to parse; a non-positive volume means non-solid
        // geometry. Neither is a measurement — the caller must keep its estimate rather than record
        // a zero, which would otherwise read as "this part vanished".
        const string json = """
            {"failed.step": null,
             "nonsolid.step": {"volume_m3": 0.0, "area_m2": null, "bbox_m": null},
             "good.step": {"volume_m3": 1e-06, "area_m2": null, "bbox_m": null}}
            """;

        var result = StepPartVolumeRefiner.ParseMeasurements(json);

        result.Should().ContainKey("good.step");
        result.Should().NotContainKey("failed.step");
        result.Should().NotContainKey("nonsolid.step");
    }

    [Fact]
    public void ParseMeasurements_BareFloatPayload_FromAToolPredatingWithBbox_YieldsNoMeasurements()
    {
        // An old bundled viewer exe ignores --with-bbox's contract. In practice it exits 2 (two
        // positional args) and never gets here — but if a payload of bare floats ever did arrive, it
        // must degrade to "no measurements", not throw and not be silently misread.
        var act = () => StepPartVolumeRefiner.ParseMeasurements("""{"a.step": 6e-06}""");

        act.Should().Throw<JsonException>(); // caught by RefineAsync → empty map → estimates kept
    }

    [Fact]
    public async Task RefineAsync_AlreadyCancelledToken_Throws_DoesNotSilentlyDegrade()
    {
        // A cancelled scan must abort, not fall back to estimates as if the tool were missing.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
            await StepPartVolumeRefiner.RefineAsync([BoxFixture], log: null, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
