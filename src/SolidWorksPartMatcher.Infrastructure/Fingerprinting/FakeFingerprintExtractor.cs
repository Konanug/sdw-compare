using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Fingerprinting;

/// <summary>
/// Stub extractor that returns deterministic fake geometry from the file hash.
/// Used for testing and UI development before the real SolidWorks worker is wired up.
/// </summary>
public sealed class FakeFingerprintExtractor : IPartFingerprintExtractor
{
    public int ExtractorVersion => 0;

    public Task<PartFingerprint?> ExtractAsync(
        ScannedFile file,
        string configName,
        CancellationToken cancellationToken)
    {
        if (file.Sha256 is null)
            return Task.FromResult<PartFingerprint?>(null);

        var seed = GetSeed(file.Sha256, configName);
        var rng = new Random(seed);

        double RandDim() => rng.NextDouble() * 0.5 + 0.005;
        double[] bb = [RandDim(), RandDim(), RandDim()];
        Array.Sort(bb);

        var fp = new PartFingerprint(
            Id: DeterministicGuid(file.Sha256 + configName),
            ScannedFileId: file.Id,
            FileSha256: file.Sha256,
            ConfigName: configName,
            ExtractorVersion: ExtractorVersion,
            SolidBodyCount: rng.Next(1, 4),
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: bb,
            VolumeM3: rng.NextDouble() * 0.001,
            SurfaceAreaM2: rng.NextDouble() * 0.1,
            MassKg: rng.NextDouble() * 2.0,
            CenterOfMassM: [rng.NextDouble() * 0.1, rng.NextDouble() * 0.1, rng.NextDouble() * 0.1],
            FaceCount: rng.Next(6, 50),
            EdgeCount: rng.Next(12, 80),
            VertexCount: rng.Next(8, 40),
            FeatureCount: rng.Next(1, 20),
            FeatureTypeHistogram: new Dictionary<string, int>
            {
                ["Extrude"] = rng.Next(0, 5),
                ["Cut"] = rng.Next(0, 3),
                ["Fillet"] = rng.Next(0, 4)
            },
            Material: "Steel",
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: "2024",
            ExtractorVersionLabel: "fake-0",
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

        return Task.FromResult<PartFingerprint?>(fp);
    }

    private static int GetSeed(string sha256, string config)
    {
        var combined = sha256 + "|" + config;
        var hash = 0;
        foreach (var c in combined)
            hash = hash * 31 + c;
        return hash;
    }

    private static Guid DeterministicGuid(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(bytes);
    }
}
