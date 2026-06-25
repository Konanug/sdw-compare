using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Discovery;

public sealed class FileDiscoveryService(ILogger<FileDiscoveryService> logger) : IFileDiscoveryService
{
    public async IAsyncEnumerable<ScannedFile> DiscoverAsync(
        IReadOnlyList<string> rootPaths,
        IProgress<string>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var root in rootPaths)
        {
            if (!Directory.Exists(root))
            {
                logger.LogWarning("Root path does not exist: {Root}", root);
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.SLDPRT", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to enumerate {Root}", root);
                continue;
            }

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(path);

                ScannedFile file;
                try
                {
                    var info = new FileInfo(path);
                    file = new ScannedFile(
                        Id: Guid.NewGuid(),
                        NormalizedPath: Path.GetFullPath(path),
                        FileName: info.Name,
                        SizeBytes: info.Length,
                        LastModifiedUtc: info.LastWriteTimeUtc,
                        Sha256: null,
                        DiscoveryRoot: Path.GetFullPath(root),
                        Status: FileStatus.Pending,
                        Error: null);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to stat file: {Path}", path);
                    file = new ScannedFile(
                        Id: Guid.NewGuid(),
                        NormalizedPath: Path.GetFullPath(path),
                        FileName: Path.GetFileName(path),
                        SizeBytes: 0,
                        LastModifiedUtc: DateTime.UtcNow,
                        Sha256: null,
                        DiscoveryRoot: Path.GetFullPath(root),
                        Status: FileStatus.Failed,
                        Error: ex.Message);
                }

                yield return file;
                await Task.Yield();
            }
        }
    }
}
