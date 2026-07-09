using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using System.IO;

namespace SolidWorksPartMatcher.App.ViewModels;

public sealed partial class MatchFileViewModel : ObservableObject
{
    public Guid ScannedFileId { get; }
    public string FileName { get; }
    public string FullPath { get; }
    public string? ConfigurationName { get; }
    public string SourceFormat { get; }

    // ── Per-file facts, surfaced in the Match Details dialog ─────────────────────────────────
    public int FaceCount { get; }
    public double VolumeM3 { get; }

    /// <summary>Engraved text features on this part (0 = none). Always 0 for STEP (no feature tree).</summary>
    public int EngravedTextCount { get; }

    /// <summary>True when this part's hole was cut with the Hole Wizard rather than a plain cut extrude.</summary>
    public bool HasHoleWizard { get; }

    /// <summary>True when this part has a plain (non-Hole-Wizard) cut feature.</summary>
    public bool HasPlainCut { get; }

    public bool IsStepFile =>
        string.Equals(SourceFormat, "STEP", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How this part's cut was modelled — only meaningful for SOLIDWORKS parts. Three distinct
    /// states: a part with no cut features at all must not be reported as using a plain cut extrude,
    /// which is what a plain <c>HasHoleWizard ? … : …</c> would wrongly claim.
    /// </summary>
    public string HoleSpecLabel =>
        HasHoleWizard ? "Hole Wizard hole"
        : HasPlainCut ? "plain cut extrude"
        : "no cut features";

    public string EngravingLabel =>
        EngravedTextCount > 0 ? $"{EngravedTextCount} engraved text feature(s)" : "none";

    /// <summary>Folder holding this part. The file name is shown separately, so it isn't repeated here.</summary>
    public string DirectoryPath => Path.GetDirectoryName(FullPath) ?? FullPath;

    /// <summary>Essential geometry values, shown for every part.</summary>
    public string GeometryLine => $"Faces {FaceCount} · Volume {VolumeM3 * 1e6:0.##} cm³";

    /// <summary>
    /// Feature-derived facts (hole specification, engraving). Null for STEP, which has no feature
    /// tree, so the details dialog simply omits the line rather than printing meaningless values.
    /// </summary>
    public string? FeatureLine =>
        IsStepFile ? null : $"Hole: {HoleSpecLabel} · Engraving: {EngravingLabel}";

    // Shown in the UI when config is not the default.
    public bool HasNonDefaultConfig =>
        !string.IsNullOrEmpty(ConfigurationName) &&
        !string.Equals(ConfigurationName, "Default", StringComparison.OrdinalIgnoreCase);

    public string OpenButtonLabel => IsStepFile ? "Open File" : "Open in SOLIDWORKS";
    public string OpenSwAutomationName => IsStepFile ? $"Open {FileName}" : $"Open {FileName} in SOLIDWORKS";
    public string OpenFolderAutomationName => $"Open folder containing {FileName}";

    [ObservableProperty] private string? _openError;

    public IAsyncRelayCommand OpenInSolidWorksCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }

    private readonly ISolidWorksFileOpener _opener;
    private readonly ILogger<MatchFileViewModel> _logger;

    public MatchFileViewModel(
        Guid scannedFileId,
        string fullPath,
        string? configurationName,
        string sourceFormat,
        PartFingerprint fingerprint,
        ISolidWorksFileOpener opener,
        ILogger<MatchFileViewModel> logger)
    {
        ScannedFileId = scannedFileId;
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        ConfigurationName = configurationName;
        SourceFormat = sourceFormat;
        FaceCount = fingerprint.FaceCount;
        VolumeM3 = fingerprint.VolumeM3;
        EngravedTextCount = PartFeatureFacts.EngravedTextCount(fingerprint);
        HasHoleWizard = PartFeatureFacts.HasHoleWizard(fingerprint);
        HasPlainCut = PartFeatureFacts.HasPlainCutFeature(fingerprint);
        _opener = opener;
        _logger = logger;

        OpenInSolidWorksCommand = new AsyncRelayCommand(DoOpenInSolidWorksAsync);
        OpenFolderCommand = new RelayCommand(DoOpenFolder);
    }

    /// <summary>Called from OpenAllCommand in the parent group.</summary>
    internal Task OpenAsync(CancellationToken ct = default)
        => DoOpenInSolidWorksAsync(ct);

    private async Task DoOpenInSolidWorksAsync(CancellationToken ct = default)
    {
        OpenError = null;

        if (!File.Exists(FullPath))
        {
            OpenError = $"File no longer exists: {FileName}";
            _logger.LogWarning("File not found: {Path}", FullPath);
            return;
        }

        // STEP files: open with OS default handler (avoids SW silent-open failure).
        if (IsStepFile)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(FullPath) { UseShellExecute = true });
                _logger.LogInformation("Opened {Path} via OS default handler", FullPath);
            }
            catch (Exception ex)
            {
                OpenError = $"Cannot open file: {ex.Message}";
                _logger.LogError(ex, "Shell-execute failed for {Path}", FullPath);
            }
            return;
        }

        try
        {
            _logger.LogInformation("Opening {Path} config={Config}", FullPath, ConfigurationName);
            await _opener.OpenFileAsync(FullPath, ConfigurationName, ct);
            _logger.LogInformation("Opened {Path}", FullPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Open cancelled for {Path}", FullPath);
        }
        catch (Exception ex)
        {
            OpenError = $"Cannot open file: {ex.Message}";
            _logger.LogError(ex, "Failed to open {Path} config={Config}", FullPath, ConfigurationName);
        }
    }

    private void DoOpenFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(FullPath);
            if (File.Exists(FullPath))
                System.Diagnostics.Process.Start(
                    "explorer.exe", $"/select,\"{FullPath}\"");
            else if (dir != null && Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
            else
                OpenError = "Folder no longer exists.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenFolder failed for {Path}", FullPath);
        }
    }
}
