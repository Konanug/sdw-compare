using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

/// <summary>
/// Opens a .SLDPRT file in SOLIDWORKS for interactive viewing.
/// Implementations must route all COM calls through the dedicated STA worker.
/// </summary>
public interface ISolidWorksFileOpener
{
    /// <summary>
    /// Opens <paramref name="filePath"/> in SOLIDWORKS, activating
    /// <paramref name="configName"/> when provided.
    /// The call blocks until the document is open or an exception is thrown.
    /// </summary>
    Task OpenFileAsync(string filePath, string? configName, CancellationToken ct);
}
