using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Persistence;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// Covers the fingerprint cache read (<see cref="SqlitePartRepository.GetFingerprintAsync"/>) that
/// lets a repeat scan reuse geometry instead of re-opening the file in SolidWorks. The key is
/// (SHA-256, configuration, extractor version); a version bump must miss so a changed extractor is
/// never served stale geometry.
/// </summary>
public sealed class FingerprintCacheTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"fpcache_{Guid.NewGuid():N}.db");

    private static PartFingerprint MakeFp(Guid id, Guid fileId, string sha, int version) => new(
        Id: id,
        ScannedFileId: fileId,
        FileSha256: sha,
        ConfigName: "Default",
        ExtractorVersion: version,
        SolidBodyCount: 1,
        SurfaceBodyCount: 0,
        SortedBoundingBoxM: [0.05, 0.10, 0.20],
        VolumeM3: 0.00123,
        SurfaceAreaM2: 0.05,
        MassKg: null,
        CenterOfMassM: null,
        FaceCount: 20,
        EdgeCount: 30,
        VertexCount: 15,
        FeatureCount: 3,
        FeatureTypeHistogram: new Dictionary<string, int> { ["Extrude"] = 2 },
        Material: "Steel",
        CustomProperties: new Dictionary<string, string>(),
        SolidWorksVersion: "2024",
        ExtractorVersionLabel: $"test-{version}",
        ExtractedUtc: DateTime.UtcNow,
        ChiralitySign: null,
        CoMOffsetInBB: null,
        SketchTextCutCount: 0,
        SuppressedSolidBodyCount: null,
        SuppressedBoundingBoxM: null,
        SuppressedVolumeM3: null,
        SuppressedSurfaceAreaM2: null,
        SuppressedFaceCount: null,
        SuppressedEdgeCount: null,
        SuppressedVertexCount: null);

    [Fact]
    public async Task GetFingerprintAsync_HitsOnMatchingKey_MissesOnVersionBumpOrUnknownSha()
    {
        var ct = CancellationToken.None;
        using var repo = new SqlitePartRepository(_db, NullLogger<SqlitePartRepository>.Instance);

        var runId = Guid.NewGuid();
        await repo.CreateScanRunAsync(
            new ScanRun(runId, DateTime.UtcNow, null, ["C:/parts"], "test", ScanStatus.Running, null), ct);

        var fileId = Guid.NewGuid();
        await repo.UpsertScannedFileAsync(
            new ScannedFile(fileId, "c:/parts/a.sldprt", "a.sldprt", 123,
                DateTime.UtcNow, "SHA-123", "C:/parts", FileStatus.Hashed, null),
            runId, ct);

        var fp = MakeFp(Guid.NewGuid(), fileId, sha: "SHA-123", version: 8);
        await repo.UpsertFingerprintAsync(fp, ct);

        // Hit: exact (sha, config, version).
        var hit = await repo.GetFingerprintAsync("SHA-123", "Default", 8, ct);
        hit.Should().NotBeNull();
        hit!.FileSha256.Should().Be("SHA-123");
        hit.VolumeM3.Should().Be(fp.VolumeM3);
        hit.FaceCount.Should().Be(fp.FaceCount);

        // Miss: extractor version changed → must re-extract, never serve stale geometry.
        (await repo.GetFingerprintAsync("SHA-123", "Default", 9, ct)).Should().BeNull();

        // Miss: different file (different bytes/SHA).
        (await repo.GetFingerprintAsync("OTHER-SHA", "Default", 8, ct)).Should().BeNull();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_db + suffix)) File.Delete(_db + suffix); }
            catch { /* temp file — best effort */ }
        }
    }
}
