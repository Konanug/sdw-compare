using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public sealed record AssemblyDiffProgress(string Stage, string Detail);

public interface IAssemblyDiffOrchestrationService
{
    Task<AssemblyDiffSummary> CompareAsync(
        string assemblyPathA,
        string assemblyPathB,
        AssemblyDiffTolerances? tolerances,
        IProgress<AssemblyDiffProgress>? progress,
        CancellationToken cancellationToken);
}
