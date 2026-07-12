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

    [Fact]
    public async Task RoundTrip_PreservesEveryFieldReadByOrdinal_IncludingTheStepOnes()
    {
        // ReadFingerprint reads columns by ORDINAL INDEX. A new column inserted anywhere but the end
        // silently shifts every subsequent read — a whole-table corruption that compiles, runs, and
        // returns plausible-looking garbage. This test is the guard: it round-trips the fields most
        // likely to be shifted, and would fail loudly if a column were ever inserted rather than
        // appended. GeometrySource is the newest (ordinal 28, migration v9).
        var ct = CancellationToken.None;
        using var repo = new SqlitePartRepository(_db, NullLogger<SqlitePartRepository>.Instance);

        var runId = Guid.NewGuid();
        await repo.CreateScanRunAsync(
            new ScanRun(runId, DateTime.UtcNow, null, ["C:/parts"], "test", ScanStatus.Running, null), ct);

        var fileId = Guid.NewGuid();
        await repo.UpsertScannedFileAsync(
            new ScannedFile(fileId, "c:/parts/a.step", "a.step", 123,
                DateTime.UtcNow, "SHA-STEP", "C:/parts", FileStatus.Hashed, null),
            runId, ct);

        var fp = MakeFp(Guid.NewGuid(), fileId, sha: "SHA-STEP", version: 102) with
        {
            SortedBoundingBoxM = [0.011, 0.022, 0.033],
            VolumeM3 = 6.1e-06,
            SurfaceAreaM2 = 2.2e-03,
            FaceCount = 84,
            EdgeCount = 252,          // real STEP counts as of extractor v102 — were hardcoded 0
            VertexCount = 168,
            Material = null,
            SourceFormat = "STEP",
            FaceGeometricSignature = ["CYLINDER|0.003|0|0|1", "PLANE|0|0|1"],
            GeometrySource = "occt",
        };
        await repo.UpsertFingerprintAsync(fp, ct);

        var back = await repo.GetFingerprintAsync("SHA-STEP", "Default", 102, ct);

        back.Should().NotBeNull();
        back!.SortedBoundingBoxM.Should().Equal(0.011, 0.022, 0.033);
        back.VolumeM3.Should().Be(6.1e-06);
        back.SurfaceAreaM2.Should().Be(2.2e-03);
        back.FaceCount.Should().Be(84);
        back.EdgeCount.Should().Be(252);
        back.VertexCount.Should().Be(168);
        back.SourceFormat.Should().Be("STEP");
        back.FaceGeometricSignature.Should().Equal("CYLINDER|0.003|0|0|1", "PLANE|0|0|1");
        back.GeometrySource.Should().Be("occt");
    }

    [Fact]
    public async Task RoundTrip_NullGeometrySource_StaysNull_SoAPreV9RowIsNeverTrustedAsKernelMeasured()
    {
        // A row written before migration v9 reads back GeometrySource = null. That must NOT silently
        // become "occt": we do not know how its geometry was obtained, and StepEngravingDetector
        // refuses to compare fine-grained deltas on anything but kernel-measured geometry.
        var ct = CancellationToken.None;
        using var repo = new SqlitePartRepository(_db, NullLogger<SqlitePartRepository>.Instance);

        var runId = Guid.NewGuid();
        await repo.CreateScanRunAsync(
            new ScanRun(runId, DateTime.UtcNow, null, ["C:/parts"], "test", ScanStatus.Running, null), ct);

        var fileId = Guid.NewGuid();
        await repo.UpsertScannedFileAsync(
            new ScannedFile(fileId, "c:/parts/old.sldprt", "old.sldprt", 1,
                DateTime.UtcNow, "SHA-OLD", "C:/parts", FileStatus.Hashed, null),
            runId, ct);

        await repo.UpsertFingerprintAsync(MakeFp(Guid.NewGuid(), fileId, "SHA-OLD", 8), ct);

        var back = await repo.GetFingerprintAsync("SHA-OLD", "Default", 8, ct);

        back!.GeometrySource.Should().BeNull();
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
