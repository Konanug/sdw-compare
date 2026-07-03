using System.Text;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Step.Assembly;

/// <summary>
/// Writes a standalone valid ISO-10303-21 file containing only the entities in one assembly
/// component's entity closure — lets the existing single/multi-file 3D diff viewer
/// (<c>StepDiffWindow</c> / <c>tools/view_steps.py</c>) render just one part from an assembly,
/// unmodified.
/// </summary>
public static class StepComponentSnippetWriter
{
    // Encoding.UTF8 writes a UTF-8 BOM preamble by default, which OCCT's STEP lexer
    // (used by build123d/tools/view_steps.py) fails to parse — it misreads the file
    // from byte 0 and reports a confusing "unexpected QUID, expecting STEP" error.
    private static readonly UTF8Encoding NoBomUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteSnippet(StepP21Reader sourceReader, AssemblyComponent component, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("ISO-10303-21;");
        sb.AppendLine("HEADER;");
        sb.AppendLine($"FILE_DESCRIPTION((''),'2;1');");
        sb.AppendLine($"FILE_NAME('{EscapeString(component.ProductName)}','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',(''),(''),'','','');");
        sb.AppendLine($"FILE_SCHEMA(('{sourceReader.SchemaName ?? "CONFIG_CONTROL_DESIGN"}'));");
        sb.AppendLine("ENDSEC;");
        sb.AppendLine("DATA;");

        foreach (var id in component.EntityClosure.Order())
        {
            if (!sourceReader.TryGetRaw(id, out var raw)) continue;
            sb.Append('#').Append(id).Append('=').Append(raw).AppendLine(";");
        }

        sb.AppendLine("ENDSEC;");
        sb.AppendLine("END-ISO-10303-21;");

        File.WriteAllText(outputPath, sb.ToString(), NoBomUtf8);
    }

    public static string BuildTempPath(string componentDisplayName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SolidWorksPartMatcher");
        var safeName = SanitizeFileName(componentDisplayName);
        return Path.Combine(dir, $"{safeName}_{Guid.NewGuid():N}.step");
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "component";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "component" : result;
    }

    private static string EscapeString(string s) => s.Replace("'", "''");
}
