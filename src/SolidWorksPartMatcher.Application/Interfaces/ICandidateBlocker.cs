using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface ICandidateBlocker
{
    IReadOnlyList<(Guid FingerprintAId, Guid FingerprintBId, string[] MatchedBuckets)> GenerateCandidates(
        IReadOnlyList<PartFingerprint> fingerprints);
}
