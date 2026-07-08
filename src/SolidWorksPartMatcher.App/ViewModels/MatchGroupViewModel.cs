using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using WpfMsgBox = System.Windows.MessageBox;
using WpfMsgBoxButton = System.Windows.MessageBoxButton;
using WpfMsgBoxImage = System.Windows.MessageBoxImage;
using WpfMsgBoxResult = System.Windows.MessageBoxResult;
using SolidWorksPartMatcher.Application.Services;
using SolidWorksPartMatcher.Domain.Models;
using System.Collections.ObjectModel;

namespace SolidWorksPartMatcher.App.ViewModels;

public sealed partial class MatchGroupViewModel : ObservableObject
{
    public Guid ClusterId { get; }

    /// <summary>"Match 1", "Match 2", etc. — deterministic, scan-scoped.</summary>
    public string DisplayName { get; }

    [ObservableProperty] private string? _canonicalName;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _statusMessage;

    public PartClassification Classification { get; }
    public string ClassificationLabel { get; }

    private ReviewStatus _reviewStatus;
    public ReviewStatus ReviewStatus
    {
        get => _reviewStatus;
        private set
        {
            if (SetProperty(ref _reviewStatus, value))
                OnPropertyChanged(nameof(HeaderBackground));
        }
    }

    public ObservableCollection<MatchFileViewModel> Files { get; }

    // Shown in the group header next to the display name.
    public string FileCountLabel =>
        $"{Files.Count} part{(Files.Count == 1 ? "" : "s")}";

    // Background brush for the group header row, driven by review status:
    //   Approved → green · Rejected → red · Pending/NeedsReview → light grey
    public System.Windows.Media.Brush HeaderBackground => _reviewStatus switch
    {
        ReviewStatus.Approved => BrushApproved,
        ReviewStatus.Rejected => BrushRejected,
        _ => BrushPending,
    };

    private static readonly System.Windows.Media.Brush BrushApproved = MakeBrush("#E8F5E9");
    private static readonly System.Windows.Media.Brush BrushRejected = MakeBrush("#FFEBEE");
    private static readonly System.Windows.Media.Brush BrushPending = MakeBrush("#F5F5F5");

    private static System.Windows.Media.Brush MakeBrush(string hex)
    {
        var b = (System.Windows.Media.SolidColorBrush)
            new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;
        b.Freeze();
        return b;
    }

    // Accessible name for the group row (screen readers).
    public string AutomationName =>
        $"{DisplayName}, {ClassificationLabel}, {Files.Count} file{(Files.Count == 1 ? "" : "s")}";

    public bool IsStepGroup => Files.Count(f => f.IsStepFile) >= 2;

    private readonly IPartRepository _repo;
    private readonly ILogger<MatchGroupViewModel> _logger;

