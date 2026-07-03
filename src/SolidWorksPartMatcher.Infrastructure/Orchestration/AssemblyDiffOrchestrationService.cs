using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Assembly;
using SolidWorksPartMatcher.Infrastructure.Step;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;

namespace SolidWorksPartMatcher.Infrastructure.Orchestration;

public sealed class AssemblyDiffOrchestrationService(
    AssemblyComponentMatcher matcher,
    ILogger<AssemblyDiffOrchestrationService> logger)
    : IAssemblyDiffOrchestrationService
{
    public async Task<AssemblyDiffSummary> CompareAsync(
        string assemblyPathA,
        string assemblyPathB,
        AssemblyDiffTolerances? tolerances,
        IProgress<AssemblyDiffProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(assemblyPathA))
            throw new FileNotFoundException("Assembly A not found.", assemblyPathA);
        if (!File.Exists(assemblyPathB))
            throw new FileNotFoundException("Assembly B not found.", assemblyPathB);

        progress?.Report(new AssemblyDiffProgress("Parsing", "Reading STEP entity structure..."));

        var taskA = Task.Run(() => ParseStructure(assemblyPathA), cancellationToken);
        var taskB = Task.Run(() => ParseStructure(assemblyPathB), cancellationToken);
        await Task.WhenAll(taskA, taskB);
        var (structA, structB) = (taskA.Result, taskB.Result);

        if (structA.Components.Count == 0)
            throw new InvalidOperationException(
                $"No parts with B-Rep geometry were found in '{Path.GetFileName(assemblyPathA)}' — " +
                "it may not be a multi-part STEP assembly.");
        if (structB.Components.Count == 0)
            throw new InvalidOperationException(
                $"No parts with B-Rep geometry were found in '{Path.GetFileName(assemblyPathB)}' — " +
                "it may not be a multi-part STEP assembly.");

        progress?.Report(new AssemblyDiffProgress("Matching", "Comparing components..."));

        var summary = matcher.Diff(
            structA, structB, tolerances ?? AssemblyDiffTolerances.Default,
            assemblyPathA, assemblyPathB);

        logger.LogInformation(
            "Assembly diff {A} vs {B}: {Count} component diffs, {Warnings} warnings",
            Path.GetFileName(assemblyPathA), Path.GetFileName(assemblyPathB),
            summary.Components.Count, summary.Warnings.Count);

        return summary;
    }

    private AssemblyStructure ParseStructure(string path)
    {
        var reader = StepP21Reader.ParseFile(path);
        return new StepAssemblyStructureReader(reader).Read();
    }
}
