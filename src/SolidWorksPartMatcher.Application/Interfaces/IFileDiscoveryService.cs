using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface IFileDiscoveryService
{
    IAsyncEnumerable<ScannedFile> DiscoverAsync(
        IReadOnlyList<string> rootPaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