    public MatchGroupViewModel(
        PartCluster cluster,
        string displayName,
        IReadOnlyList<MatchFileViewModel> files,
        IPartRepository repo,
        ILogger<MatchGroupViewModel> logger)
    {
        ClusterId = cluster.Id;
        DisplayName = displayName;
        _canonicalName = cluster.CanonicalName;
        Classification = cluster.Classification;
        ClassificationLabel = MatchGroupFilter.ToLabel(cluster.Classification);
        _reviewStatus = cluster.ReviewStatus;
        _repo = repo;
        _logger = logger;
        Files = new ObservableCollection<MatchFileViewModel>(files);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Filtering
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Returns true when this group satisfies the current search/filter state.</summary>
    public bool MatchesFilter(
        string? searchText,
        PartClassification? cls,
        ReviewStatus? status)
        => MatchGroupFilter.MatchesFilter(
            CanonicalName, Classification, ReviewStatus,
            Files.Select(f => (f.FileName, f.FullPath)),
            searchText, cls, status);

    // ──────────────────────────────────────────────────────────────────────────
    // Commands
    // ──────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ApproveAsync()
    {
        try
        {
            await _repo.UpdateClusterReviewAsync(
                ClusterId, ReviewStatus.Approved, null,
                Environment.UserName, DateTime.UtcNow, CancellationToken.None);
            ReviewStatus = ReviewStatus.Approved;
            StatusMessage = "Approved.";
            _logger.LogInformation("Cluster {Id} approved by {User}", ClusterId, Environment.UserName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Approve failed: {ex.Message}";
            _logger.LogError(ex, "Approve failed for cluster {Id}", ClusterId);
        }
    }

    [RelayCommand]
    private async Task ResetStatusAsync()
    {
        try
        {
            await _repo.UpdateClusterReviewAsync(
                ClusterId, ReviewStatus.Pending, null,
                Environment.UserName, DateTime.UtcNow, CancellationToken.None);
            ReviewStatus = ReviewStatus.Pending;
            StatusMessage = "Reset to pending.";
            _logger.LogInformation("Cluster {Id} reset to pending by {User}", ClusterId, Environment.UserName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reset failed: {ex.Message}";
            _logger.LogError(ex, "Reset failed for cluster {Id}", ClusterId);
        }
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        try
        {
            await _repo.UpdateClusterReviewAsync(
                ClusterId, ReviewStatus.Rejected, "Rejected by reviewer",
                Environment.UserName, DateTime.UtcNow, CancellationToken.None);
            ReviewStatus = ReviewStatus.Rejected;
            StatusMessage = "Rejected.";
            _logger.LogInformation("Cluster {Id} rejected by {User}", ClusterId, Environment.UserName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reject failed: {ex.Message}";
            _logger.LogError(ex, "Reject failed for cluster {Id}", ClusterId);
        }
    }

    [RelayCommand]
    private async Task EditCanonicalNameAsync()
    {
        var dlg = new Views.RenameDialog
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dlg.NewName = CanonicalName ?? "";
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _repo.UpdateClusterCanonicalNameAsync(
                ClusterId, dlg.NewName, CancellationToken.None);
            CanonicalName = dlg.NewName;
            StatusMessage = $"Renamed to '{dlg.NewName}'.";
            _logger.LogInformation("Cluster {Id} renamed to '{Name}'", ClusterId, dlg.NewName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rename failed: {ex.Message}";
            _logger.LogError(ex, "Rename failed for cluster {Id}", ClusterId);
        }
    }

    [RelayCommand]
    private void ViewDetails()
    {
        var lines = new[]
        {
            $"Group:          {DisplayName}",
            $"Canonical Name: {CanonicalName ?? "(none)"}",
            $"Classification: {ClassificationLabel}",
            $"Review Status:  {ReviewStatus}",
            $"Files:          {Files.Count}",
            "",
            "Files:",
        }.Concat(Files.Select(f =>
            string.IsNullOrEmpty(f.ConfigurationName) || !f.HasNonDefaultConfig
                ? $"  {f.FullPath}"
                : $"  {f.FullPath}  [{f.ConfigurationName}]"));

        WpfMsgBox.Show(
            string.Join("\n", lines),
            $"Details — {DisplayName}",
            WpfMsgBoxButton.OK,
            WpfMsgBoxImage.Information);
    }

    [RelayCommand]
    private async Task OpenAllAsync()
    {
        const int ConfirmThreshold = 5;
        if (Files.Count > ConfirmThreshold)
        {
            var result = WpfMsgBox.Show(
                $"Open all {Files.Count} files in SOLIDWORKS?",
                "Confirm Open All",
                WpfMsgBoxButton.YesNo,
                WpfMsgBoxImage.Question);
            if (result != WpfMsgBoxResult.Yes) return;
        }

        // Open files sequentially. Stop on first error.
        using var cts = new CancellationTokenSource();
        foreach (var file in Files)
        {
            if (cts.IsCancellationRequested) break;
            await file.OpenAsync(cts.Token);

            // If the file VM recorded an error, stop the sequence.
            if (!string.IsNullOrEmpty(file.OpenError))
            {
                _logger.LogWarning(
                    "OpenAll stopped after error on {File}: {Err}",
                    file.FileName, file.OpenError);
                break;
            }
        }
    }

    [RelayCommand]
    private void ViewStepDiff()
    {
        var stepPaths = Files.Where(f => f.IsStepFile).Select(f => f.FullPath).ToList();
        if (stepPaths.Count < 2) return;
        var win = new Views.StepDiffWindow(DisplayName, stepPaths)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        win.Show();
    }
}
