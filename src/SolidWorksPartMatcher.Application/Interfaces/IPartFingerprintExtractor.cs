using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface IPartFingerprintExtractor
{
    /// <summary>
    /// Extract geometry fingerprint for one file/configuration pair.
    /// Returns null when the file cannot be opened or processed.
    /// </summary>
    Task<PartFingerprint?> ExtractAsync(
        ScannedFile file,
        string configName,
        CancellationToken cancellationToken);

    int ExtractorVersion { get; }
}
