using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;
using System.Collections.ObjectModel;
using WpfMsgBox = System.Windows.MessageBox;
using WpfMsgBoxButton = System.Windows.MessageBoxButton;
using WpfMsgBoxImage = System.Windows.MessageBoxImage;

namespace SolidWorksPartMatcher.App.ViewModels;

/// <summary>Display row wrapping one <see cref="AssemblyComponentDiff"/> with formatted deltas.</summary>
public sealed class AssemblyComponentDiffRowViewModel
{
    public AssemblyComponentDiffRowViewModel(AssemblyComponentDiff diff) => Diff = diff;

    public AssemblyComponentDiff Diff { get; }

    // Geometry-unchanged-but-quantity-changed parts get their own distinct status rather than
    // showing as plain "Unchanged" — the count genuinely differs, which is worth calling out
    // as its own category rather than burying it in a column badge alone.
    public bool IsQuantityOnlyChange => Diff.DiffType == AssemblyDiffType.Unchanged && Diff.QuantityChanged;

    public string MatchKey => Diff.MatchKey;
    public string StatusLabel => Diff.DiffType switch
    {
        AssemblyDiffType.Unchanged when IsQuantityOnlyChange => "Quantity Changed",
        AssemblyDiffType.Unchanged => "Unchanged",
        AssemblyDiffType.Modified => "Modified",
        AssemblyDiffType.Added => "Added",
        AssemblyDiffType.Removed => "Removed",
        AssemblyDiffType.SuspiciousMatch => "Suspicious Match",
        _ => Diff.DiffType.ToString()
    };

    public string StatusBrush => Diff.DiffType switch
    {
        AssemblyDiffType.Unchanged when IsQuantityOnlyChange => "#EDE7F6",
        AssemblyDiffType.Unchanged => "#E8F5E9",
        AssemblyDiffType.Modified => "#FFF8E1",
        AssemblyDiffType.Added => "#E3F2FD",
        AssemblyDiffType.Removed => "#FFEBEE",
        AssemblyDiffType.SuspiciousMatch => "#FFF3E0",
        _ => "#F5F5F5"
    };

    // Always concrete numbers — a missing part is quantity 0, never "?" (see AssemblyComponentMatcher,
    // which now populates InstanceCountA/B as 0 for Added/Removed rather than leaving them null).
    public string QuantityLabel => Diff.InstanceCountA.HasValue || Diff.InstanceCountB.HasValue
        ? $"{Diff.InstanceCountA?.ToString() ?? "?"} → {Diff.InstanceCountB?.ToString() ?? "?"}"
        : "—";

    // The real (OCCT) volume delta — the sole classification signal. Bounding box is no longer
    // computed or shown at all: it produced skewed/false results (a small local feature could
    // swing one bbox axis disproportionately while true volume barely moved, and vice versa).
    // Always shown for any matched pair — even 0.00%, since it's genuinely useful reference
    // data. Only "—" for Added/Removed, where there's no second side to compare against. 2
    // decimal places (not 1) so a real ~-99.98% doesn't misleadingly round to "-100.0%".
    public string VolumeDeltaLabel => Diff.VolumeDeltaPercent is { } v ? $"{v:+0.00;-0.00;0}%" : "—";

    // One bullet per line — easier to scan than a single semicolon-joined sentence. Each bullet
    // is short, plain-English, and free of raw internal measurements (those live on the Diff
    // record itself, e.g. for the Excel export, not in this user-facing text).
    public string ReasonText => string.Join(Environment.NewLine, Diff.Reasons.Select(r => $"• {r}"));

    // A 3D view is available whenever there's at least one side to show — for Added/Removed
    // parts there's nothing to compare against, but the single remaining side can still be
    // viewed on its own (no comparison, just visualization).
    public bool CanViewDiff => Diff.ComponentA is not null || Diff.ComponentB is not null;
}

public sealed partial class AssemblyDiffResultsViewModel : ObservableObject
{
    private readonly AssemblyDiffSummary _summary;
    private readonly string _pathA;
    private readonly string _pathB;
    private readonly IAssemblyDiffReportExporter _exporter;
    private readonly ILogger<AssemblyDiffResultsViewModel> _logger;

    public ObservableCollection<AssemblyComponentDiffRowViewModel> Rows { get; }

    public int UnchangedCount { get; }
    public int ModifiedCount { get; }
    public int AddedCount { get; }
    public int RemovedCount { get; }
    public int SuspiciousCount { get; }
    // Geometry-unchanged-but-quantity-changed parts — its own distinct category (see
    // AssemblyComponentDiffRowViewModel.IsQuantityOnlyChange). Rows that are BOTH shape-Modified
    // AND quantity-changed are still counted under ModifiedCount, not here — the quantity delta
    // for those rows is still visible per-row via QuantityLabel.
    public int QuantityChangeCount { get; }

