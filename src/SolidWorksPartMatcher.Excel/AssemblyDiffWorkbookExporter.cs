using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Excel;

/// <summary>
/// Exports an assembly-diff report whose primary sheet ("Comparison") is a faithful, one-to-one
/// mirror of the app's results grid: the same Changed/Unchanged grouping, the same Added / Removed
/// / Suspicious status words, and the same Position / Quantity / Volume / Renamed signals shown as
/// tick marks — so the workbook reads like an exported copy of what the user sees on screen, not a
/// separate format. The per-category detail sheets (Modified / Added / Removed / Quantity Changed)
/// follow as drill-down with the raw geometry numbers the grid surfaces only via its 3D view.
///
/// The row/grouping logic here is deliberately kept identical to
/// <c>AssemblyComponentDiffRowViewModel</c> in the App project; the two must stay in sync.
/// </summary>
public sealed class AssemblyDiffWorkbookExporter(ILogger<AssemblyDiffWorkbookExporter> logger)
    : IAssemblyDiffReportExporter
{
    private const string Tick = "✓";

    // App status/group colours (match AssemblyComponentDiffRowViewModel.StatusBrush exactly).
    private const string ColAdded = "#E3F2FD";
    private const string ColRemoved = "#FFEBEE";
    private const string ColSuspicious = "#FFF3E0";
    private const string ColChanged = "#FFF8E1";
    private const string ColUnchanged = "#E8F5E9";

    public Task ExportAsync(AssemblyDiffSummary summary, string outputPath, CancellationToken ct)
    {
        logger.LogInformation("Exporting assembly diff report to {Path}", outputPath);

        using var wb = new XLWorkbook();

        AddSummarySheet(wb, summary);
        AddComparisonSheet(wb, summary);   // primary sheet — mirrors the app's results grid
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

    // ── Row-level predicates — kept identical to the app's AssemblyComponentDiffRowViewModel ─────

    private static bool IsTwoSided(AssemblyComponentDiff d) => d.ComponentA is not null && d.ComponentB is not null;
    private static bool HasVolumeChange(AssemblyComponentDiff d) =>
        IsTwoSided(d) && d.DiffType is AssemblyDiffType.Modified or AssemblyDiffType.SuspiciousMatch;
    private static bool HasQuantityChange(AssemblyComponentDiff d) => d.QuantityChanged;
    private static bool HasPositionChange(AssemblyComponentDiff d) => d.PositionChanged == true;
    private static bool IsRenamed(AssemblyComponentDiff d) => IsTwoSided(d) && d.GeometricSimilarityScore is not null;
    private static bool HasAnyChange(AssemblyComponentDiff d) =>
        HasVolumeChange(d) || HasQuantityChange(d) || HasPositionChange(d);
    private static bool IsUnchanged(AssemblyComponentDiff d) =>
        d.DiffType == AssemblyDiffType.Unchanged && !HasAnyChange(d);
    private static string GroupOf(AssemblyComponentDiff d) => IsUnchanged(d) ? "Unchanged" : "Changed";
    private static int GroupRank(AssemblyComponentDiff d) => IsUnchanged(d) ? 1 : 0;

    private static string StatusBadge(AssemblyComponentDiff d) => d.DiffType switch
    {
        AssemblyDiffType.Added => "Added",
        AssemblyDiffType.Removed => "Removed",
        AssemblyDiffType.SuspiciousMatch => "Suspicious",
        _ => ""
    };

    private static string RowColour(AssemblyComponentDiff d) => d.DiffType switch
    {
        AssemblyDiffType.Added => ColAdded,
        AssemblyDiffType.Removed => ColRemoved,
        AssemblyDiffType.SuspiciousMatch => ColSuspicious,
        _ => HasAnyChange(d) ? ColChanged : ColUnchanged
    };

    private static string VolumeDelta(AssemblyComponentDiff d) =>
        d.VolumeDeltaPercent is { } v ? v.ToString("+0.00;-0.00;0") + "%" : "—";

    private static string QuantityDetail(AssemblyComponentDiff d) =>
        $"{d.InstanceCountA?.ToString() ?? "?"} → {d.InstanceCountB?.ToString() ?? "?"}";

    // Same lines the grid's "Details" column shows (rename, quantity, volume), joined for one cell.
    private static string DetailText(AssemblyComponentDiff d)
    {
        var lines = new List<string>();
        if (IsRenamed(d))
            lines.Add($"Renamed: {d.ComponentA?.MatchKey} → {d.ComponentB?.MatchKey}");
        if (HasQuantityChange(d))
            lines.Add($"Quantity: {d.InstanceCountA} → {d.InstanceCountB}");
        if (HasVolumeChange(d) && d.VolumeDeltaPercent is { } v)
            lines.Add($"Volume: {v.ToString("+0.##;-0.##;0")}%");
        return string.Join("; ", lines);
    }

    // Same ordering the grid uses: Changed group first, then alphabetical by MatchKey.
    private static IEnumerable<AssemblyComponentDiff> InGridOrder(AssemblyDiffSummary s) =>
        s.Components
            .OrderBy(GroupRank)
            .ThenBy(c => c.MatchKey, StringComparer.Ordinal);

    // ── Sheet 1: Summary (counts match the app's summary bar) ────────────────

    private static void AddSummarySheet(XLWorkbook wb, AssemblyDiffSummary summary)
    {
        var ws = wb.Worksheets.Add("Summary");
        ws.Cell(1, 1).Value = "Item";
        ws.Cell(1, 2).Value = "Value";
        StyleHeader(ws, 1, 2);

        // These mirror AssemblyDiffResultsViewModel's counts exactly (Changed collapses the former
        // Modified / quantity-change / position-change categories, matching the grid).
        int unchanged = summary.Components.Count(IsUnchanged);
        int changed = summary.Components.Count(c =>
            c.DiffType == AssemblyDiffType.Modified ||
            (c.DiffType == AssemblyDiffType.Unchanged && HasAnyChange(c)));
        int added = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Added);
        int removed = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Removed);
        int suspicious = summary.Components.Count(c => c.DiffType == AssemblyDiffType.SuspiciousMatch);
        int qtyChanged = summary.Components.Count(HasQuantityChange);
        int posChanged = summary.Components.Count(HasPositionChange);

        var kvs = new List<(string, string)>
        {
            ("Assembly A",             summary.FileAPath),
            ("Assembly B",             summary.FileBPath),
            ("Compared",               summary.ComparedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            ("Total Components",       summary.Components.Count.ToString()),
            ("  — Changed",            changed.ToString()),
            ("  — Unchanged",          unchanged.ToString()),
            ("  — Added",              added.ToString()),
            ("  — Removed",            removed.ToString()),
            ("  — Suspicious Match",   suspicious.ToString()),
            ("  — Quantity Changed",   qtyChanged.ToString()),
            ("  — Position Changed",   posChanged.ToString()),
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

    // ── Sheet 2: Comparison — the app's results grid, exported ───────────────

    private static void AddComparisonSheet(XLWorkbook wb, AssemblyDiffSummary summary)
    {
        var ws = wb.Worksheets.Add("Comparison");
        var headers = new[]
        {
            "Part", "Group", "Status", "Position", "Quantity", "Volume", "Renamed",
            "Quantity (A→B)", "Volume Δ %", "Details"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var d in InGridOrder(summary))
        {
            ws.Cell(row, 1).Value = d.MatchKey;
            ws.Cell(row, 2).Value = GroupOf(d);
            ws.Cell(row, 3).Value = StatusBadge(d);
            ws.Cell(row, 4).Value = HasPositionChange(d) ? Tick : "";
            ws.Cell(row, 5).Value = HasQuantityChange(d) ? Tick : "";
            ws.Cell(row, 6).Value = HasVolumeChange(d) ? Tick : "";
            ws.Cell(row, 7).Value = IsRenamed(d) ? Tick : "";
            ws.Cell(row, 8).Value = QuantityDetail(d);
            ws.Cell(row, 9).Value = VolumeDelta(d);
            ws.Cell(row, 10).Value = DetailText(d);

            // Shade the row with the same colour the grid uses for this status.
            ws.Range(row, 1, row, headers.Length).Style.Fill.BackgroundColor =
                XLColor.FromHtml(RowColour(d));

            // Centre the tick columns so blanks vs. ticks scan cleanly, like the grid.
            ws.Range(row, 4, row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;
        }

        // Manual filter/freeze (not a themed table) so the per-row status colours are preserved.
        if (row > 2)
            ws.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents();
        CapColumnWidths(ws, headers.Length, 60);
    }

    // ── Sheet 3: Modified Parts (drill-down detail) ──────────────────────────

    private static void AddModifiedPartsSheet(XLWorkbook wb, AssemblyDiffSummary summary)
    {
        var ws = wb.Worksheets.Add("Modified Parts");
        var headers = new[]
        {
            "Part Name", "Status", "Volume Δ (cm³, %)", "Surface Area Δ (cm², %)", "Face Count Δ",
            "Qty A", "Qty B", "Qty Changed?", "Position Changed?", "Match Basis", "Notes"
        };
        WriteHeaders(ws, headers);

        int row = 2;
        foreach (var d in summary.Components
            .Where(c => c.DiffType is AssemblyDiffType.Modified or AssemblyDiffType.SuspiciousMatch)
            .OrderBy(c => c.MatchKey, StringComparer.Ordinal))
        {
            var a = d.ComponentA; var b = d.ComponentB;
            ws.Cell(row, 1).Value = d.MatchKey;
            ws.Cell(row, 2).Value = d.DiffType == AssemblyDiffType.SuspiciousMatch ? "Suspicious" : "Modified";

            // 2 decimal places, not 1 — a genuine -99.98% delta rounding to a clean-looking
            // "-100.0%" misleadingly reads as "vanished entirely" rather than "very different".
            // This is the real (OCCT) volume — the sole classification signal; bounding box is
            // no longer computed or shown at all, since it produced skewed/false results.
            if (d.VolumeDeltaPercent is { } volPct && a is not null && b is not null)
                ws.Cell(row, 3).Value =
                    $"{Math.Round((b.VolumeM3 - a.VolumeM3) * 1e6, 2)} ({Math.Round(volPct, 2)}%)";
            if (d.SurfaceAreaDeltaPercent is { } saPct && a is not null && b is not null)
                ws.Cell(row, 4).Value =
                    $"{Math.Round((b.SurfaceAreaM2 - a.SurfaceAreaM2) * 1e4, 2)} ({Math.Round(saPct, 2)}%)";
            if (d.FaceCountDelta is { } faceDelta)
                ws.Cell(row, 5).Value = faceDelta;

            ws.Cell(row, 6).Value = d.InstanceCountA?.ToString() ?? "?";
            ws.Cell(row, 7).Value = d.InstanceCountB?.ToString() ?? "?";
            ws.Cell(row, 8).Value = d.QuantityChanged ? "Yes" : "No";
            ws.Cell(row, 9).Value = d.PositionChanged switch { true => "Yes", false => "No", null => "—" };
            ws.Cell(row, 10).Value = d.GeometricSimilarityScore.HasValue ? "Geometry (rename)" : "Name";
            ws.Cell(row, 11).Value = string.Join("; ", d.Reasons);

            row++;
        }

        FormatAsTable(ws, "ModifiedParts", 1, row - 1, headers.Length);
    }

    // ── Sheet 4: Added Parts ─────────────────────────────────────────────────

    private static void AddAddedPartsSheet(XLWorkbook wb, AssemblyDiffSummary summary)
        => AddOneSidedSheet(wb, "Added Parts", summary.Components
            .Where(c => c.DiffType == AssemblyDiffType.Added)
            .OrderBy(c => c.MatchKey, StringComparer.Ordinal),
            c => (c.ComponentB!, c.InstanceCountB));

    // ── Sheet 5: Removed Parts ───────────────────────────────────────────────

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

    // ── Sheet 6: Quantity Changed (cross-cutting) ────────────────────────────

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
