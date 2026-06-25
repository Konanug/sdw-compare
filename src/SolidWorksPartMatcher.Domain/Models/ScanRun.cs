namespace SolidWorksPartMatcher.Domain.Models;

public enum ScanStatus
{
    Running,
    Completed,
    Cancelled,
    Failed
}

public sealed record ScanRun(
    Guid Id,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    IReadOnlyList<string> SourceRoots,
    string AppVersion,
    ScanStatus Status,
    string? ScanSettingsJson);
