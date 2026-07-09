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

    /// <summary>
    /// Why this group was declared a match — the distinct classification reasons of the pair
    /// comparisons among its members, strongest evidence first (e.g. "SHA-256 match", "Proper rigid
    /// transform confirmed (det(R)=1.000000)", "Under review — 3/4 geometry signals agree: …").
    /// These are the raw/technical strings; the user-facing popup uses <see cref="FriendlyMatchReason"/>.
    /// </summary>
    public IReadOnlyList<string> MatchReasons { get; }

    /// <summary>
    /// A plain-language explanation of why the parts were grouped, written for a non-technical user.
    /// Shown in the "Why was this matched?" popup. Built from the classification, plus a friendly
    /// rendering of the geometry-vote evidence when present.
    /// </summary>
    public string FriendlyMatchReason
    {
        get
        {
            string baseMsg = Classification switch
            {
                PartClassification.BinaryDuplicate =>
                    "These files are exact, byte-for-byte identical copies of each other.",
                PartClassification.ExactGeometryMatch =>
                    "These parts have the same shape and size.",
                PartClassification.GeometryMatchMetadataVariant =>
                    "These parts have the same shape and size, but differ in a detail such as material.",
                PartClassification.EngravingVariant =>
                    "These are the same part, differing only by an engraving or marking on the surface.",
                PartClassification.RevisionFamily =>
                    "These are closely related versions of the same part, with only small size differences.",
                PartClassification.MirrorOrHandedVariant =>
                    "These parts are mirror images of each other — like a left-hand and a right-hand version.",
                PartClassification.PossibleMatch =>
                    "These parts look very similar and may be the same. Please review them to confirm.",
                PartClassification.ComparisonFailed =>
                    "These parts couldn't be fully compared, so they've been grouped for you to check.",
                _ => "These parts were grouped together for review.",
            };

            var evidence = FriendlyEvidence();
            return evidence is null ? baseMsg : $"{baseMsg}\n\nWhat matched:\n{evidence}";
        }
    }

    // Translates the geometry-vote reason (if any) into plain bullet points. Returns null when the
    // group wasn't decided by the vote (e.g. an exact/binary match), so only relevant groups show it.
    private string? FriendlyEvidence()
    {
        var vote = MatchReasons.FirstOrDefault(
            r => r.Contains("signals agree", StringComparison.OrdinalIgnoreCase));
        if (vote is null) return null;

        var bullets = new List<string>();
        if (vote.Contains("volume", StringComparison.OrdinalIgnoreCase))
            bullets.Add("• They take up almost exactly the same amount of space.");
        if (vote.Contains("face count", StringComparison.OrdinalIgnoreCase))
            bullets.Add("• They have the same number of surfaces.");
        if (vote.Contains("face-type", StringComparison.OrdinalIgnoreCase))
            bullets.Add("• They're built from the same kinds of surfaces (flat, round, etc.).");
        if (vote.Contains("signature", StringComparison.OrdinalIgnoreCase))
            bullets.Add("• Their surfaces are shaped and sized almost identically.");

        return bullets.Count == 0 ? null : string.Join("\n", bullets);
    }

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
        ILogger<MatchGroupViewModel> logger,
        IReadOnlyList<string>? matchReasons = null)
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
        MatchReasons = matchReasons ?? [];
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

    /// <summary>
    /// Single "Match Details" dialog — replaces the former separate "Why was this matched?" and
    /// "View Details" popups. Leads with the plain-language explanation, then calls out the two
    /// differences a user most needs named per file (hole specification, engraving), then the
    /// essential per-file values, and finally the raw technical evidence for anyone who wants it.
    /// </summary>
    [RelayCommand]
    private void ShowMatchDetails()
    {
        WpfMsgBox.Show(
            BuildMatchDetailsText(),
            $"Match Details — {DisplayName}",
            WpfMsgBoxButton.OK,
            WpfMsgBoxImage.Information);
    }

    internal string BuildMatchDetailsText()
    {
        var lines = new List<string>
        {
            $"{DisplayName} — {ClassificationLabel}",
            $"Name: {CanonicalName ?? "(none)"}      Review status: {ReviewStatus}      Parts: {Files.Count}",
            "",
            "WHY THESE WERE GROUPED",
            FriendlyMatchReason,
        };

        // Hole specification — only a difference is worth calling out, and the user needs to know
        // exactly which file is which. Meaningless for STEP (no feature tree), so ignore those.
        var swFiles = Files.Where(f => !f.IsStepFile).ToList();
        var wizard = swFiles.Where(f => f.HasHoleWizard).ToList();
        var plainCut = swFiles.Where(f => !f.HasHoleWizard).ToList();
        if (wizard.Count > 0 && plainCut.Count > 0)
        {
            lines.Add("");
            lines.Add("HOLE SPECIFICATION — these differ");
            lines.AddRange(wizard.Select(f => $"  • {f.FileName}: Hole Wizard hole"));
            lines.AddRange(plainCut.Select(f => $"  • {f.FileName}: plain cut extrude"));
            lines.Add("  The holes may sit in the same place, but they were modelled differently,");
            lines.Add("  so these are treated as different engineering specifications.");
        }

        // Engraving — list every SW part so "has none" is explicit, not just absent.
        if (swFiles.Any(f => f.EngravedTextCount > 0))
        {
            lines.Add("");
            lines.Add("ENGRAVING");
            lines.AddRange(swFiles.Select(f => $"  • {f.FileName}: {f.EngravingLabel}"));
        }

        lines.Add("");
        lines.Add("PARTS");
        foreach (var f in Files)
        {
            var cfg = f.HasNonDefaultConfig ? $"  [{f.ConfigurationName}]" : "";
            lines.Add($"  {f.FileName}{cfg}");
            lines.Add($"     {f.GeometrySummary}");
            lines.Add($"     {f.FullPath}");
        }

        if (MatchReasons.Count > 0)
        {
            lines.Add("");
            lines.Add("TECHNICAL EVIDENCE");
            lines.AddRange(MatchReasons.Select(r => $"  • {r}"));
        }

        return string.Join("\n", lines);
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
