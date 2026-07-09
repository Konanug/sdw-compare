using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Computes real CAD-kernel (OCCT) volumes for whole STEP part files, replacing the crude
/// <see cref="StepGeometryEstimator.EstimateVolume"/> estimate used for STEP fingerprints. Reuses
/// the same bundled tool as the assembly path (<c>tools/compute_component_volume.py</c>, which
/// computes a real volume for any STEP path and batches many via a manifest), but for whole part
/// files rather than assembly-component snippets — so it writes no snippets and touches no assembly
/// code.
///
/// Degrades gracefully: if the tool is missing (no Python/build123d, no bundled exe), the subprocess
/// errors, times out, or one file fails to parse, the affected files simply aren't in the returned
/// map and the caller keeps the existing estimate. The only exception it propagates is
/// <see cref="OperationCanceledException"/>, so a cancelled scan aborts promptly.
/// </summary>
public static class StepPartVolumeRefiner
{
    private const string ScriptName = "compute_component_volume";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Returns file path → real volume (m³) for every STEP file the tool could measure. Paths not
    /// present in the result should keep their existing (estimated) volume. Diagnostics go to
    /// <paramref name="log"/>. Cancelling <paramref name="ct"/> kills the subprocess (whole tree)
    /// and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<Dictionary<string, double>> RefineAsync(
        IReadOnlyList<string> stepFilePaths, Action<string>? log = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (stepFilePaths.Count == 0) return result;

        var (exe, script) = FindTool();
        if (string.IsNullOrEmpty(exe))
        {
            log?.Invoke("Real STEP volume unavailable (OCCT tool not found) — using bounding-box estimates.");
            return result;
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
            if (!ok) return result;

            Dictionary<string, double?>? volumesByPath;
            try { volumesByPath = JsonSerializer.Deserialize<Dictionary<string, double?>>(stdout); }
            catch (JsonException ex)
            {
                log?.Invoke($"Real STEP volume returned malformed output ({ex.Message}) — using estimates.");
                return result;
            }
            if (volumesByPath is null) return result;

            foreach (var (path, volume) in volumesByPath)
                if (volume is { } v && v > 0)
                    result[path] = v;
        }
        catch (OperationCanceledException)
        {
            throw; // the user cancelled the scan — do not silently degrade
        }
        catch (Exception ex)
        {
            log?.Invoke($"Real STEP volume failed ({ex.Message}) — using estimates.");
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        return result;
    }

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
        psi.ArgumentList.Add(manifestPath);

        using var process = Process.Start(psi);
        if (process is null)
        {
            log?.Invoke("Real STEP volume failed to start — using estimates.");
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
            log?.Invoke($"Real STEP volume timed out after {Timeout.TotalSeconds:F0}s — using estimates.");
            return (string.Empty, false);
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > 500 ? stderr[^500..] : stderr;
            log?.Invoke($"Real STEP volume exited with code {process.ExitCode} — using estimates.{(tail.Length > 0 ? $" ({tail.Trim()})" : "")}");
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
