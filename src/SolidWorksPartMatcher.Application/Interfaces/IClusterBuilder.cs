using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface IClusterBuilder
{
    IReadOnlyList<PartCluster> BuildClusters(
        Guid scanRunId,
        IReadOnlyList<PartFingerprint> fingerprints,
        IReadOnlyList<CandidatePair> pairs,
        IReadOnlyList<ScannedFile> files,
        ICanonicalNameService nameService);
}
