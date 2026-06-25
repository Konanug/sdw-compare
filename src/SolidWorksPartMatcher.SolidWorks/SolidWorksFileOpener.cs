using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksPartMatcher.Application.Interfaces;

namespace SolidWorksPartMatcher.SolidWorks;

/// <summary>
/// Opens .SLDPRT files in SOLIDWORKS for interactive viewing.
/// All COM calls are routed through the dedicated STA worker.
/// </summary>
public sealed class SolidWorksFileOpener : ISolidWorksFileOpener
{
    // Open silently (suppress dialogs) but writeable so the user can save changes.
    private const int SwDocPart = (int)swDocumentTypes_e.swDocPART;
    private const int OpenOpts  = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;

    private readonly StaSolidWorksWorker _worker;
    private readonly ILogger<SolidWorksFileOpener> _logger;

    public SolidWorksFileOpener(
        StaSolidWorksWorker worker,
        ILogger<SolidWorksFileOpener> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    public Task OpenFileAsync(string filePath, string? configName, CancellationToken ct)
        => _worker.RunAsync(() => OpenOnSta(filePath, configName));

    // ──────────────────────────────────────────────────────────────────────────
    // All COM access below runs on the STA thread only.
    // ──────────────────────────────────────────────────────────────────────────

    private bool OpenOnSta(string filePath, string? configName)
    {
        var sw = _worker.GetOrCreateSwApp()
            ?? throw new InvalidOperationException(
                "Cannot connect to SOLIDWORKS. Ensure it is installed and licensed.");

        // Reuse an already-open document to avoid duplicates.
        var existingObj = sw.GetOpenDocumentByName(filePath);
        IModelDoc2? doc;

        if (existingObj is IModelDoc2 existing)
        {
            _logger.LogInformation("Reusing open document: {Path}", filePath);
            doc = existing;
        }
        else
        {
            int errors = 0, warnings = 0;
            var openedObj = sw.OpenDoc6(
                filePath, SwDocPart, OpenOpts,
                configName ?? "", ref errors, ref warnings);

            if (openedObj is not IModelDoc2 opened)
            {
                _logger.LogError(
                    "OpenDoc6 returned null for {Path} (errors={E}, warnings={W})",
                    filePath, errors, warnings);
                throw new InvalidOperationException(
                    $"SOLIDWORKS could not open '{System.IO.Path.GetFileName(filePath)}' " +
                    $"(error code {errors}).");
            }

            _logger.LogInformation(
                "Opened {Path} config={Config} errors={E} warnings={W}",
                filePath, configName, errors, warnings);
            doc = opened;
        }

        // Activate the requested configuration.
        // VALIDATE: ShowConfiguration2 against installed SW 2024 interop before production use.
        if (!string.IsNullOrEmpty(configName))
        {
            try { doc.ShowConfiguration2(configName); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ShowConfiguration2 failed for config '{Config}' in {Path}",
                    configName, filePath);
            }
        }

        // Make SOLIDWORKS visible to the user.
        sw.Visible = true;

        return true;
    }
}
