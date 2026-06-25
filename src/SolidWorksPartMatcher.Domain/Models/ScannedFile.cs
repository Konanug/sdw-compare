namespace SolidWorksPartMatcher.Domain.Models;

public enum FileStatus
{
    Pending,
    Hashed,
    Fingerprinted,
    Failed
}

public sealed record ScannedFile(
    Guid Id,
    string NormalizedPath,
    string FileName,
    long SizeBytes,
    DateTime LastModifiedUtc,
    string? Sha256,
    string DiscoveryRoot,
    FileStatus Status,
    string? Error);
