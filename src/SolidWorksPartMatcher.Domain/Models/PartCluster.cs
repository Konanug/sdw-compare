namespace SolidWorksPartMatcher.Domain.Models;

public enum ReviewStatus
{
    Pending,
    Approved,
    Rejected,
    NeedsReview
}

public sealed record PartCluster(
    Guid Id,
    Guid ScanRunId,
    string CanonicalName,
    PartClassification Classification,
    Guid RepresentativeFingerprintId,
    ReviewStatus ReviewStatus,
    string? ReviewerNote,
    DateTime? ReviewedUtc,
    string? ReviewerName);

public sealed record ClusterMember(
    Guid ClusterId,
    Guid FingerprintId,
    bool IsRepresentative);
