using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Blocking;

public sealed class BucketCandidateBlocker : ICandidateBlocker
{
    private const double BbQuantumM = 0.005;     // 5 mm buckets
    private const double VolQuantumM3 = 1e-7;    // ~0.1 cm³
    private const double SaQuantumM2 = 1e-5;

    public IReadOnlyList<(Guid FingerprintAId, Guid FingerprintBId, string[] MatchedBuckets)> GenerateCandidates(
        IReadOnlyList<PartFingerprint> fingerprints)
    {
        var groups = new Dictionary<string, List<Guid>>();

        foreach (var fp in fingerprints)
        {
            foreach (var key in BucketKeys(fp))
            {
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = [];
                list.Add(fp.Id);
            }
        }

        // Map fingerprint id → index for deduplication
        var seen = new HashSet<(Guid, Guid)>();
        var fpMap = fingerprints.ToDictionary(f => f.Id);
        var results = new List<(Guid, Guid, string[])>();

        foreach (var (bucket, ids) in groups)
        {
            if (ids.Count < 2) continue;
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                {
                    var a = ids[i];
                    var b = ids[j];
                    var key = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
                    if (!seen.Add(key)) continue;

                    var fpA = fpMap[a];
                    var fpB = fpMap[b];
                    if (EffectiveBodyCount(fpA) != EffectiveBodyCount(fpB)) continue;

                    results.Add((key.Item1, key.Item2, [bucket]));
                }
            }
        }

        return results;
    }

    private static double[] EffectiveBB(PartFingerprint fp)
        => fp.SuppressedBoundingBoxM ?? fp.SortedBoundingBoxM;
    private static double EffectiveVolume(PartFingerprint fp)
        => fp.SuppressedVolumeM3 ?? fp.VolumeM3;
    private static double EffectiveSA(PartFingerprint fp)
        => fp.SuppressedSurfaceAreaM2 ?? fp.SurfaceAreaM2;
    private static int EffectiveBodyCount(PartFingerprint fp)
        => fp.SuppressedSolidBodyCount ?? fp.SolidBodyCount;

    private static IEnumerable<string> BucketKeys(PartFingerprint fp)
    {
        var bb = EffectiveBB(fp);
        if (bb.Length < 3) yield break;

        var bc = EffectiveBodyCount(fp);

        // Quantize into bucket
        var bx = Quantize(bb[0], BbQuantumM);
        var by = Quantize(bb[1], BbQuantumM);
        var bz = Quantize(bb[2], BbQuantumM);
        var vol = Quantize(EffectiveVolume(fp), VolQuantumM3);
        var sa = Quantize(EffectiveSA(fp), SaQuantumM2);

        // Primary bucket
        yield return $"bc={bc}|bb={bx},{by},{bz}|vol={vol}";

        // Neighboring buckets — offset each dimension by ±1 to prevent boundary misses
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            yield return $"bc={bc}|bb={bx+dx},{by+dy},{bz+dz}|vol={vol}";
        }

        // Volume-only bucket as fallback
        yield return $"bc={bc}|vol={vol}|sa={sa}";
    }

    private static long Quantize(double value, double quantum) => (long)Math.Round(value / quantum);
}
