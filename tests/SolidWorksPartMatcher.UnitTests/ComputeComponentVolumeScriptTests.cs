using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// Shells out to the real, shipped <c>tools/compute_component_volume.py</c> against committed
/// known-volume STEP fixtures — validates the actual artifact that ships, not a re-implementation
/// of its logic. Skipped (vacuously passing) when Python/build123d isn't available on the
/// machine, mirroring how SW-COM-unavailable behavior is already handled elsewhere.
/// </summary>
public sealed class ComputeComponentVolumeScriptTests
{
    private static readonly string BoxFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "known-box.step");
    private static readonly string CylinderFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "known-cylinder.step");

    private static readonly Lazy<bool> PythonAvailable = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import build123d");
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    });

    [Fact]
    public void Script_ComputesKnownVolumes_AndReturnsNullForMissingFile()
    {
        if (!PythonAvailable.Value) return; // environment without Python/build123d

        string missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.step");
        string manifestPath = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(new { paths = new[] { BoxFixture, CylinderFixture, missing } }));

        try
        {
            var scriptPath = Path.Combine(FindRepoRoot(), "tools", "compute_component_volume.py");
            File.Exists(scriptPath).Should().BeTrue("the script must exist at the expected path");

            var psi = new ProcessStartInfo("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(manifestPath);

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000).Should().BeTrue("the script should finish well within 60s");
            process.ExitCode.Should().Be(0, $"stderr was: {stderr}");

            var result = JsonSerializer.Deserialize<Dictionary<string, double?>>(stdout)!;

            result[BoxFixture].Should().NotBeNull();
            result[BoxFixture]!.Value.Should().BeApproximately(6e-06, 1e-12); // exact 10x20x30mm box

            result[CylinderFixture].Should().NotBeNull();
            result[CylinderFixture]!.Value.Should().BeApproximately(Math.PI * 25 * 20 * 1e-9, 1e-12); // r=5,h=20mm

            result[missing].Should().BeNull();
        }
        finally
        {
            try { File.Delete(manifestPath); } catch { /* best effort */ }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SolidWorksPartMatcher.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (SolidWorksPartMatcher.sln) from test output directory.");
    }
}

/// <summary>Basic contract checks on <see cref="OcctVolumeRefiner"/> that don't need real STEP data.</summary>
public sealed class OcctVolumeRefinerTests
{
    [Fact]
    public void Refine_EmptyComponentList_ReturnsEmptyDictionary_NoIO()
    {
        var result = OcctVolumeRefiner.Refine(
            SolidWorksPartMatcher.Infrastructure.Step.StepP21Reader.ParseText(
                "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;"),
            [],
            new List<string>());

        result.Should().BeEmpty();
    }
}
