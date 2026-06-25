using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Application.Interfaces;

public interface IPartRepository
{
    // Scan runs
    Task<ScanRun> CreateScanRunAsync(ScanRun run, CancellationToken ct);
    Task UpdateScanRunStatusAsync(Guid runId, ScanStatus status, DateTime? endedUtc, CancellationToken ct);

    // Scanned files
    Task UpsertScannedFileAsync(ScannedFile file, Guid scanRunId, CancellationToken ct);
    Task<ScannedFile?> GetScannedFileByPathAsync(string normalizedPath, CancellationToken ct);
    Task<IReadOnlyList<ScannedFile>> GetAllScannedFilesAsync(Guid scanRunId, CancellationToken ct);

    // Fingerprints
    Task UpsertFingerprintAsync(PartFingerprint fingerprint, CancellationToken ct);
    Task<PartFingerprint?> GetFingerprintAsync(string sha256, string configName, int extractorVersion, CancellationToken ct);
    Task<IReadOnlyList<PartFingerprint>> GetAllFingerprintsAsync(Guid scanRunId, CancellationToken ct);

    // Candidate pairs
    Task UpsertCandidatePairAsync(CandidatePair pair, CancellationToken ct);
    Task<IReadOnlyList<CandidatePair>> GetCandidatePairsAsync(Guid scanRunId, CancellationToken ct);

    // Clusters
    Task UpsertClusterAsync(PartCluster cluster, CancellationToken ct);
    Task UpsertClusterMemberAsync(ClusterMember member, CancellationToken ct);
    // Atomically writes a cluster and all its members in a single transaction.
    Task UpsertClusterWithMembersAsync(PartCluster cluster, IReadOnlyList<ClusterMember> members, CancellationToken ct);
    Task<IReadOnlyList<PartCluster>> GetClustersAsync(Guid scanRunId, CancellationToken ct);
    Task<IReadOnlyList<ClusterMember>> GetClusterMembersAsync(Guid clusterId, CancellationToken ct);
    Task UpdateClusterReviewAsync(Guid clusterId, ReviewStatus status, string? note, string? reviewer, DateTime reviewedUtc, CancellationToken ct);
    Task UpdateClusterCanonicalNameAsync(Guid clusterId, string canonicalName, CancellationToken ct);
}
