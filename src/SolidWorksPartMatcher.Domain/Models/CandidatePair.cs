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

public static class PartClassificationExtensions
{
    /// <summary>
    /// True when the classification asserts the two parts are the same part — either confirmed or
    /// pending human review. These are exactly the edges union-find joins into a cluster, and so the
    /// only ones whose <see cref="CandidatePair.ClassificationReason"/> can explain why a group was
    /// matched.
    ///
    /// Distinct and ComparisonFailed are excluded: every candidate pair is persisted, so a cluster
    /// of three joined by A~B and B~C may still contain a Distinct A–C pair whose reason ("different
    /// face count") must never be presented as evidence of a match.
    ///
    /// Deliberately an allow-list rather than "is not (Distinct or ComparisonFailed)", so a newly
    /// added classification stays excluded until someone decides it belongs here.
    /// </summary>
    public static bool IsMatch(this PartClassification classification) =>
        classification is PartClassification.BinaryDuplicate
                       or PartClassification.ExactGeometryMatch
                       or PartClassification.GeometryMatchMetadataVariant
                       or PartClassification.EngravingVariant
                       or PartClassification.RevisionFamily
                       or PartClassification.MirrorOrHandedVariant
                       or PartClassification.PossibleMatch;
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
