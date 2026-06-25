using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface ICandidateScorer
{
    double Score(PartFingerprint a, PartFingerprint b, ScoringWeights weights);
}
