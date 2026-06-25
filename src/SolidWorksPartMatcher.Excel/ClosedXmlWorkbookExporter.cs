using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Excel;

public sealed class ClosedXmlWorkbookExporter(ILogger<ClosedXmlWorkbookExporter> logger) : IWorkbookExporter
{
    public Task ExportAsync(ExportContext ctx, string outputPath, CancellationToken ct)
    {
        logger.LogInformation("Exporting workbook to {Path}", outputPath);

        using var wb = new XLWorkbook();

        AddMatchesSheet(wb, ctx);
        AddAllFilesSheet(wb, ctx);
        AddComparisonDetailsSheet(wb, ctx);
        AddScanInfoSheet(wb, ctx);

        try { wb.SaveAs(outputPath); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save workbook to {Path}", outputPath);
            throw;
        }

        logger.LogInformation("Workbook saved to {Path}", outputPath);
        return Task.CompletedTask;
    }

    // ── Sheet 1: Matches ─────────────────────────────────────────────────────
    // One row per matched group (2+ files). GUIDs removed; filenames are the identity.

    private static void AddMatchesSheet(XLWorkbook wb, ExportContext ctx)
    {
        var fileById = ctx.Files.ToDictionary(f => f.Id);
        var fpById   = ctx.Fingerprints.ToDictionary(f => f.Id);

        // Build (cluster, orderedFiles, memberFingerprints) only for groups with ≥2 files
        var matchedGroups = ctx.Clusters
            .OrderBy(c => c.CanonicalName)
            .Select(c =>
            {
                var memberFps = ctx.Members
                    .Where(m => m.ClusterId == c.Id)
                    .Select(m => fpById.TryGetValue(m.FingerprintId, out var fp) ? fp : null)
                    .Where(fp => fp != null).Select(fp => fp!)
                    .ToList();

                var memberFiles = memberFps
                    .Select(fp => fileById.TryGetValue(fp.ScannedFileId, out var f) ? f : null)
                    .Where(f => f != null).Select(f => f!)
                    .OrderBy(f => f.FileName)
                    .ToList();

                return (Cluster: c, Files: memberFiles, Fps: memberFps);
            })
            .Where(g => g.Files.Count >= 2)
            .ToList();

        int maxFiles = matchedGroups.Count == 0 ? 2 : matchedGroups.Max(g => g.Files.Count);
        maxFiles = Math.Max(maxFiles, 2);

        var ws = wb.Worksheets.Add("Matches");

        // Fixed leading columns
        int col = 1;
        ws.Cell(1, col++).Value = "#";
        ws.Cell(1, col++).Value = "Match Type";
        ws.Cell(1, col++).Value = "# Files";
        int firstFileCol = col;
        for (int i = 1; i <= maxFiles; i++)
        {
            ws.Cell(1, col++).Value = $"File {i} Name";
            ws.Cell(1, col++).Value = $"File {i} Path";
        }
        ws.Cell(1, col++).Value = "Notes";
        int totalCols = col - 1;

        StyleHeader(ws, 1, totalCols);

        int row = 2;
        int seq = 1;
        foreach (var (cluster, files, fps) in matchedGroups)
        {
            col = 1;
            ws.Cell(row, col++).Value = seq++;
            ws.Cell(row, col++).Value = HumanType(cluster.Classification);
            ws.Cell(row, col++).Value = files.Count;

            foreach (var f in files)
            {
                ws.Cell(row, col++).Value = f.FileName;
                ws.Cell(row, col++).Value = f.NormalizedPath;
            }
            // Pad empty file columns so Notes lands in the right column
            col = firstFileCol + maxFiles * 2;
            ws.Cell(row, col++).Value = BuildNotes(fps, cluster.Classification);

            row++;
        }

        if (row > 2)
        {
            var tbl = ws.Range(1, 1, row - 1, totalCols).CreateTable("Matches");
            tbl.Theme = XLTableTheme.TableStyleMedium2;
            tbl.ShowAutoFilter = true;
        }

        ws.Columns(1, totalCols).AdjustToContents();
        CapColumnWidths(ws, totalCols, 70);
        ws.Column(1).Width = 5;
        ws.Column(2).Width = Math.Max(ws.Column(2).Width, 26);
        ws.Column(3).Width = 8;
    }

