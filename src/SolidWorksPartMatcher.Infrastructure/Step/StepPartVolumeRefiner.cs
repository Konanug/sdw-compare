using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SolidWorksPartMatcher.Infrastructure.Assembly;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Measures whole STEP part files with the real CAD kernel (OCCT), replacing
/// <see cref="StepGeometryEstimator"/>'s crude estimates in a STEP fingerprint. Reuses the same
/// bundled tool as the assembly path (<c>tools/compute_component_volume.py</c>, which measures any
/// STEP path and batches many via a manifest), but for whole part files rather than
/// assembly-component snippets — so it writes no snippets and touches no assembly code.
///
/// Returns volume, surface area AND a tight, rotation-invariant oriented bounding box (the tool's
/// <c>--with-bbox</c> mode). All three matter: the estimator's volume (0.55 × bbVolume) and surface
/// area (the box formula) are pure functions of the bounding box, so without the kernel two
/// different parts sharing a box get bit-identical volume and area — see
/// <see cref="Domain.Models.PartFingerprint.GeometrySource"/>.
///
/// Degrades gracefully: if the tool is missing (no Python/build123d, no bundled exe), the subprocess
/// errors, times out, or one file fails to parse, the affected files simply aren't in the returned
/// map and the caller keeps the existing estimate. That includes an OLD bundled exe that predates
/// <c>--with-bbox</c>: it sees two positional args, exits 2, and we degrade like any other failure.
/// The only exception it propagates is <see cref="OperationCanceledException"/>, so a cancelled scan
/// aborts promptly.
/// </summary>
public static class StepPartVolumeRefiner
{
    private const string ScriptName = "compute_component_volume";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Returns file path → real OCCT measurements for every STEP file the tool could measure. Paths
    /// not present in the result should keep their existing (estimated) geometry. Diagnostics go to
    /// <paramref name="log"/>. Cancelling <paramref name="ct"/> kills the subprocess (whole tree)
    /// and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<Dictionary<string, OcctVolumeRefiner.OcctMeasurement>> RefineAsync(
        IReadOnlyList<string> stepFilePaths, Action<string>? log = null, CancellationToken ct = default)
    {
        var empty = new Dictionary<string, OcctVolumeRefiner.OcctMeasurement>(StringComparer.Ordinal);
        if (stepFilePaths.Count == 0) return empty;

        // Honour cancellation before doing any work, so an already-cancelled scan aborts even when
        // the OCCT tool is absent (which would otherwise return early and look like a clean degrade).
        ct.ThrowIfCancellationRequested();

        var (exe, script) = FindTool();
        if (string.IsNullOrEmpty(exe))
        {
            log?.Invoke("Real STEP geometry unavailable (OCCT tool not found) — using bounding-box estimates.");
            return empty;
        }

        string tempDir = Path.Combine(
            Path.GetTempPath(), "SolidWorksPartMatcher", "StepVolume", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string manifestPath = Path.Combine(tempDir, "manifest.json");
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(new { paths = stepFilePaths.ToArray() }));

            var (stdout, ok) = await RunToolAsync(exe, script, manifestPath, log, ct);
            if (!ok) return empty;

            try { return ParseMeasurements(stdout); }
            catch (JsonException ex)
            {
                log?.Invoke($"Real STEP geometry returned malformed output ({ex.Message}) — using estimates.");
                return empty;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // the user cancelled the scan — do not silently degrade
        }
        catch (Exception ex)
        {
            log?.Invoke($"Real STEP geometry failed ({ex.Message}) — using estimates.");
            return empty;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Parses the tool's <c>--with-bbox</c> payload: <c>{path: {volume_m3, area_m2, bbox_m} | null}</c>.
    /// Only entries with a positive volume are kept — a null entry (that one file failed) or a
    /// non-positive volume (non-solid geometry) is not a measurement. Separated from the subprocess
    /// plumbing so the wire contract is testable without spawning anything.
    /// </summary>
    internal static Dictionary<string, OcctVolumeRefiner.OcctMeasurement> ParseMeasurements(string stdout)
    {
        var result = new Dictionary<string, OcctVolumeRefiner.OcctMeasurement>(StringComparer.Ordinal);
        var byPath = JsonSerializer.Deserialize<Dictionary<string, RichMeasurement?>>(stdout);
        if (byPath is null) return result;

        foreach (var (path, m) in byPath)
            if (m is { } v && v.VolumeM3 > 0)
                result[path] = new OcctVolumeRefiner.OcctMeasurement(v.VolumeM3, v.AreaM2, v.BboxM);

        return result;
    }

    // Wire DTO for the --with-bbox JSON payload. Kept private and local rather than shared with
    // OcctVolumeRefiner's identical DTO: this is a serialization contract, and coupling the two
    // would mean a change made for the assembly path silently alters how part files deserialize.
    private sealed record RichMeasurement(
        [property: System.Text.Json.Serialization.JsonPropertyName("volume_m3")] double VolumeM3,
        [property: System.Text.Json.Serialization.JsonPropertyName("area_m2")] double? AreaM2,
        [property: System.Text.Json.Serialization.JsonPropertyName("bbox_m")] double[]? BboxM);

    private static async Task<(string Stdout, bool Succeeded)> RunToolAsync(
        string exe, string? script, string manifestPath, Action<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (script is not null) psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--with-bbox");
        psi.ArgumentList.Add(manifestPath);

        using var process = Process.Start(psi);
        if (process is null)
        {
            log?.Invoke("Real STEP geometry failed to start — using estimates.");
            return (string.Empty, false);
        }

        // Drain both pipes concurrently. Reading stdout to completion first can deadlock: the child
        // writes per-file diagnostics to stderr, and once that pipe's buffer fills the child blocks
        // on write while we block on stdout — neither side ever progresses, and the timeout path
        // below is never reached because WaitForExit is never called.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* pipes torn down */ }

            if (ct.IsCancellationRequested) throw; // user cancelled the scan
            log?.Invoke($"Real STEP geometry timed out after {Timeout.TotalSeconds:F0}s — using estimates.");
            return (string.Empty, false);
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > 500 ? stderr[^500..] : stderr;
            log?.Invoke($"Real STEP geometry exited with code {process.ExitCode} — using estimates.{(tail.Length > 0 ? $" ({tail.Trim()})" : "")}");
            return (string.Empty, false);
        }

        return (stdout, true);
    }

    // Bundled-exe-vs-dev-script resolution (same convention as OcctVolumeRefiner.FindTool; kept
    // self-contained to avoid coupling to that assembly-specific class).
    private static (string Exe, string? Script) FindTool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string bundled = Path.Combine(dir.FullName, "viewer", ScriptName + ".exe");
            if (File.Exists(bundled)) return (bundled, null);

            string devScript = Path.Combine(dir.FullName, "tools", ScriptName + ".py");
            if (File.Exists(devScript)) return ("python", devScript);

            dir = dir.Parent;
        }
        return (string.Empty, null);
    }
}
