using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Application.Services;
using SolidWorksPartMatcher.Domain.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using WinForms = System.Windows.Forms;
using WpfMsgBox = System.Windows.MessageBox;
using WpfMsgBoxButton = System.Windows.MessageBoxButton;
using WpfMsgBoxImage = System.Windows.MessageBoxImage;
using WpfMsgBoxResult = System.Windows.MessageBoxResult;
using System.Windows.Data;
using System.Windows.Threading;

namespace SolidWorksPartMatcher.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ── Services ──────────────────────────────────────────────────────────────

    private readonly IScanOrchestrationService _scanner;
    private readonly IPartRepository _repo;
    private readonly IWorkbookExporter _exporter;
    private readonly ISolidWorksFileOpener _opener;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // ── Scan state ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _statusText = "Add folders and click Start Scan.";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 100;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _canExport;
    [ObservableProperty] private string _scanStage = "";

    partial void OnCanExportChanged(bool value)
    {
        ExportAllCommand.NotifyCanExecuteChanged();
        ExportVisibleCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsScanningChanged(bool value)
    {
        // Folder editing is disabled while a scan is running, re-enabled when it finishes.
        AddFolderCommand.NotifyCanExecuteChanged();
        RemoveFolderCommand.NotifyCanExecuteChanged();
        RemoveAllFoldersCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource? _cts;
    private ScanRun? _lastRun;
    private bool _hasScanned;

    // Cached result sets for export.
    private IReadOnlyList<PartCluster> _lastClusters = [];
    private IReadOnlyList<ScannedFile> _lastFiles = [];
    private IReadOnlyList<PartFingerprint> _lastFps = [];
    private IReadOnlyList<ClusterMember> _lastMembers = [];
    private IReadOnlyList<CandidatePair> _lastPairs = [];

    // ── Folders ───────────────────────────────────────────────────────────────

    public ObservableCollection<string> FolderPaths { get; } = [];

    // ── Match groups ──────────────────────────────────────────────────────────

    private readonly ObservableCollection<MatchGroupViewModel> _groups = [];
    private ICollectionView? _filteredGroupsView;

    public IReadOnlyList<MatchGroupViewModel> Groups => _groups;

    /// <summary>Filtered view bound to the results TreeView.</summary>
    public ICollectionView FilteredGroups
    {
        get
        {
            if (_filteredGroupsView is null)
            {
                _filteredGroupsView = CollectionViewSource.GetDefaultView(_groups);
                _filteredGroupsView.Filter = FilterGroupObject;
            }
            return _filteredGroupsView;
        }
    }

    private bool FilterGroupObject(object obj)
        => obj is MatchGroupViewModel g
        && g.Classification != PartClassification.Distinct
        && g.MatchesFilter(SearchText, ClassificationFilter, ReviewStatusFilter);

    private void RefreshFilter()
    {
        FilteredGroups.Refresh();
        OnPropertyChanged(nameof(ShowNoFilterResultsState));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    // ── Search & filter state ─────────────────────────────────────────────────

    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private PartClassification? _classificationFilter;
    [ObservableProperty] private ReviewStatus? _reviewStatusFilter;

    [ObservableProperty] private ClassificationOption? _selectedClassificationOption;
    [ObservableProperty] private ReviewStatusOption? _selectedReviewStatusOption;

    partial void OnSearchTextChanged(string? value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedClassificationOptionChanged(ClassificationOption? value)
    {
        ClassificationFilter = value?.Value;
        RefreshFilter();
    }

    partial void OnSelectedReviewStatusOptionChanged(ReviewStatusOption? value)
    {
        ReviewStatusFilter = value?.Value;
        RefreshFilter();
    }

    private readonly DispatcherTimer _searchDebounce;

    // ── Filter combo options ──────────────────────────────────────────────────

    public IReadOnlyList<ClassificationOption> ClassificationOptions { get; } =
    [
        new("All Classifications",                    null),
        new("Geometry Match (Identical Copy)",         PartClassification.BinaryDuplicate),
        new("Geometry Match",                          PartClassification.ExactGeometryMatch),
        new("Geometry Match (Metadata Variant)",       PartClassification.GeometryMatchMetadataVariant),
        new("Geometry Match (Engraving Variant)",      PartClassification.EngravingVariant),
        new("Mirror / Handed Variant",                 PartClassification.MirrorOrHandedVariant),
        new("Revision Family",                         PartClassification.RevisionFamily),
        new("Possible Match (Needs Review)",           PartClassification.PossibleMatch),
        new("Comparison Failed",                       PartClassification.ComparisonFailed),
    ];

    public IReadOnlyList<ReviewStatusOption> ReviewStatusOptions { get; } =
    [
        new("All Statuses",          null),
        new("Pending / Needs Review", ReviewStatus.Pending),
        new("Approved",              ReviewStatus.Approved),
        new("Rejected",              ReviewStatus.Rejected),
    ];

    // ── Empty-state flags ─────────────────────────────────────────────────────

    public bool ShowNoScanState => !_hasScanned && !IsScanning;
    public bool ShowNoMatchesState => _hasScanned && _groups.Count == 0;
    public bool ShowNoFilterResultsState =>
        _hasScanned && _groups.Count > 0 && !FilteredGroups.Cast<object>().Any();
    public bool ShowEmptyState =>
        ShowNoScanState || ShowNoMatchesState || ShowNoFilterResultsState;

    // ──────────────────────────────────────────────────────────────────────────
    // Constructor
    // ──────────────────────────────────────────────────────────────────────────

    public MainViewModel(
        IScanOrchestrationService scanner,
        IPartRepository repo,
        IWorkbookExporter exporter,
        ISolidWorksFileOpener opener,
        ILoggerFactory loggerFactory)
    {
        _scanner = scanner;
        _repo = repo;
        _exporter = exporter;
        _opener = opener;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainViewModel>();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ClassificationFilter = SelectedClassificationOption?.Value;
            ReviewStatusFilter = SelectedReviewStatusOption?.Value;
            RefreshFilter();
        };

        _selectedClassificationOption = ClassificationOptions[0];
        _selectedReviewStatusOption = ReviewStatusOptions[0];
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Folder commands
    // ──────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanEditFolders))]
    private void AddFolder()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder containing .SLDPRT or .STEP/.STP files",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        if (!FolderPaths.Contains(dialog.SelectedPath))
            FolderPaths.Add(dialog.SelectedPath);

        // The folder set changed, so any results from a previous scan no longer match it.
        if (_hasScanned)
            InvalidateResults("Folders changed — click Start Scan to refresh results.");
    }

    [RelayCommand(CanExecute = nameof(CanEditFolders))]
    private void RemoveFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        FolderPaths.Remove(path);

        // Removing a folder invalidates results that were scanned from it — clear them so the
        // view reflects the current folder set rather than leaving stale parts on screen.
        if (_hasScanned)
            InvalidateResults("Folders changed — click Start Scan to refresh results.");
    }

    [RelayCommand(CanExecute = nameof(CanEditFolders))]
    private void RemoveAllFolders()
    {
        FolderPaths.Clear();
        InvalidateResults("Add folders and click Start Scan.");
    }

    // Folders may be edited any time except while a scan is actively running.
    private bool CanEditFolders() => !IsScanning;

    // ──────────────────────────────────────────────────────────────────────────
    // Scan command
    // ──────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (FolderPaths.Count == 0)
        {
            WpfMsgBox.Show("Add at least one folder first.", "No Folders",
                WpfMsgBoxButton.OK, WpfMsgBoxImage.Information);
            return;
        }

        IsScanning = true;
        CanExport = false;
        _groups.Clear();
        _cts = new CancellationTokenSource();
        _hasScanned = false;
        OnPropertyChanged(nameof(ShowNoScanState));
        OnPropertyChanged(nameof(ShowEmptyState));

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanStage = p.Stage;
            StatusText = $"[{p.Stage}] {p.Detail}";
            if (p.Total > 0) { ProgressMax = p.Total; ProgressValue = p.Current; }
        });

        try
        {
            _lastRun = await _scanner.RunScanAsync(
                FolderPaths.ToList(), null, progress, _cts.Token);

            await LoadGroupsAsync(_lastRun.Id);

            var visibleCount = FilteredGroups.Cast<object>().Count();
            var distinctCount = _groups.Count - visibleCount;
            StatusText = distinctCount > 0
                ? $"Scan complete — {visibleCount} match group{(visibleCount == 1 ? "" : "s")} found ({distinctCount} confirmed distinct, hidden)."
                : $"Scan complete — {visibleCount} match group{(visibleCount == 1 ? "" : "s")} found.";
            CanExport = true;
            _hasScanned = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _logger.LogError(ex, "Scan failed");
            WpfMsgBox.Show(ex.Message, "Scan Error", WpfMsgBoxButton.OK, WpfMsgBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(ShowNoScanState));
            OnPropertyChanged(nameof(ShowNoMatchesState));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private bool CanStartScan() => !IsScanning;

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    // ──────────────────────────────────────────────────────────────────────────
    // Group loading
    // ──────────────────────────────────────────────────────────────────────────

    private async Task LoadGroupsAsync(Guid scanRunId)
    {
        var clusters = await _repo.GetClustersAsync(scanRunId, CancellationToken.None);
        var files = await _repo.GetAllScannedFilesAsync(scanRunId, CancellationToken.None);
        var fingerprints = await _repo.GetAllFingerprintsAsync(scanRunId, CancellationToken.None);
        var pairs = await _repo.GetCandidatePairsAsync(scanRunId, CancellationToken.None);

        var fileById = files.ToDictionary(f => f.Id);
        var fpById = fingerprints.ToDictionary(f => f.Id);

        var allMembers = new List<ClusterMember>();
        foreach (var cluster in clusters)
            allMembers.AddRange(
                await _repo.GetClusterMembersAsync(cluster.Id, CancellationToken.None));

        var membersByCluster = allMembers
            .GroupBy(m => m.ClusterId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ClusterMember>)g.ToList());

        _lastClusters = clusters;
        _lastFiles = files;
        _lastFps = fingerprints;
        _lastMembers = allMembers;
        _lastPairs = pairs;

        var sorted = clusters
            .OrderBy(c => c.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id)
            .ToList();

        _groups.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            var cluster = sorted[i];
            var displayName = $"Match {i + 1}";
            var members = membersByCluster.GetValueOrDefault(cluster.Id, []);

            var fileVms = members
                .Select(m =>
                {
                    if (!fpById.TryGetValue(m.FingerprintId, out var fp)) return null;
                    if (!fileById.TryGetValue(fp.ScannedFileId, out var sf)) return null;
                    return new MatchFileViewModel(
                        sf.Id, sf.NormalizedPath, fp.ConfigName,
                        fp.SourceFormat,
                        _opener,
                        _loggerFactory.CreateLogger<MatchFileViewModel>());
                })
                .Where(vm => vm != null)
                .Cast<MatchFileViewModel>()
                .ToList();

            _groups.Add(new MatchGroupViewModel(
                cluster, displayName, fileVms, _repo,
                _loggerFactory.CreateLogger<MatchGroupViewModel>()));
        }

        RefreshFilter();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Expand / Collapse / Clear filters
    // ──────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var g in _groups) g.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var g in _groups) g.IsExpanded = false;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _searchDebounce.Stop();
        SearchText = "";
        SelectedClassificationOption = ClassificationOptions[0];
        SelectedReviewStatusOption = ReviewStatusOptions[0];
        ClassificationFilter = null;
        ReviewStatusFilter = null;

        // Clear all loaded scan data — returns to fresh-open state.
        InvalidateResults("Add folders and click Start Scan.");
    }

    /// <summary>
    /// Discards the currently loaded scan results and returns to the pre-scan state, updating the
    /// status text. Used when the user clears filters, or when the folder set changes after a scan
    /// (which makes the displayed results stale). Does not touch <see cref="FolderPaths"/>.
    /// </summary>
    private void InvalidateResults(string status)
    {
        _groups.Clear();
        _lastClusters = [];
        _lastFiles = [];
        _lastFps = [];
        _lastMembers = [];
        _lastPairs = [];
        _lastRun = null;
        _hasScanned = false;
        CanExport = false;
        ScanStage = "";
        StatusText = status;

        RefreshFilter();
        OnPropertyChanged(nameof(ShowNoScanState));
        OnPropertyChanged(nameof(ShowNoMatchesState));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Export commands
    // ──────────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAllAsync()
    {
        if (_lastRun == null) return;
        var path = PromptForSavePath();
        if (path is null) return;

        try
        {
            var ctx = BuildExportContext(_lastClusters, _lastMembers);
            await _exporter.ExportAsync(ctx, path, CancellationToken.None);
            StatusText = $"Exported to {Path.GetFileName(path)}";
            OpenExternalFile(path);
        }
        catch (Exception ex)
        {
            WpfMsgBox.Show(ex.Message, "Export Error", WpfMsgBoxButton.OK, WpfMsgBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportVisibleAsync()
    {
        if (_lastRun == null) return;
        var path = PromptForSavePath();
        if (path is null) return;

        try
        {
            var visibleIds = FilteredGroups
                .Cast<MatchGroupViewModel>()
                .Select(g => g.ClusterId)
                .ToHashSet();

            var visibleClusters = _lastClusters
                .Where(c => visibleIds.Contains(c.Id))
                .ToList();

            var visibleMembers = _lastMembers
                .Where(m => visibleIds.Contains(m.ClusterId))
                .ToList();

            var visibleFpIds = visibleMembers
                .Select(m => m.FingerprintId)
                .ToHashSet();

            var visibleFileIds = _lastFps
                .Where(fp => visibleFpIds.Contains(fp.Id))
                .Select(fp => fp.ScannedFileId)
                .ToHashSet();

            var ctx = BuildExportContext(
                visibleClusters,
                visibleMembers,
                _lastFiles.Where(f => visibleFileIds.Contains(f.Id)).ToList(),
                _lastFps.Where(fp => visibleFpIds.Contains(fp.Id)).ToList(),
                _lastPairs.Where(p =>
                    visibleFpIds.Contains(p.FingerprintAId) ||
                    visibleFpIds.Contains(p.FingerprintBId)).ToList());

            await _exporter.ExportAsync(ctx, path, CancellationToken.None);
            StatusText = $"Exported {visibleClusters.Count} visible group(s) to {Path.GetFileName(path)}";
            OpenExternalFile(path);
        }
        catch (Exception ex)
        {
            WpfMsgBox.Show(ex.Message, "Export Error", WpfMsgBoxButton.OK, WpfMsgBoxImage.Error);
        }
    }

    private ExportContext BuildExportContext(
        IReadOnlyList<PartCluster> clusters,
        IReadOnlyList<ClusterMember> members,
        IReadOnlyList<ScannedFile>? files = null,
        IReadOnlyList<PartFingerprint>? fps = null,
        IReadOnlyList<CandidatePair>? pairs = null)
        => new(_lastRun!,
               files ?? _lastFiles,
               fps ?? _lastFps,
               clusters,
               members,
               pairs ?? _lastPairs);

    private static string? PromptForSavePath()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Excel Workbook",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"PartMatcher_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static void OpenExternalFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* non-critical */ }
    }
}
