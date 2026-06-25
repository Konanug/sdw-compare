namespace SolidWorksPartMatcher.Domain.Models;

public enum PartClassification
{
    BinaryDuplicate,
    ExactGeometryMatch,
    GeometryMatchMetadataVariant,
    MirrorOrHandedVariant,
    RevisionFamily,
    PossibleMatch,
    // Engraving or logo marking is the only apparent difference; not a confirmed exact match.
    EngravingVariant,
    Distinct,
    ComparisonFailed
}

public sealed record CandidatePair(
    Guid Id,
    Guid ScanRunId,
    Guid FingerprintAId,
    Guid FingerprintBId,
    double CoarseScore,
    string[] MatchedBuckets,
    PartClassification Classification,
    double? Confidence,
    string? ClassificationReason,
    string? ComparatorVersion,
    string? ToleranceProfile);
