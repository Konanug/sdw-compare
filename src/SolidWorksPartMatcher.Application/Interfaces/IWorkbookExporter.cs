using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public sealed record ExportContext(
    ScanRun Run,
    IReadOnlyList<ScannedFile> Files,
    IReadOnlyList<PartFingerprint> Fingerprints,
    IReadOnlyList<PartCluster> Clusters,
    IReadOnlyList<ClusterMember> Members,
    IReadOnlyList<CandidatePair> Pairs);

public interface IWorkbookExporter
{
    Task ExportAsync(ExportContext context, string outputPath, CancellationToken ct);
}
