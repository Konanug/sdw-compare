using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public sealed record ScanProgress(string Stage, string Detail, int Current, int Total);

public interface IScanOrchestrationService
{
    Task<ScanRun> RunScanAsync(
        IReadOnlyList<string> rootPaths,
        ScoringWeights? weights,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
