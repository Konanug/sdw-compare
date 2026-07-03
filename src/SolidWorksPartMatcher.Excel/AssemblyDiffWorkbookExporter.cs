using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Excel;

public sealed class AssemblyDiffWorkbookExporter(ILogger<AssemblyDiffWorkbookExporter> logger)
    : IAssemblyDiffReportExporter
{
    public Task ExportAsync(AssemblyDiffSummary summary, string outputPath, CancellationToken ct)
    {
        logger.LogInformation("Exporting assembly diff report to {Path}", outputPath);

        using var wb = new XLWorkbook();

        AddSummarySheet(wb, summary);
        AddModifiedPartsSheet(wb, summary);
        AddAddedPartsSheet(wb, summary);
        AddRemovedPartsSheet(wb, summary);
        AddQuantityChangedSheet(wb, summary);

        try { wb.SaveAs(outputPath); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save assembly diff report to {Path}", outputPath);
            throw;
        }

        logger.LogInformation("Assembly diff report saved to {Path}", outputPath);
        return Task.CompletedTask;
    }

    // ── Sheet 1: Summary ─────────────────────────────────────────────────────

    private static void AddSummarySheet(XLWorkbook wb, AssemblyDiffSummary summary)
    {
        var ws = wb.Worksheets.Add("Summary");
        ws.Cell(1, 1).Value = "Item";
        ws.Cell(1, 2).Value = "Value";
        StyleHeader(ws, 1, 2);

        int unchanged = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Unchanged);
        int modified  = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Modified);
        int added     = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Added);
        int removed   = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Removed);
        int suspicious = summary.Components.Count(c => c.DiffType == AssemblyDiffType.SuspiciousMatch);
        int qtyChanged = summary.Components.Count(c => c.QuantityChanged);

        var kvs = new List<(string, string)>
        {
            ("Assembly A",             summary.FileAPath),
            ("Assembly B",             summary.FileBPath),
            ("Compared",               summary.ComparedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            ("Total Components",       summary.Components.Count.ToString()),
            ("  — Unchanged",          unchanged.ToString()),
            ("  — Modified",           modified.ToString()),
            ("  — Added",              added.ToString()),
            ("  — Removed",            removed.ToString()),
            ("  — Suspicious Match",   suspicious.ToString()),
            ("  — Quantity Changed",   qtyChanged.ToString()),
            ("Warnings",               summary.Warnings.Count.ToString()),
        };

        for (int i = 0; i < kvs.Count; i++)
        {
            ws.Cell(i + 2, 1).Value = kvs[i].Item1;
            ws.Cell(i + 2, 2).Value = kvs[i].Item2;
            ws.Cell(i + 2, 1).Style.Font.Bold = true;
        }

        int warnRow = kvs.Count + 3;
        if (summary.Warnings.Count > 0)
        {
            ws.Cell(warnRow, 1).Value = "Warnings:";
            ws.Cell(warnRow, 1).Style.Font.Bold = true;
            warnRow++;
            foreach (var w in summary.Warnings)
            {
                ws.Cell(warnRow, 1).Value = w;
                warnRow++;
            }
        }

        ws.Column(1).Width = 24;
        ws.Column(2).Width = 90;
    }

    // ── Sheet 2: Modified Parts ──────────────────────────────────────────────

    private static void AddModifiedPartsSheet(XLWorkbook wb, AssemblyDiffSummary summary)
    {
        var ws = wb.Worksheets.Add("Modified Parts");
        var headers = new[]
        {
            "Part Name", "Length Δ (mm, %)", "Width Δ (mm, %)", "Height Δ (mm, %)",
            "BB Volume Δ (%)", "Est. Volume Δ (cm³, %)", "Surface Area Δ (cm², %)", "Face Count Δ",
            "Qty A", "Qty B", "Qty Changed?", "Orientation Changed?", "Position Changed?",
            "Match Basis", "Notes"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var d in summary.Components
            .Where(c => c.DiffType is AssemblyDiffType.Modified or AssemblyDiffType.SuspiciousMatch)
            .OrderBy(c => c.MatchKey, StringComparer.Ordinal))
        {
            var a = d.ComponentA; var b = d.ComponentB;
            ws.Cell(row, 1).Value = d.MatchKey;

            for (int axis = 0; axis < 3 && d.BoundingBoxDeltaPercent is not null; axis++)
            {
                double deltaMm = a is not null && b is not null
                    ? (b.SortedBoundingBoxM[axis] - a.SortedBoundingBoxM[axis]) * 1000.0
                    : 0;
                ws.Cell(row, 2 + axis).Value =
                    $"{Math.Round(deltaMm, 2)} ({Math.Round(d.BoundingBoxDeltaPercent[axis], 2)}%)";
            }

            // 2 decimal places, not 1 — a genuine -99.98% delta rounding to a clean-looking
            // "-100.0%" misleadingly reads as "vanished entirely" rather than "very different".
            // BB Volume Δ is exact (straight from the measured L×W×H product); Est. Volume Δ is
            // StepGeometryEstimator's heuristic body-volume estimate — both worth showing since
            // they're derived differently and can disagree.
            if (d.BoundingBoxVolumeDeltaPercent is { } bbVolPct)
                ws.Cell(row, 5).Value = $"{Math.Round(bbVolPct, 2)}%";
            if (d.VolumeDeltaPercent is { } volPct && a is not null && b is not null)
                ws.Cell(row, 6).Value =
                    $"{Math.Round((b.VolumeM3 - a.VolumeM3) * 1e6, 2)} ({Math.Round(volPct, 2)}%)";
            if (d.SurfaceAreaDeltaPercent is { } saPct && a is not null && b is not null)
                ws.Cell(row, 7).Value =
                    $"{Math.Round((b.SurfaceAreaM2 - a.SurfaceAreaM2) * 1e4, 2)} ({Math.Round(saPct, 2)}%)";
            if (d.FaceCountDelta is { } faceDelta)
                ws.Cell(row, 8).Value = faceDelta;

            ws.Cell(row, 9).Value  = d.InstanceCountA?.ToString() ?? "?";
            ws.Cell(row, 10).Value = d.InstanceCountB?.ToString() ?? "?";
            ws.Cell(row, 11).Value = d.QuantityChanged ? "Yes" : "No";
            ws.Cell(row, 12).Value = d.OrientationChanged switch { true => "Yes", false => "No", null => "N/A" };
            ws.Cell(row, 13).Value = d.PositionChanged switch { true => "Yes", false => "No", null => "N/A" };
            ws.Cell(row, 14).Value = d.GeometricSimilarityScore.HasValue ? "Geometry (rename)" : "Name";
            ws.Cell(row, 15).Value = string.Join("; ", d.Reasons);

            row++;
        }

        FormatAsTable(ws, "ModifiedParts", 1, row - 1, headers.Length);
    }

    // ── Sheet 3: Added Parts ─────────────────────────────────────────────────

    private static void AddAddedPartsSheet(XLWorkbook wb, AssemblyDiffSummary summary)
        => AddOneSidedSheet(wb, "Added Parts", summary.Components
            .Where(c => c.DiffType == AssemblyDiffType.Added)
            .OrderBy(c => c.MatchKey, StringComparer.Ordinal),
            c => (c.ComponentB!, c.InstanceCountB));

    // ── Sheet 4: Removed Parts ───────────────────────────────────────────────

    private static void AddRemovedPartsSheet(XLWorkbook wb, AssemblyDiffSummary summary)
        => AddOneSidedSheet(wb, "Removed Parts", summary.Components
            .Where(c => c.DiffType == AssemblyDiffType.Removed)
            .OrderBy(c => c.MatchKey, StringComparer.Ordinal),
            c => (c.ComponentA!, c.InstanceCountA));

    private static void AddOneSidedSheet(
        XLWorkbook wb, string sheetName,
        IEnumerable<AssemblyComponentDiff> diffs,
        Func<AssemblyComponentDiff, (AssemblyComponent Component, int? Qty)> select)
    {
        var ws = wb.Worksheets.Add(sheetName);
        var headers = new[] { "Part Name", "Length (mm)", "Width (mm)", "Height (mm)", "Volume (cm³)", "Qty" };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var d in diffs)
        {
            var (c, qty) = select(d);
            var bb = c.SortedBoundingBoxM;
            ws.Cell(row, 1).Value = d.MatchKey;
            if (bb.Length > 0) ws.Cell(row, 2).Value = Math.Round(bb[0] * 1000.0, 2);
            if (bb.Length > 1) ws.Cell(row, 3).Value = Math.Round(bb[1] * 1000.0, 2);
            if (bb.Length > 2) ws.Cell(row, 4).Value = Math.Round(bb[2] * 1000.0, 2);
            ws.Cell(row, 5).Value = Math.Round(c.VolumeM3 * 1e6, 2);
            ws.Cell(row, 6).Value = qty?.ToString() ?? "?";
            row++;
        }

        FormatAsTable(ws, sheetName.Replace(" ", ""), 1, row - 1, headers.Length);
    }

    // ── Sheet 5: Quantity Changed (cross-cutting) ────────────────────────────

    private static void AddQuantityChangedSheet(XLWorkbook wb, AssemblyDiffSummary summary)
    {
        var ws = wb.Worksheets.Add("Quantity Changed");
        var headers = new[] { "Part Name", "Qty A", "Qty B", "Qty Δ", "Also Shape-Modified?" };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var d in summary.Components
            .Where(c => c.QuantityChanged)
            .OrderBy(c => c.MatchKey, StringComparer.Ordinal))
        {
            ws.Cell(row, 1).Value = d.MatchKey;
            ws.Cell(row, 2).Value = d.InstanceCountA?.ToString() ?? "?";
            ws.Cell(row, 3).Value = d.InstanceCountB?.ToString() ?? "?";
            if (d.InstanceCountA.HasValue && d.InstanceCountB.HasValue)
                ws.Cell(row, 4).Value = d.InstanceCountB.Value - d.InstanceCountA.Value;
            ws.Cell(row, 5).Value = d.DiffType is AssemblyDiffType.Modified or AssemblyDiffType.SuspiciousMatch
                ? "Yes" : "No";
            row++;
        }

        FormatAsTable(ws, "QuantityChanged", 1, row - 1, headers.Length);
    }

    // ── Helpers (mirrors ClosedXmlWorkbookExporter's styling conventions) ──────

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
            ws.Cell(1, i + 1).Value = headers[i];
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