    // ── Sheet 2: All Files ───────────────────────────────────────────────────
    // One row per scanned file with its group membership and key dimensions.

    private static void AddAllFilesSheet(XLWorkbook wb, ExportContext ctx)
    {
        var ws = wb.Worksheets.Add("All Files");
        var headers = new[]
        {
            "File Name", "Full Path", "Match Type", "Matched With",
            "Material", "Length (mm)", "Width (mm)", "Height (mm)", "Volume (cm³)"
        };
        WriteHeaders(ws, headers);

        var fpById       = ctx.Fingerprints.ToDictionary(f => f.Id);
        var fpByFileId   = ctx.Fingerprints.ToDictionary(f => f.ScannedFileId);
        var clusterByFp  = ctx.Members.ToDictionary(m => m.FingerprintId, m => m.ClusterId);
        var clusterById  = ctx.Clusters.ToDictionary(c => c.Id);
        var membersByCluster = ctx.Members
            .GroupBy(m => m.ClusterId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var fileById = ctx.Files.ToDictionary(f => f.Id);

        int row = 2;
        foreach (var file in ctx.Files.OrderBy(f => f.FileName))
        {
            fpByFileId.TryGetValue(file.Id, out var fp);
            Guid clusterId = default;
            if (fp != null) clusterByFp.TryGetValue(fp.Id, out clusterId);
            clusterById.TryGetValue(clusterId, out var cluster);

            // Sibling filenames in same cluster
            string matchedWith = "—";
            if (cluster != null && membersByCluster.TryGetValue(cluster.Id, out var siblings))
            {
                var names = siblings
                    .Where(m => m.FingerprintId != fp?.Id)
                    .Select(m =>
                    {
                        fpById.TryGetValue(m.FingerprintId, out var sibFp);
                        fileById.TryGetValue(sibFp?.ScannedFileId ?? Guid.Empty, out var sibFile);
                        return sibFile?.FileName ?? "";
                    })
                    .Where(n => n.Length > 0)
                    .Distinct()
                    .OrderBy(n => n);
                var joined = string.Join(", ", names);
                if (joined.Length > 0) matchedWith = joined;
            }

            ws.Cell(row, 1).Value = file.FileName;
            ws.Cell(row, 2).Value = file.NormalizedPath;
            ws.Cell(row, 3).Value = cluster != null ? HumanType(cluster.Classification) : "Unique";
            ws.Cell(row, 4).Value = matchedWith;
            ws.Cell(row, 5).Value = fp?.Material ?? "";

            var bb = fp?.SortedBoundingBoxM ?? [];
            if (bb.Length > 0) ws.Cell(row, 6).Value = Math.Round(bb[0] * 1000.0, 2);
            if (bb.Length > 1) ws.Cell(row, 7).Value = Math.Round(bb[1] * 1000.0, 2);
            if (bb.Length > 2) ws.Cell(row, 8).Value = Math.Round(bb[2] * 1000.0, 2);
            if (fp != null)    ws.Cell(row, 9).Value = Math.Round(fp.VolumeM3 * 1e6, 2);

            row++;
        }

        FormatAsTable(ws, "AllFiles", 1, row - 1, headers.Length);
    }

    // ── Sheet 3: Comparison Details ──────────────────────────────────────────
    // Pair-level audit trail using filenames rather than GUIDs.

    private static void AddComparisonDetailsSheet(XLWorkbook wb, ExportContext ctx)
    {
        var ws = wb.Worksheets.Add("Comparison Details");
        var headers = new[]
        {
            "File A", "File B", "Score", "Result", "Reason"
        };
        WriteHeaders(ws, headers);

        var fpById   = ctx.Fingerprints.ToDictionary(f => f.Id);
        var fileById = ctx.Files.ToDictionary(f => f.Id);
        int row = 2;

        foreach (var pair in ctx.Pairs.OrderByDescending(p => p.CoarseScore))
        {
            fpById.TryGetValue(pair.FingerprintAId, out var fpA);
            fpById.TryGetValue(pair.FingerprintBId, out var fpB);
            fileById.TryGetValue(fpA?.ScannedFileId ?? Guid.Empty, out var fileA);
            fileById.TryGetValue(fpB?.ScannedFileId ?? Guid.Empty, out var fileB);

            ws.Cell(row, 1).Value = fileA?.FileName ?? "?";
            ws.Cell(row, 2).Value = fileB?.FileName ?? "?";
            ws.Cell(row, 3).Value = Math.Round(pair.CoarseScore, 3);
            ws.Cell(row, 4).Value = HumanType(pair.Classification);
            ws.Cell(row, 5).Value = pair.ClassificationReason ?? "";

                row++;
        }

        FormatAsTable(ws, "ComparisonDetails", 1, row - 1, headers.Length);
    }

    // ── Sheet 4: Scan Info ───────────────────────────────────────────────────

    private static void AddScanInfoSheet(XLWorkbook wb, ExportContext ctx)
    {
        var ws = wb.Worksheets.Add("Scan Info");
        ws.Cell(1, 1).Value = "Item";
        ws.Cell(1, 2).Value = "Value";
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
        ws.Row(1).Style.Font.FontColor = XLColor.White;

        int matched      = ctx.Clusters.Count(c => c.Classification != PartClassification.Distinct);
        int identical    = ctx.Clusters.Count(c => c.Classification == PartClassification.BinaryDuplicate);
        int sameGeo      = ctx.Clusters.Count(c => c.Classification == PartClassification.ExactGeometryMatch);
        int matVariant   = ctx.Clusters.Count(c => c.Classification == PartClassification.GeometryMatchMetadataVariant);
        int mirror       = ctx.Clusters.Count(c => c.Classification == PartClassification.MirrorOrHandedVariant);
        int revision     = ctx.Clusters.Count(c => c.Classification == PartClassification.RevisionFamily);
        int engraving    = ctx.Clusters.Count(c => c.Classification == PartClassification.EngravingVariant);
        int review       = ctx.Clusters.Count(c => c.Classification == PartClassification.PossibleMatch);

        var kvs = new List<(string, string)>
        {
            ("Scan Date",                           ctx.Run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            ("Folders Scanned",                     string.Join("; ", ctx.Run.SourceRoots)),
            ("Total Files Scanned",                 ctx.Files.Count.ToString()),
            ("Matched Groups",                      matched.ToString()),
            ("  — Identical Copies",                identical.ToString()),
            ("  — Same Geometry",                   sameGeo.ToString()),
            ("  — Same Geometry, Diff. Material",   matVariant.ToString()),
            ("  — Mirror / Handed",                 mirror.ToString()),
            ("  — Revision Family",                 revision.ToString()),
            ("  — Engraving Difference",            engraving.ToString()),
            ("  — Possible Match",                  review.ToString()),
            ("Unique Parts (no match)",             ctx.Clusters.Count(c => c.Classification == PartClassification.Distinct).ToString()),
            ("Report Generated",                    DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
        };

        for (int i = 0; i < kvs.Count; i++)
        {
            ws.Cell(i + 2, 1).Value = kvs[i].Item1;
            ws.Cell(i + 2, 2).Value = kvs[i].Item2;
            ws.Cell(i + 2, 1).Style.Font.Bold = true;
        }

        ws.Column(1).Width = 28;
        ws.Column(2).Width = 70;
    }

    // ── Classification helpers ────────────────────────────────────────────────

    private static string HumanType(PartClassification cls) => cls switch
    {
        PartClassification.BinaryDuplicate               => "Geometry Match (Identical Copy)",
        PartClassification.ExactGeometryMatch            => "Geometry Match",
        PartClassification.GeometryMatchMetadataVariant  => "Geometry Match (Metadata Variant)",
        PartClassification.MirrorOrHandedVariant         => "Geometry Match (Mirror Variant)",
        PartClassification.RevisionFamily                => "Geometry Match (Revision Family)",
        PartClassification.EngravingVariant              => "Geometry Match (Engraving Variant)",
        PartClassification.PossibleMatch                 => "Possible Match",
        PartClassification.Distinct                      => "Unique",
        PartClassification.ComparisonFailed              => "Comparison Failed",
        _ => cls.ToString()
    };

    // Builds the Notes string from differences between cluster members (spec image rules).
    private static string BuildNotes(IReadOnlyList<PartFingerprint> fps, PartClassification cls)
    {
        if (fps.Count < 2) return "";
        var notes = new List<string>();

        // Material difference: always noted regardless of classification.
        var materials = fps.Select(fp => fp.Material?.Trim() ?? "").ToList();
        if (materials.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            var detail = string.Join(" / ", materials.Select(m => m.Length > 0 ? m : "(none)"));
            notes.Add($"Materials differ: {detail}");
        }

        // Hole Wizard vs plain cut: treated as different parts — flag for review.
        static bool HasHoleWzd(PartFingerprint fp) =>
            fp.FeatureTypeHistogram.Keys.Any(k =>
                k.StartsWith("HoleWzd", StringComparison.OrdinalIgnoreCase));
        var wzdFlags = fps.Select(HasHoleWzd).ToList();
        if (wzdFlags.Contains(true) && wzdFlags.Contains(false))
            notes.Add("Hole spec differs: one file uses Hole Wizard, others use plain cut — verify before merging");

        // Engraving: for EngravingVariant clusters, describe the specific difference.
        // For other classifications, mention it as an ancillary observation.
        static bool HasWrapOrCosmetic(PartFingerprint fp) =>
            fp.FeatureTypeHistogram.Keys.Any(k =>
                k.StartsWith("Wrap", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("CosmeticThread", StringComparison.OrdinalIgnoreCase));

        if (cls == PartClassification.EngravingVariant)
        {
            var textCuts = fps.Select(fp => fp.SketchTextCutCount).ToList();
            bool hasTextVariance = textCuts.Distinct().Count() > 1;
            bool hasWrapVariance = fps.Select(HasWrapOrCosmetic).Distinct().Count() > 1;

            if (hasTextVariance)
            {
                var detail = string.Join(", ", fps.Select((fp, i) =>
                    $"File {i + 1}={fp.SketchTextCutCount} text engraving(s)"));
                notes.Add($"Text engraving difference: {detail}");
            }
            if (hasWrapVariance)
                notes.Add("Logo/wrap marking present on some files but not all");
        }
        else
        {
            // Non-engraving clusters: note engraving as an observation if present on any member.
            bool anyTextCut = fps.Any(fp => fp.SketchTextCutCount > 0);
            bool anyWrap    = fps.Any(HasWrapOrCosmetic);
            var engFlags    = fps.Select(fp => fp.SketchTextCutCount > 0 || HasWrapOrCosmetic(fp)).ToList();
            if ((anyTextCut || anyWrap) && engFlags.Contains(true) && engFlags.Contains(false))
                notes.Add("Engraving / marking differs between files — check if intentional");
        }

        return string.Join("; ", notes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void StyleHeader(IXLWorksheet ws, int row, int totalCols)
    {
        var rng = ws.Range(row, 1, row, totalCols);
        rng.Style.Font.Bold = true;
        rng.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
        rng.Style.Font.FontColor = XLColor.White;
        ws.Row(row).Height = 18;
        ws.SheetView.FreezeRows(row);
    }

    private static void WriteHeaders(IXLWorksheet ws, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
        }
        StyleHeader(ws, 1, headers.Length);
    }

    private static void FormatAsTable(IXLWorksheet ws, string name, int firstRow, int lastRow, int colCount)
    {
        if (lastRow < firstRow) return;
        var tbl = ws.Range(firstRow, 1, lastRow, colCount).CreateTable(name);
        tbl.Theme = XLTableTheme.TableStyleMedium2;
        tbl.ShowAutoFilter = true;
        ws.Columns(1, colCount).AdjustToContents();
        CapColumnWidths(ws, colCount, 70);
    }

    private static void CapColumnWidths(IXLWorksheet ws, int colCount, double max)
    {
        for (int c = 1; c <= colCount; c++)
            if (ws.Column(c).Width > max) ws.Column(c).Width = max;
    }
}
