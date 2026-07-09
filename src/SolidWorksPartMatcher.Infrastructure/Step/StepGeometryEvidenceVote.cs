using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// STEP-only geometric-evidence vote (scan orchestrator Stage 3.6). Counts orientation-invariant
/// signals that agree between two STEP parts; when at least
/// <see cref="StepMatchTolerances.MinimumAgreeingFlags"/> agree, the pair is escalated to
/// <see cref="PartClassification.PossibleMatch"/> so a human reviews it — never a confirmed match,
/// never an auto-merge. Deliberately excludes bounding box (orientation-dependent). Pure C# — no
/// SolidWorks, no OCCT subprocess — so it is fully unit-testable.
/// </summary>
internal static class StepGeometryEvidenceVote
{
    public sealed record Result(bool Escalate, int AgreeingFlags, string Reason);

    public static Result Evaluate(PartFingerprint a, PartFingerprint b, StepMatchTolerances tol)
    {
        var agreeing = new List<string>();

        // Flag 1 — real (OCCT) volume within tolerance. Skipped if either volume is unusable.
        double maxVol = Math.Max(a.VolumeM3, b.VolumeM3);
        if (maxVol > 0)
        {
            double frac = Math.Abs(a.VolumeM3 - b.VolumeM3) / maxVol;
            if (frac <= tol.VolumeDeltaFraction)
                agreeing.Add($"volume Δ {frac * 100:0.##}%");
        }

        // Flag 2 — equal face count.
        if (a.FaceCount == b.FaceCount)
            agreeing.Add($"face count {a.FaceCount}");

        // Flag 3 — identical face-type histogram (per-surface-type counts). Both must be non-empty.
        if (HistogramsEqual(a.FeatureTypeHistogram, b.FeatureTypeHistogram))
            agreeing.Add("face-type match");

        // Flag 4 — tolerant face signature: fraction of faces that pair up with the surface type and
        // axis/angle exactly equal and the radii within relative tolerance.
        double sigFraction = SignatureMatchFraction(a.FaceGeometricSignature, b.FaceGeometricSignature, tol);
        if (sigFraction >= tol.SignatureMatchFraction)
            agreeing.Add($"tolerant signature {sigFraction * 100:0}%");

        int count = agreeing.Count;
        bool escalate = count >= tol.MinimumAgreeingFlags;
        string reason = escalate
            ? $"Under review — {count}/4 geometry signals agree: {string.Join(", ", agreeing)}"
            : $"{count}/4 geometry signals agree (below the {tol.MinimumAgreeingFlags}-signal review threshold)";

        return new Result(escalate, count, reason);
    }

    private static bool HistogramsEqual(
        IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        if (a.Count == 0 || b.Count == 0) return false; // no evidence either way
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var bv) || bv != v) return false;
        return true;
    }

    // Greedy multiset pairing: each A-face is matched to at most one B-face whose shape key
    // (surface type + non-radius params) is identical and whose radii are all within relative
    // tolerance. Returns matched / max(faceCountA, faceCountB) — 0 when either signature is missing.
    private static double SignatureMatchFraction(
        IReadOnlyList<string>? sigA, IReadOnlyList<string>? sigB, StepMatchTolerances tol)
    {
        if (sigA is null || sigB is null || sigA.Count == 0 || sigB.Count == 0) return 0.0;

        // Bucket B's faces by shape key so we only compare radii against same-type/same-axis faces.
        var bByKey = new Dictionary<string, List<(double[] Radii, bool Used)>>(StringComparer.Ordinal);
        foreach (var d in sigB)
        {
            var (key, radii) = StepGeometryEstimator.ParseDescriptor(d);
            if (!bByKey.TryGetValue(key, out var list))
                bByKey[key] = list = [];
            list.Add((radii, false));
        }

        int matched = 0;
        foreach (var d in sigA)
        {
            var (key, radii) = StepGeometryEstimator.ParseDescriptor(d);
            if (!bByKey.TryGetValue(key, out var candidates)) continue;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Used) continue;
                if (RadiiWithinTolerance(radii, candidates[i].Radii, tol.RadiusRelativeTolerance))
                {
                    candidates[i] = (candidates[i].Radii, true);
                    matched++;
                    break;
                }
            }
        }

        int denom = Math.Max(sigA.Count, sigB.Count);
        return denom == 0 ? 0.0 : (double)matched / denom;
    }

    private static bool RadiiWithinTolerance(double[] a, double[] b, double relTol)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (double.IsNaN(a[i]) || double.IsNaN(b[i])) return false;
            double max = Math.Max(Math.Abs(a[i]), Math.Abs(b[i]));
            if (max == 0) continue; // both zero → equal
            if (Math.Abs(a[i] - b[i]) / max > relTol) return false;
        }
        return true;
    }
}
