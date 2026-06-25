using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.SolidWorks;

/// <summary>
/// Stage 4.5 — tessellation-based tolerance comparison.
///
/// When IBody2.GetCoincidenceTransform2 (Stage 4) returns "not coincident",
/// this comparator extracts surface vertex clouds via IBody2.GetTessellation,
/// CoM-aligns them, tries 8 sign-flip orientations of the bounding-box axes,
/// then classifies the pair using HD50 (median NN-distance) and surface-coverage
/// fraction via a KD-tree (Supercluster.KDTree.Standard).
///
/// Classification thresholds (toleranceM = 0.0005 m = 0.5 mm default):
///   coverage ≥ 95% AND HD50 ≤ toleranceM/2  →  ExactGeometryMatch (within tolerance)
///   otherwise                                →  PossibleMatch
///
/// VALIDATION NOTE: IBody2.GetTessellation(null) → ITessellation is confirmed in
/// SW 2024 API Help. Tessellation is graphics-quality, acceptable for ≤0.5 mm checks.
/// All COM calls run on the dedicated STA thread via StaSolidWorksWorker.
/// </summary>
public sealed class TessellationToleranceComparator : ITessellationComparator
{
    private const int SwDocPart = (int)swDocumentTypes_e.swDocPART;
    private const int OpenOpts =
        (int)swOpenDocOptions_e.swOpenDocOptions_Silent |
        (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;

    private const string ComparatorVersion = "tessellation-hd95-1";

    private readonly StaSolidWorksWorker _worker;
    private readonly ILogger<TessellationToleranceComparator> _logger;

    public TessellationToleranceComparator(
        StaSolidWorksWorker worker,
        ILogger<TessellationToleranceComparator> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    public Task<BodyEquivalenceResult> CompareAsync(
        ScannedFile fileA, PartFingerprint fpA,
        ScannedFile fileB, PartFingerprint fpB,
        double toleranceM,
        CancellationToken ct)
        => _worker.RunAsync(() => CompareOnSta(fileA, fpA, fileB, fpB, toleranceM));

    // ─────────────────────────────────────────────────────────────────────────
    // STA thread
    // ─────────────────────────────────────────────────────────────────────────

    private BodyEquivalenceResult CompareOnSta(
        ScannedFile fileA, PartFingerprint fpA,
        ScannedFile fileB, PartFingerprint fpB,
        double toleranceM)
    {
        var sw = _worker.GetOrCreateSwApp();
        if (sw == null) return Fail("SolidWorks unavailable");

        IModelDoc2? docA = null, docB = null;
        bool weOpenedA = false, weOpenedB = false;

        try
        {
            docA = OpenOrReuse(sw, fileA, fpA.ConfigName, out weOpenedA);
            if (docA == null) return Fail($"Cannot open {fileA.FileName}");

            docB = OpenOrReuse(sw, fileB, fpB.ConfigName, out weOpenedB);
            if (docB == null) return Fail($"Cannot open {fileB.FileName}");

            var bodyA = GetFirstSolidBody(docA, fileA.FileName);
            var bodyB = GetFirstSolidBody(docB, fileB.FileName);
            if (bodyA == null || bodyB == null)
                return Fail($"Could not get solid bodies for {fileA.FileName} / {fileB.FileName}");

            var vertsA = ExtractVertices(bodyA, fileA.FileName);
            var vertsB = ExtractVertices(bodyB, fileB.FileName);

            if (vertsA.Count < 4 || vertsB.Count < 4)
                return Fail($"Tessellation too sparse ({vertsA.Count}/{vertsB.Count} vertices)");

            // CoM-translate both clouds so each centroid is at the origin.
            ApplyComTranslation(vertsA, fpA.CenterOfMassM);
            ApplyComTranslation(vertsB, fpB.CenterOfMassM);

            return TryAlignAndClassify(vertsA, fpA, vertsB, fpB, toleranceM,
                fileA.FileName, fileB.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tessellation comparison failed: {A} vs {B}",
                fileA.FileName, fileB.FileName);
            return Fail($"Exception: {ex.Message}");
        }
        finally
        {
            if (weOpenedB) CloseQuietly(sw, fileB.NormalizedPath);
            if (weOpenedA) CloseQuietly(sw, fileA.NormalizedPath);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tessellation extraction
    // ─────────────────────────────────────────────────────────────────────────

    private List<float[]> ExtractVertices(IBody2 body, string fileName)
    {
        var verts = new List<float[]>();
        object? tessObj = null;
        try
        {
            // IBody2.GetTessellation(null) tessellates the entire body.
            tessObj = body.GetTessellation(null);
            if (tessObj is not ITessellation tess)
            {
                _logger.LogDebug("GetTessellation returned unexpected type for {File}", fileName);
                return verts;
            }

            tess.NeedFaceFacetMap = false;
            if (!tess.Tessellate())
            {
                _logger.LogDebug("Tessellate() returned false for {File}", fileName);
                return verts;
            }

            int count = tess.GetVertexCount();
            for (int i = 0; i < count; i++)
            {
                var ptObj = tess.GetVertexPoint(i);
                if (ptObj is double[] pt && pt.Length >= 3)
                    verts.Add([(float)pt[0], (float)pt[1], (float)pt[2]]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tessellation extraction failed for {File}", fileName);
        }
        return verts;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Alignment
    // ─────────────────────────────────────────────────────────────────────────

    private static void ApplyComTranslation(List<float[]> verts, double[]? com)
    {
        if (com == null || com.Length < 3) return;
        float tx = (float)com[0], ty = (float)com[1], tz = (float)com[2];
        for (int i = 0; i < verts.Count; i++)
        {
            verts[i][0] -= tx;
            verts[i][1] -= ty;
            verts[i][2] -= tz;
        }
    }

    // Try identity rotation plus 7 axis sign-flip combinations (8 total) to
    // find the orientation of B that best aligns with A. For parts designed in
    // the same SW coordinate frame, identity (mask=0) should win. The sign-flip
    // search handles cases where one part was mirrored or placed in a different
    // quadrant before saving.
    private BodyEquivalenceResult TryAlignAndClassify(
        List<float[]> vertsA, PartFingerprint fpA,
        List<float[]> vertsB, PartFingerprint fpB,
        double toleranceM, string nameA, string nameB)
    {
        float[][] arrA = [.. vertsA];
        var kdA = BuildKdTree(arrA);

        float[][] bestVertsB = [.. vertsB];
        double bestHd50 = OneWayHd50(vertsB, kdA);

        for (int mask = 1; mask < 8; mask++)
        {
            bool flipX = (mask & 1) != 0;
            bool flipY = (mask & 2) != 0;
            bool flipZ = (mask & 4) != 0;

            float[][] candidate = vertsB.Select(v => new float[]
            {
                flipX ? -v[0] : v[0],
                flipY ? -v[1] : v[1],
                flipZ ? -v[2] : v[2]
            }).ToArray();

            double hd50 = OneWayHd50(candidate, kdA);
            if (hd50 < bestHd50)
            {
                bestHd50 = hd50;
                bestVertsB = candidate;
            }
        }

        return ClassifyFromMetrics(arrA, bestVertsB, fpA, fpB, toleranceM, nameA, nameB);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Metric computation and classification
    // ─────────────────────────────────────────────────────────────────────────

    private BodyEquivalenceResult ClassifyFromMetrics(
        float[][] vertsA, float[][] vertsB,
        PartFingerprint fpA, PartFingerprint fpB,
        double toleranceM, string nameA, string nameB)
    {
        var kdA = BuildKdTree(vertsA);
        var kdB = BuildKdTree(vertsB);

        var distsAtoB = ComputeOneWayDistances(vertsA, kdB);
        var distsBtoA = ComputeOneWayDistances(vertsB, kdA);

        // Sort combined distances for percentile computation.
        var allDists = new List<float>(distsAtoB.Count + distsBtoA.Count);
        allDists.AddRange(distsAtoB);
        allDists.AddRange(distsBtoA);
        allDists.Sort();

        double hd50 = Percentile(allDists, 50);
        double hd95 = Percentile(allDists, 95);
        float tolF = (float)toleranceM;
        double coverage = distsAtoB.Count > 0
            ? distsAtoB.Count(d => d <= tolF) / (double)distsAtoB.Count
            : 0.0;

        _logger.LogDebug(
            "Tessellation {A}↔{B}: HD50={H50:F3}mm HD95={H95:F3}mm coverage={C:P1} verts={VA}/{VB}",
            nameA, nameB, hd50 * 1000, hd95 * 1000, coverage, vertsA.Length, vertsB.Length);

        double halfTol = toleranceM * 0.5;

        if (coverage >= 0.95 && hd50 <= halfTol)
        {
            _logger.LogInformation(
                "ExactGeometryMatch (within {T:F1}mm tolerance): {A} ≡ {B} " +
                "(HD50={H:F3}mm coverage={C:P1})",
                toleranceM * 1000, nameA, nameB, hd50 * 1000, coverage);
            return new BodyEquivalenceResult(true, PartClassification.ExactGeometryMatch, null,
                $"Within tolerance: HD50={hd50 * 1000:F3}mm coverage={coverage:P1} " +
                $"comparator={ComparatorVersion}");
        }

        _logger.LogDebug(
            "PossibleMatch after tessellation {A}↔{B}: HD50={H:F3}mm coverage={C:P1}",
            nameA, nameB, hd50 * 1000, coverage);
        return new BodyEquivalenceResult(false, PartClassification.PossibleMatch, null,
            $"Tessellation inconclusive: HD50={hd50 * 1000:F3}mm coverage={coverage:P1} " +
            $"comparator={ComparatorVersion}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // KD-tree helpers  (Supercluster.KDTree.Standard)
    // KDTree<TNode, TValue> where TNode = float (coordinate element), TValue = int (index).
    // Points are float[][] (array of points, each a float[dimensions]).
    // Metric: Func<float[], float[], double>.
    // NearestNeighbors(float[] query, int k) → Tuple<float[][], int[]>.
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Func<float[], float[], double> EuclideanMetric =
        static (a, b) =>
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        };

    private static Supercluster.KDTree.KDTree<float, int> BuildKdTree(float[][] points)
    {
        var values = Enumerable.Range(0, points.Length).ToArray();
        return new Supercluster.KDTree.KDTree<float, int>(3, points, values, EuclideanMetric);
    }

    private static List<float> ComputeOneWayDistances(
        IEnumerable<float[]> queryPts,
        Supercluster.KDTree.KDTree<float, int> tree)
    {
        var dists = new List<float>();
        foreach (var pt in queryPts)
        {
            // NearestNeighbors returns Tuple<float[], int>[] — one entry per nearest neighbour.
            var results = tree.NearestNeighbors(pt, 1);
            if (results.Length > 0)
            {
                float[] nearest = results[0].Item1;
                double dx = pt[0] - nearest[0], dy = pt[1] - nearest[1], dz = pt[2] - nearest[2];
                dists.Add((float)Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
        }
        return dists;
    }

    private double OneWayHd50(IEnumerable<float[]> queryPts,
        Supercluster.KDTree.KDTree<float, int> tree)
    {
        var dists = ComputeOneWayDistances(queryPts, tree);
        dists.Sort();
        return Percentile(dists, 50);
    }

    private static double Percentile(List<float> sorted, int pct)
    {
        if (sorted.Count == 0) return double.MaxValue;
        int idx = Math.Clamp((int)Math.Ceiling(sorted.Count * pct / 100.0) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SW document helpers
    // ─────────────────────────────────────────────────────────────────────────

    private IModelDoc2? OpenOrReuse(ISldWorks sw, ScannedFile file, string config, out bool weOpened)
    {
        var existing = sw.GetOpenDocumentByName(file.NormalizedPath);
        if (existing is IModelDoc2 doc) { weOpened = false; return doc; }

        int errors = 0, warnings = 0;
        var opened = sw.OpenDoc6(file.NormalizedPath, SwDocPart, OpenOpts, config, ref errors, ref warnings);
        weOpened = opened != null;
        if (weOpened && errors != 0)
            _logger.LogDebug("OpenDoc6 errors={E} warnings={W} for {File}", errors, warnings, file.FileName);
        return opened as IModelDoc2;
    }

    private IBody2? GetFirstSolidBody(IModelDoc2 doc, string fileName)
    {
        try
        {
            var obj = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false);
            if (obj is not object[] arr || arr.Length == 0) return null;
            return arr[0] as IBody2;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "GetFirstSolidBody failed for {File}", fileName); }
        return null;
    }

    private void CloseQuietly(ISldWorks sw, string path)
    {
        try { sw.CloseDoc(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "CloseDoc failed for {Path}", path); }
    }

    private static BodyEquivalenceResult Fail(string reason) =>
        new(false, PartClassification.ComparisonFailed, null, reason);
}