    // Old/new file being compared — shown in the results window so it's always clear which two
    // files produced this comparison, without having to check the title bar or re-open the
    // compare dialog. Full path is available via tooltip binding to *FilePath.
    public string OldFileLabel => System.IO.Path.GetFileName(_pathA);
    public string NewFileLabel => System.IO.Path.GetFileName(_pathB);
    public string OldFilePath => _pathA;
    public string NewFilePath => _pathB;

    [ObservableProperty] private string _statusMessage = "";

    public AssemblyDiffResultsViewModel(
        AssemblyDiffSummary summary,
        string pathA,
        string pathB,
        IAssemblyDiffReportExporter exporter,
        ILogger<AssemblyDiffResultsViewModel> logger)
    {
        _summary  = summary;
        _pathA    = pathA;
        _pathB    = pathB;
        _exporter = exporter;
        _logger   = logger;

        Rows = new ObservableCollection<AssemblyComponentDiffRowViewModel>(
            summary.Components.Select(c => new AssemblyComponentDiffRowViewModel(c)));

        UnchangedCount      = summary.Components.Count(c =>
            c.DiffType == AssemblyDiffType.Unchanged && !c.QuantityChanged);
        ModifiedCount       = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Modified);
        AddedCount          = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Added);
        RemovedCount        = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Removed);
        SuspiciousCount     = summary.Components.Count(c => c.DiffType == AssemblyDiffType.SuspiciousMatch);
        QuantityChangeCount = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Unchanged && c.QuantityChanged);

        if (summary.Warnings.Count > 0)
            StatusMessage = $"{summary.Warnings.Count} warning(s) — see Export Report for details.";
    }

    [RelayCommand]
    private void ViewComponentDiff(AssemblyComponentDiffRowViewModel? row)
    {
        if (row is null) return;
        var compA = row.Diff.ComponentA;
        var compB = row.Diff.ComponentB;
        if (compA is null && compB is null) return;

        try
        {
            // Added/Removed rows only have one side — there's nothing to compare against, but
            // the single remaining part can still be opened for a plain (non-comparison) view.
            if (compA is not null && compB is not null)
            {
                var readerA = StepP21Reader.ParseFile(_pathA);
                var readerB = StepP21Reader.ParseFile(_pathB);

                var tempA = StepComponentSnippetWriter.BuildTempPath($"{compA.MatchKey}_A");
                var tempB = StepComponentSnippetWriter.BuildTempPath($"{compB.MatchKey}_B");
                StepComponentSnippetWriter.WriteSnippet(readerA, compA, tempA);
                StepComponentSnippetWriter.WriteSnippet(readerB, compB, tempB);

                // Side by side, not overlaid: there's no reliable landmark-based (hole-to-hole,
                // edge-to-edge) automatic alignment available from tessellated STEP geometry
                // alone. Each viewport keeps its own independent camera (no link_views()), so
                // orbiting one part never affects the other.
                var win = new Views.StepDiffWindow(
                    $"{row.Diff.MatchKey} — A vs B", [tempA, tempB], sideBySide: true)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                win.Show();
            }
            else if (compA is not null)
            {
                var readerA = StepP21Reader.ParseFile(_pathA);
                var tempA = StepComponentSnippetWriter.BuildTempPath($"{compA.MatchKey}_A");
                StepComponentSnippetWriter.WriteSnippet(readerA, compA, tempA);

                var win = new Views.StepDiffWindow($"{row.Diff.MatchKey} (removed)", [tempA])
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                win.Show();
            }
            else
            {
                var readerB = StepP21Reader.ParseFile(_pathB);
                var tempB = StepComponentSnippetWriter.BuildTempPath($"{compB!.MatchKey}_B");
                StepComponentSnippetWriter.WriteSnippet(readerB, compB, tempB);

                var win = new Views.StepDiffWindow($"{row.Diff.MatchKey} (added)", [tempB])
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                win.Show();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build component 3D view for {Key}", row.Diff.MatchKey);
            WpfMsgBox.Show($"Could not open the 3D view for '{row.Diff.MatchKey}':\n{ex.Message}",
                "3D View Error", WpfMsgBoxButton.OK, WpfMsgBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ViewWholeAssemblyOverlay()
    {
        // Two versions of the same assembly share a native coordinate system, so skip the
        // viewer's default centroid re-centering (useNativeAlignment: true) — see
        // StepDiffWindow's constructor doc and tools/view_steps.py for why.
        var win = new Views.StepDiffWindow("Whole Assembly Comparison", [_pathA, _pathB], useNativeAlignment: true)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        win.Show();
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title    = "Export Assembly Diff Report",
            Filter   = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"AssemblyDiff_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _exporter.ExportAsync(_summary, dlg.FileName, CancellationToken.None);
            StatusMessage = $"Exported to {System.IO.Path.GetFileName(dlg.FileName)}";
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assembly diff report export failed");
            WpfMsgBox.Show(ex.Message, "Export Error", WpfMsgBoxButton.OK, WpfMsgBoxImage.Error);
        }
    }
}
