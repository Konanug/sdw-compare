using FluentAssertions;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Clustering;
using Xunit;

// VolumetricBodyComparator constants — kept as literals here because the SolidWorks project
// requires SW interop assemblies that are not present in the unit-test environment.
// If these change in VolumetricBodyComparator, update the literals here to match.

namespace SolidWorksPartMatcher.UnitTests;

public sealed class RevisionFamilyTests
{
    // ── Jaccard threshold ─────────────────────────────────────────────────────

    // RevisionFamilyThreshold = 0.90 (from VolumetricBodyComparator — literal here to avoid SW interop dep)
    private const double RevisionFamilyThreshold = 0.90;

    [Theory]
    [InlineData(1.00, PartClassification.RevisionFamily)]  // identical volumes
    [InlineData(0.95, PartClassification.RevisionFamily)]  // 5% volume difference
    [InlineData(0.90, PartClassification.RevisionFamily)]  // exactly at threshold
    [InlineData(0.89, PartClassification.PossibleMatch)]   // just below threshold
    [InlineData(0.70, PartClassification.PossibleMatch)]   // clearly below
    public void JaccardThreshold_ClassifiesCorrectly(double jaccard, PartClassification expected)
    {
        var cls = jaccard >= RevisionFamilyThreshold
            ? PartClassification.RevisionFamily
            : PartClassification.PossibleMatch;
        cls.Should().Be(expected);
    }

    // ── UnionFind soft-join ───────────────────────────────────────────────────

    [Fact]
    public void RevisionFamilyPair_SoftJoins_IntoSameCluster()
    {
        var runId = Guid.NewGuid();
        var fpA   = MakeFp("sha1");
        var fpB   = MakeFp("sha2");

        var pair = new CandidatePair(
            Id: Guid.NewGuid(),
            ScanRunId: runId,
            FingerprintAId: fpA.Id,
            FingerprintBId: fpB.Id,
            CoarseScore: 0.92,
            MatchedBuckets: ["vol"],
            Classification: PartClassification.RevisionFamily,
            Confidence: 0.92,
            ClassificationReason: "Volumetric Jaccard ≈ 0.94",
            ComparatorVersion: "volumetric-jaccard-1",
            ToleranceProfile: "default");

        var files = new[] { MakeSf(fpA.ScannedFileId), MakeSf(fpB.ScannedFileId) };
        var builder = new UnionFindClusterBuilder();
        var clusters = builder.BuildClusters(runId, [fpA, fpB], [pair], files, new FakeNameService());

        // Both fingerprints must be grouped into one cluster.
        clusters.Should().HaveCount(1);
        clusters[0].Classification.Should().Be(PartClassification.RevisionFamily);
        clusters[0].ReviewStatus.Should().Be(ReviewStatus.NeedsReview);
    }

    [Fact]
    public void ExactMatchInCluster_TakesPriorityOver_RevisionFamily()
    {
        var runId = Guid.NewGuid();
        var fpA   = MakeFp("sha1");
        var fpB   = MakeFp("sha2");

        var pair = new CandidatePair(
            Id: Guid.NewGuid(),
            ScanRunId: runId,
            FingerprintAId: fpA.Id,
            FingerprintBId: fpB.Id,
            CoarseScore: 1.0,
            MatchedBuckets: [],
            Classification: PartClassification.ExactGeometryMatch,
            Confidence: 1.0,
            ClassificationReason: "Confirmed",
            ComparatorVersion: "body-coincidence-1",
            ToleranceProfile: "default");

        var files = new[] { MakeSf(fpA.ScannedFileId), MakeSf(fpB.ScannedFileId) };
        var builder = new UnionFindClusterBuilder();
        var clusters = builder.BuildClusters(runId, [fpA, fpB], [pair], files, new FakeNameService());

        clusters.Should().HaveCount(1);
        clusters[0].Classification.Should().Be(PartClassification.ExactGeometryMatch);
    }

    [Fact]
    public void PossibleMatchPair_DoesNotJoin_IntoSameCluster()
    {
        var runId = Guid.NewGuid();
        var fpA   = MakeFp("sha1");
        var fpB   = MakeFp("sha2");

        var pair = new CandidatePair(
            Id: Guid.NewGuid(),
            ScanRunId: runId,
            FingerprintAId: fpA.Id,
            FingerprintBId: fpB.Id,
            CoarseScore: 0.72,
            MatchedBuckets: ["vol"],
            Classification: PartClassification.PossibleMatch,
            Confidence: 0.72,
            ClassificationReason: "Coarse score 0.72",
            ComparatorVersion: "coarse-1",
            ToleranceProfile: "default");

        var files = new[] { MakeSf(fpA.ScannedFileId), MakeSf(fpB.ScannedFileId) };
        var builder = new UnionFindClusterBuilder();
        var clusters = builder.BuildClusters(runId, [fpA, fpB], [pair], files, new FakeNameService());

        // PossibleMatch must NOT auto-join — each stays its own singleton.
        clusters.Should().HaveCount(2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PartFingerprint MakeFp(string sha) => new(
        Id: Guid.NewGuid(),
        ScannedFileId: Guid.NewGuid(),
        FileSha256: sha,
        ConfigName: "Default",
        ExtractorVersion: 3,
        SolidBodyCount: 1,
        SurfaceBodyCount: 0,
        SortedBoundingBoxM: [0.05, 0.10, 0.20],
        VolumeM3: 0.001,
        SurfaceAreaM2: 0.05,
        MassKg: null,
        CenterOfMassM: null,
        FaceCount: 20,
        EdgeCount: 30,
        VertexCount: 15,
        FeatureCount: 5,
        FeatureTypeHistogram: new Dictionary<string, int> { ["Extrude"] = 2 },
        Material: "Steel",
        CustomProperties: new Dictionary<string, string>(),
        SolidWorksVersion: "2024",
        ExtractorVersionLabel: "test-3",
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

    private static ScannedFile MakeSf(Guid id) => new(
        Id: id,
        NormalizedPath: $@"C:\Parts\Part_{id:N}.SLDPRT",
        FileName: $"Part_{id:N}.SLDPRT",
        SizeBytes: 0,
        LastModifiedUtc: DateTime.UtcNow,
        Sha256: null,
        DiscoveryRoot: @"C:\Parts",
        Status: FileStatus.Hashed,
        Error: null);

    private sealed class FakeNameService : ICanonicalNameService
    {
        public string Suggest(
            IReadOnlyList<PartFingerprint> members,
            IReadOnlyList<ScannedFile> files)
            => files.Count > 0 ? Path.GetFileNameWithoutExtension(files[0].FileName) : "PART-000001";
    }
}
