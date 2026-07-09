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
        result[BoxFixture].Should().BeApproximately(6e-06, 1e-9);          // 10×20×30 mm box

        result.Should().ContainKey(CylinderFixture);
        result[CylinderFixture].Should().BeApproximately(Math.PI * 25 * 20 * 1e-9, 1e-9); // r=5, h=20 mm
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
