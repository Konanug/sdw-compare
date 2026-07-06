using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;

namespace SolidWorksPartMatcher.Infrastructure.Assembly;

/// <summary>
/// Replaces the heuristic 55%-of-bounding-box volume estimate
/// (<see cref="StepGeometryEstimator.EstimateVolume"/>'s non-cylinder fallback) with a real
/// CAD-kernel volume, computed by a small batch Python/OCCT subprocess
/// (<c>tools/compute_component_volume.py</c>), for every <see cref="AssemblyComponent"/> in one
/// STEP assembly file.
///
/// Deliberately narrow: unlike an earlier, reverted full-pipeline rewrite, this touches nothing
/// about structure parsing, instance counting, matching, or placement — it only slices each
/// component's already-resolved <see cref="AssemblyComponent.EntityClosure"/> into a standalone
/// STEP snippet (via the existing, unmodified <see cref="StepComponentSnippetWriter"/>) and asks
/// OCCT for that one part's real volume. Any failure — Python/build123d missing, the subprocess
/// erroring, or one specific component's snippet failing to parse — degrades to leaving that
/// component's existing heuristic volume in place, with a warning; it never fails the comparison.
/// </summary>
public static class OcctVolumeRefiner
{
    private const string ScriptName = "compute_component_volume";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Returns ProductId → real volume (m³) for every component whose snippet OCCT could parse.
    /// Components not present in the result should keep their existing (heuristic) volume.
    /// Warnings are appended to <paramref name="warnings"/>, never thrown.
    /// </summary>
    public static Dictionary<string, double> Refine(
        StepP21Reader reader, IReadOnlyList<AssemblyComponent> components, List<string> warnings)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (components.Count == 0) return result;

        var (exe, script) = FindTool();
        if (string.IsNullOrEmpty(exe))
        {
            warnings.Add(
                "Real-volume refinement unavailable (OCCT component not found) — using the " +
                "bounding-box volume estimate for all components in this file.");
            return result;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "SolidWorksPartMatcher", "VolumeRefine", Guid.NewGuid().ToString("N"));
        var pathToProductId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (var component in components)
            {
                string snippetPath = Path.Combine(tempDir, $"{SanitizeFileName(component.ProductId)}.step");
                StepComponentSnippetWriter.WriteSnippet(reader, component, snippetPath);
                pathToProductId[snippetPath] = component.ProductId;
            }

            string manifestPath = Path.Combine(tempDir, "manifest.json");
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(new { paths = pathToProductId.Keys.ToArray() }));

            string stdout = RunTool(exe, script, manifestPath, warnings, out bool succeeded);
            if (!succeeded) return result; // warning already appended by RunTool

            Dictionary<string, double?>? volumesByPath;
            try
            {
                volumesByPath = JsonSerializer.Deserialize<Dictionary<string, double?>>(stdout);
            }
            catch (JsonException ex)
            {
                warnings.Add(
                    $"Real-volume refinement returned malformed output ({ex.Message}) — using " +
                    "the bounding-box volume estimate for all components in this file.");
                return result;
            }
            if (volumesByPath is null) return result;

            int missing = 0;
            foreach (var (path, productId) in pathToProductId)
            {
                if (volumesByPath.TryGetValue(path, out var volume) && volume is { } v)
                    result[productId] = v;
                else
                    missing++;
            }
            if (missing > 0)
                warnings.Add(
                    $"Real-volume refinement could not compute {missing} of {components.Count} " +
                    "component(s) — those keep the bounding-box volume estimate instead.");
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"Real-volume refinement failed ({ex.Message}) — using the bounding-box volume " +
                "estimate for all components in this file.");
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        return result;
    }

    private static string RunTool(
        string exe, string? script, string manifestPath, List<string> warnings, out bool succeeded)
    {
        succeeded = false;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        if (script is not null) psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(manifestPath);

        using var process = Process.Start(psi);
        if (process is null)
        {
            warnings.Add(
                "Real-volume refinement failed to start — using the bounding-box volume " +
                "estimate for all components in this file.");
            return string.Empty;
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        bool exited = process.WaitForExit((int)Timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            warnings.Add(
                $"Real-volume refinement timed out after {Timeout.TotalSeconds:F0}s — using the " +
                "bounding-box volume estimate for all components in this file.");
            return string.Empty;
        }
        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > 500 ? stderr[^500..] : stderr;
            warnings.Add(
                $"Real-volume refinement exited with code {process.ExitCode} — using the " +
                $"bounding-box volume estimate for all components in this file.{(tail.Length > 0 ? $" ({tail.Trim()})" : "")}");
            return string.Empty;
        }

        succeeded = true;
        return stdout;
    }

    // Bundled-exe-vs-dev-script resolution, self-contained (deliberately not shared with
    // StepDiffWindow's own viewer-locating logic, to avoid touching that working code at all).
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

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "component" : result;
    }
}
