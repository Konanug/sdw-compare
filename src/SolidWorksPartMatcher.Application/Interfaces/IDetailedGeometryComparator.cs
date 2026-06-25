using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public sealed record DetailedComparisonResult(
    PartClassification Classification,
    double? JaccardSimilarity,
    string Reason);

/// <summary>
/// Stage 5: detailed geometric similarity for pairs that survived Stage 3 scoring
/// but whose bodies are not exactly coincident per Stage 4.
/// Implementations compare body shapes directly (e.g. via volumetric Jaccard).
/// RevisionFamily is the expected outcome for "same design, slightly tweaked dimensions".
/// All implementations that call the SW COM API must route through the STA thread.
/// </summary>
public interface IDetailedGeometryComparator
{
    Task<DetailedComparisonResult> CompareAsync(
        ScannedFile fileA, PartFingerprint fpA,
        ScannedFile fileB, PartFingerprint fpB,
        CancellationToken cancellationToken);
}
