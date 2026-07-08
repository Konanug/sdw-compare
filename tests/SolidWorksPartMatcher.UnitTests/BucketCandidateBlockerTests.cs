using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class BucketCandidateBlockerTests
{
    private static PartFingerprint Fp(Guid id, double[] bb, double vol) => new(
        Id: id,
        ScannedFileId: id,
        FileSha256: id.ToString("N"),
        ConfigName: "Default",
        ExtractorVersion: 2,
        SolidBodyCount: 1,
        SurfaceBodyCount: 0,
        SortedBoundingBoxM: bb,
        VolumeM3: vol,
        SurfaceAreaM2: 0.05,
        MassKg: null,
        CenterOfMassM: null,
        FaceCount: 20,
        EdgeCount: 30,
        VertexCount: 15,
        FeatureCount: 3,
        FeatureTypeHistogram: new Dictionary<string, int>(),
        Material: null,
        CustomProperties: new Dictionary<string, string>(),
        SolidWorksVersion: "2024",
        ExtractorVersionLabel: "test-2",
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

    // Fixed ids so the expected order is stable and the assertion is meaningful.
    private static IReadOnlyList<PartFingerprint> SampleSet()
    {
        var g1 = new Guid("11111111-1111-1111-1111-111111111111");
        var g2 = new Guid("22222222-2222-2222-2222-222222222222");
        var g3 = new Guid("33333333-3333-3333-3333-333333333333");
        var g4 = new Guid("44444444-4444-4444-4444-444444444444");
        return
        [
            Fp(g1, [0.05, 0.10, 0.20], 0.001),
            Fp(g2, [0.05, 0.10, 0.20], 0.001), // matches g1
            Fp(g3, [0.05, 0.10, 0.20], 0.001), // matches g1, g2
            Fp(g4, [0.50, 0.60, 0.70], 0.05),  // isolated (different bucket)
        ];
    }

    [Fact]
    public void GenerateCandidates_IsDeterministic_AcrossRepeatedRuns()
    {
        var blocker = new BucketCandidateBlocker();
        var set = SampleSet();

        // Signature = ordered list of "a|b|firstBucket" — captures order, pairing, AND bucket label.
        static List<string> Signature(
            IReadOnlyList<(Guid A, Guid B, string[] Buckets)> r) =>
            r.Select(x => $"{x.A}|{x.B}|{string.Join(",", x.Buckets)}").ToList();

        var first = Signature(blocker.GenerateCandidates(set));

        for (int i = 0; i < 5; i++)
            Signature(blocker.GenerateCandidates(set)).Should().Equal(first);
    }

    [Fact]
    public void GenerateCandidates_ResultsAreSortedByPairId()
    {
        var blocker = new BucketCandidateBlocker();
        var result = blocker.GenerateCandidates(SampleSet());

        // The three identical parts form all 3 pairwise candidates; the isolated part forms none.
        result.Should().HaveCount(3);

        for (int i = 1; i < result.Count; i++)
        {
            int c = result[i - 1].Item1.CompareTo(result[i].Item1);
            if (c == 0) c = result[i - 1].Item2.CompareTo(result[i].Item2);
            c.Should().BeLessThanOrEqualTo(0, "candidate pairs must be emitted in ascending id order");
        }
    }
}
