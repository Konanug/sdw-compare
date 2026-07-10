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

/// <summary>
/// Display row wrapping one <see cref="AssemblyComponentDiff"/>. The grid uses a checklist style:
/// parts present in both versions are described purely by three tick columns (Position / Quantity
/// / Volume), replacing the old prose "what changed" bullets and the many separate status
/// categories. Added / Removed / Suspicious keep a distinct coloured status word, because those
/// aren't expressible as a Position/Quantity/Volume tick (a one-sided add/remove, or a
/// "likely a different part" warning).
/// </summary>
public sealed class AssemblyComponentDiffRowViewModel
{
    private const string Tick = "✓"; // ✓

    public AssemblyComponentDiffRowViewModel(AssemblyComponentDiff diff) => Diff = diff;

    public AssemblyComponentDiff Diff { get; }

    public string MatchKey => Diff.MatchKey;

    // A part present in both versions — the only case where Position/Quantity/Volume ticks are
    // meaningful (Added/Removed have nothing to compare against).
    private bool IsTwoSided => Diff.ComponentA is not null && Diff.ComponentB is not null;

    // Kept as a distinct coloured word rather than folded into the tick columns.
    public bool ShowStatusBadge => Diff.DiffType
        is AssemblyDiffType.Added or AssemblyDiffType.Removed or AssemblyDiffType.SuspiciousMatch;
    public string StatusBadge => Diff.DiffType switch
    {
        AssemblyDiffType.Added => "Added",
        AssemblyDiffType.Removed => "Removed",
        AssemblyDiffType.SuspiciousMatch => "Suspicious",
        _ => ""
    };

    // ── Checklist ticks ──────────────────────────────────────────────────────────────────────
    // A tick means that specific kind of change was detected. Blank otherwise. Volume "changed"
    // is exactly the classification signal (Modified/Suspicious are reached only via a real
    // volume delta); Quantity and Position mirror the detected flags on the diff.
    public bool HasVolumeChange => IsTwoSided
        && Diff.DiffType is AssemblyDiffType.Modified or AssemblyDiffType.SuspiciousMatch;
    public bool HasQuantityChange => Diff.QuantityChanged;
    public bool HasPositionChange => Diff.PositionChanged == true;
    // A geometric-fallback pairing (different names, matched by shape) — i.e. a likely rename.
    public bool IsRenamed => IsTwoSided && Diff.GeometricSimilarityScore is not null;

    public string VolumeMark => HasVolumeChange ? Tick : "";
    public string QuantityMark => HasQuantityChange ? Tick : "";
    public string PositionMark => HasPositionChange ? Tick : "";

    // The underlying numbers, surfaced on hover so the ticks stay clean but no detail is lost.
    public string QuantityDetail =>
        $"{Diff.InstanceCountA?.ToString() ?? "?"} → {Diff.InstanceCountB?.ToString() ?? "?"}";
    public string VolumeDetail => Diff.VolumeDeltaPercent is { } v ? $"{v:+0.00;-0.00;0}%" : "—";

    // Spelled-out specifics for the changes the ticks can't quantify: how much the quantity
    // changed, the exact volume %, and whether the name changed (rename). Only the applicable
    // lines appear; blank for a plain unchanged part.
    public string DetailText
    {
        get
        {
            var lines = new List<string>();
            if (IsRenamed)
                lines.Add($"Renamed: {(Diff.ComponentA?.MatchKey)} → {(Diff.ComponentB?.MatchKey)}");
            if (HasQuantityChange)
                lines.Add($"Quantity: {Diff.InstanceCountA} → {Diff.InstanceCountB}");
            if (HasVolumeChange && Diff.VolumeDeltaPercent is { } v)
                lines.Add($"Volume: {v:+0.##;-0.##;0}%");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasAnyChange => HasVolumeChange || HasQuantityChange || HasPositionChange;

    // Split changed parts away from unchanged ones. Added/Removed/Suspicious and any two-sided
    // part with a detected change are "Changed"; a plain identical part is "Unchanged".
    public bool IsUnchanged =>
        Diff.DiffType == AssemblyDiffType.Unchanged && !HasAnyChange;
    public string Group => IsUnchanged ? "Unchanged" : "Changed";
    public int GroupRank => IsUnchanged ? 1 : 0; // Changed group sorts first

    public string StatusBrush => Diff.DiffType switch
    {
        AssemblyDiffType.Added => "#E3F2FD",
        AssemblyDiffType.Removed => "#FFEBEE",
        AssemblyDiffType.SuspiciousMatch => "#FFF3E0",
        // Everything else present in both versions: one "changed" colour (collapsing the former
        // Modified / Quantity / Placement categories) vs. plain unchanged.
        _ => HasAnyChange ? "#FFF8E1" : "#E8F5E9"
    };

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

    // Grouped ("Changed" / "Unchanged") and filterable view bound by the grid. The toggle
    // filters below combine with AND: a row is shown only if it matches every active filter.
    public System.ComponentModel.ICollectionView RowsView { get; }

    public int UnchangedCount { get; }
    // Collapses the former Modified / Quantity-change / Placement-change categories: any part
    // present in both versions with at least one detected Position/Quantity/Volume change.
    public int ChangedCount { get; }
    public int AddedCount { get; }
    public int RemovedCount { get; }
    public int SuspiciousCount { get; }

    // ── Filter toggles (AND-combined) ────────────────────────────────────────────────────────
    // Each toggle isolates rows with that change; multiple toggles require ALL of them to hold
    // (e.g. Position + Volume → only parts that both moved AND changed volume). No toggles = show
    // everything. "Clear" (ClearFiltersCommand) deselects all.
    [ObservableProperty] private bool _filterPosition;
    [ObservableProperty] private bool _filterQuantity;
    [ObservableProperty] private bool _filterVolume;
    [ObservableProperty] private bool _filterRenamed;
    [ObservableProperty] private bool _filterAdded;
    [ObservableProperty] private bool _filterRemoved;
    [ObservableProperty] private bool _filterSuspicious;

    partial void OnFilterPositionChanged(bool value) => OnFilterChanged();
    partial void OnFilterQuantityChanged(bool value) => OnFilterChanged();
    partial void OnFilterVolumeChanged(bool value) => OnFilterChanged();
    partial void OnFilterRenamedChanged(bool value) => OnFilterChanged();
    partial void OnFilterAddedChanged(bool value) => OnFilterChanged();
    partial void OnFilterRemovedChanged(bool value) => OnFilterChanged();
    partial void OnFilterSuspiciousChanged(bool value) => OnFilterChanged();

    private void OnFilterChanged()
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(AnyFilterActive));
    }

