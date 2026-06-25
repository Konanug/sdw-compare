using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using System.IO;

namespace SolidWorksPartMatcher.App.ViewModels;

public sealed partial class MatchFileViewModel : ObservableObject
{
    public Guid ScannedFileId { get; }
    public string FileName { get; }
    public string FullPath { get; }
    public string? ConfigurationName { get; }

    // Shown in the UI when config is not the default.
    public bool HasNonDefaultConfig =>
        !string.IsNullOrEmpty(ConfigurationName) &&
        !string.Equals(ConfigurationName, "Default", StringComparison.OrdinalIgnoreCase);

    // Accessible names for screen readers.
    public string OpenSwAutomationName    => $"Open {FileName} in SOLIDWORKS";
    public string OpenFolderAutomationName => $"Open folder containing {FileName}";

    [ObservableProperty] private string? _openError;

    public IAsyncRelayCommand OpenInSolidWorksCommand { get; }
    public IRelayCommand       OpenFolderCommand      { get; }

    private readonly ISolidWorksFileOpener _opener;
    private readonly ILogger<MatchFileViewModel> _logger;

    public MatchFileViewModel(
        Guid scannedFileId,
        string fullPath,
        string? configurationName,
        ISolidWorksFileOpener opener,
        ILogger<MatchFileViewModel> logger)
    {
        ScannedFileId     = scannedFileId;
        FullPath          = fullPath;
        FileName          = Path.GetFileName(fullPath);
        ConfigurationName = configurationName;
        _opener           = opener;
        _logger           = logger;

        OpenInSolidWorksCommand = new AsyncRelayCommand(DoOpenInSolidWorksAsync);
        OpenFolderCommand       = new RelayCommand(DoOpenFolder);
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
