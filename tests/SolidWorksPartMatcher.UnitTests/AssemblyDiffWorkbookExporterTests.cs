using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Excel;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class AssemblyDiffWorkbookExporterTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"assembly_diff_test_{Guid.NewGuid():N}.xlsx");

    private static AssemblyComponent MakeComponent(string name, int? qty = 1) => new(
        ProductId: name, ProductName: name, MatchKey: name, InstanceCount: qty,
        SortedBoundingBoxM: [0.01, 0.02, 0.03], VolumeM3: 0.00001, SurfaceAreaM2: 0.001,
        FaceCount: 6, FaceTypeHistogram: new Dictionary<string, int>(),
        FaceGeometricSignature: [], EntityClosure: [], OccurrencePositionsM: []);

    [Fact]
    public async Task ExportAsync_ProducesWorkbook_WithExpectedSheets()
    {
        var a1 = MakeComponent("MODIFIED-PART", qty: 2);
        var b1 = a1 with { SortedBoundingBoxM = [0.02, 0.03, 0.04], VolumeM3 = 0.00003, InstanceCount = 3 };
        var addedComp = MakeComponent("ADDED-PART");
        var removedComp = MakeComponent("REMOVED-PART");

        var diffs = new List<AssemblyComponentDiff>
        {
            new("MODIFIED-PART", a1, b1, AssemblyDiffType.Modified, true, 2, 3,
                200.0, 100.0, 0, null, ["Quantity changed from 2 to 3.", "Volume increased by 200%."]),
            new("ADDED-PART", null, addedComp, AssemblyDiffType.Added, false, 0, 1,
                null, null, null, null, ["Part added."]),
            new("REMOVED-PART", removedComp, null, AssemblyDiffType.Removed, false, 1, 0,
                null, null, null, null, ["Part removed."]),
        };

        var summary = new AssemblyDiffSummary("A.step", "B.step", DateTime.UtcNow, diffs, []);
        var exporter = new AssemblyDiffWorkbookExporter(NullLogger<AssemblyDiffWorkbookExporter>.Instance);

        await exporter.ExportAsync(summary, _tempPath, CancellationToken.None);

        File.Exists(_tempPath).Should().BeTrue();

        using var wb = new XLWorkbook(_tempPath);
        wb.Worksheets.Select(w => w.Name).Should().BeEquivalentTo(
            ["Summary", "Comparison", "Modified Parts", "Added Parts", "Removed Parts", "Quantity Changed"]);

        wb.Worksheet("Modified Parts").Cell(2, 1).GetString().Should().Be("MODIFIED-PART");
        wb.Worksheet("Added Parts").Cell(2, 1).GetString().Should().Be("ADDED-PART");
        wb.Worksheet("Removed Parts").Cell(2, 1).GetString().Should().Be("REMOVED-PART");
        wb.Worksheet("Quantity Changed").Cell(2, 1).GetString().Should().Be("MODIFIED-PART");
    }

    [Fact]
    public async Task ComparisonSheet_MirrorsTheAppGrid_ChangedRowsFirstWithTickColumns()
    {
        // MODIFIED-PART: volume + quantity change (two-sided). ADDED / REMOVED are one-sided.
        var a1 = MakeComponent("MODIFIED-PART", qty: 2);
        var b1 = a1 with { VolumeM3 = 0.00003, InstanceCount = 3 };
        var unchanged = MakeComponent("ZZZ-UNCHANGED");

        var diffs = new List<AssemblyComponentDiff>
        {
            // Trailing positional arg is PositionChanged (true here) to prove the tick surfaces.
            new("MODIFIED-PART", a1, b1, AssemblyDiffType.Modified, true, 2, 3,
                200.0, 100.0, 0, null, ["Quantity changed.", "Volume increased."], true),
            new("ZZZ-UNCHANGED", unchanged, unchanged, AssemblyDiffType.Unchanged, false, 1, 1,
                null, null, null, null, []),
        };

        var summary = new AssemblyDiffSummary("A.step", "B.step", DateTime.UtcNow, diffs, []);
        var exporter = new AssemblyDiffWorkbookExporter(NullLogger<AssemblyDiffWorkbookExporter>.Instance);

        await exporter.ExportAsync(summary, _tempPath, CancellationToken.None);

        using var wb = new XLWorkbook(_tempPath);
        var ws = wb.Worksheet("Comparison");

        // Header order mirrors the grid columns.
        ws.Cell(1, 1).GetString().Should().Be("Part");
        ws.Cell(1, 4).GetString().Should().Be("Position");
        ws.Cell(1, 5).GetString().Should().Be("Quantity");
        ws.Cell(1, 6).GetString().Should().Be("Volume");

        // Changed group sorts first: the modified part is row 2, the unchanged part row 3.
        ws.Cell(2, 1).GetString().Should().Be("MODIFIED-PART");
        ws.Cell(2, 2).GetString().Should().Be("Changed");
        ws.Cell(2, 4).GetString().Should().Be("✓"); // position
        ws.Cell(2, 5).GetString().Should().Be("✓"); // quantity
        ws.Cell(2, 6).GetString().Should().Be("✓"); // volume

        ws.Cell(3, 1).GetString().Should().Be("ZZZ-UNCHANGED");
        ws.Cell(3, 2).GetString().Should().Be("Unchanged");
        ws.Cell(3, 4).GetString().Should().BeEmpty();
        ws.Cell(3, 6).GetString().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