    public bool AnyFilterActive => FilterPosition || FilterQuantity || FilterVolume
        || FilterRenamed || FilterAdded || FilterRemoved || FilterSuspicious;

    [RelayCommand]
    private void ClearFilters()
    {
        FilterPosition = FilterQuantity = FilterVolume = FilterRenamed
            = FilterAdded = FilterRemoved = FilterSuspicious = false;
    }

    private bool MatchesFilters(object item)
    {
        var r = (AssemblyComponentDiffRowViewModel)item;
        if (FilterPosition && !r.HasPositionChange) return false;
        if (FilterQuantity && !r.HasQuantityChange) return false;
        if (FilterVolume && !r.HasVolumeChange) return false;
        if (FilterRenamed && !r.IsRenamed) return false;
        if (FilterAdded && r.Diff.DiffType != AssemblyDiffType.Added) return false;
        if (FilterRemoved && r.Diff.DiffType != AssemblyDiffType.Removed) return false;
        if (FilterSuspicious && r.Diff.DiffType != AssemblyDiffType.SuspiciousMatch) return false;
        return true;
    }

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
        _summary = summary;
        _pathA = pathA;
        _pathB = pathB;
        _exporter = exporter;
        _logger = logger;

        Rows = new ObservableCollection<AssemblyComponentDiffRowViewModel>(
            summary.Components.Select(c => new AssemblyComponentDiffRowViewModel(c)));

        // Grouped ("Changed" first, then "Unchanged"), alphabetical within a group, and filtered
        // by the toggle bar. Column-header sorting is disabled in the view (it was confusing);
        // ordering is fixed here instead.
        var cvs = new System.Windows.Data.CollectionViewSource { Source = Rows };
        cvs.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(AssemblyComponentDiffRowViewModel.Group)));
        cvs.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(AssemblyComponentDiffRowViewModel.GroupRank), System.ComponentModel.ListSortDirection.Ascending));
        cvs.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(AssemblyComponentDiffRowViewModel.MatchKey), System.ComponentModel.ListSortDirection.Ascending));
        RowsView = cvs.View;
        RowsView.Filter = MatchesFilters;

        // A two-sided part "changed" if it has any detected Position/Quantity/Volume difference
        // (Modified always implies a volume change; an Unchanged-shape part can still be flagged
        // via quantity or position). Suspicious/Added/Removed are counted on their own.
        static bool AnyChange(AssemblyComponentDiff c) =>
            c.DiffType == AssemblyDiffType.Modified || c.QuantityChanged || c.PositionChanged == true;

        UnchangedCount = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Unchanged && !AnyChange(c));
        ChangedCount = summary.Components.Count(c =>
            c.DiffType == AssemblyDiffType.Modified ||
            (c.DiffType == AssemblyDiffType.Unchanged && AnyChange(c)));
        AddedCount = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Added);
        RemovedCount = summary.Components.Count(c => c.DiffType == AssemblyDiffType.Removed);
        SuspiciousCount = summary.Components.Count(c => c.DiffType == AssemblyDiffType.SuspiciousMatch);

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
            Title = "Export Assembly Diff Report",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
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
