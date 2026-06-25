using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.SolidWorks;

/// <summary>
/// Stage 4 body equivalence check.
/// Uses IBody2.GetCoincidenceTransform2 (verified against installed SW 2024 interop) to
/// determine if two solid bodies are congruent, then classifies the alignment transform:
///   det(R) ≈ +1  →  proper rotation  →  ExactGeometryMatch
///   det(R) ≈ -1  →  reflection       →  MirrorOrHandedVariant
/// All COM access runs through the dedicated STA thread.
/// </summary>
public sealed class BodyEquivalenceChecker : IBodyEquivalenceChecker
{
    private const int SwDocPart = (int)swDocumentTypes_e.swDocPART;
    private const int OpenOpts =
        (int)swOpenDocOptions_e.swOpenDocOptions_Silent |
        (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;

    private readonly StaSolidWorksWorker _worker;
    private readonly ILogger<BodyEquivalenceChecker> _logger;

    public BodyEquivalenceChecker(
        StaSolidWorksWorker worker,
        ILogger<BodyEquivalenceChecker> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    public Task<BodyEquivalenceResult> CheckAsync(
        ScannedFile fileA, string configA,
        ScannedFile fileB, string configB,
        CancellationToken ct)
        => _worker.RunAsync(() => CheckOnSta(fileA, configA, fileB, configB));

    // ─────────────────────────────────────────────────────────────────────────
    // STA thread
    // ─────────────────────────────────────────────────────────────────────────

    private BodyEquivalenceResult CheckOnSta(
        ScannedFile fileA, string configA,
        ScannedFile fileB, string configB)
    {
        var sw = _worker.GetOrCreateSwApp();
        if (sw == null)
            return Fail("SolidWorks unavailable");

        IModelDoc2? docA = null, docB = null;
        bool weOpenedA = false, weOpenedB = false;

        try
        {
            docA = OpenOrReuse(sw, fileA, configA, out weOpenedA);
            if (docA == null) return Fail($"Cannot open {fileA.FileName}");

            docB = OpenOrReuse(sw, fileB, configB, out weOpenedB);
            if (docB == null) return Fail($"Cannot open {fileB.FileName}");

            var bodyA = GetFirstSolidBody(docA, fileA.FileName);
            var bodyB = GetFirstSolidBody(docB, fileB.FileName);
            if (bodyA == null || bodyB == null)
                return Fail($"Could not obtain solid bodies for {fileA.FileName} / {fileB.FileName}");

            return ClassifyByCoincidence(bodyA, bodyB, fileA.FileName, fileB.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Body equivalence check failed: {A} vs {B}", fileA.FileName, fileB.FileName);
            return Fail($"Exception: {ex.Message}");
        }
        finally
        {
            if (weOpenedB) CloseQuietly(sw, fileB.NormalizedPath);
            if (weOpenedA) CloseQuietly(sw, fileA.NormalizedPath);
        }
    }

    // SW revision major threshold for GetCoincidenceTransform2.
    // The method was added in SW 2018 (major 26). On SW 2016-2017, calling it via the
    private BodyEquivalenceResult ClassifyByCoincidence(
        IBody2 bodyA, IBody2 bodyB, string nameA, string nameB)
    {
        // IBody2.GetCoincidenceTransform2 — VALIDATED: exists in installed SW 2024 interop.
        // Returns true if the two bodies are congruent (same shape) and outputs
        // the transform that maps bodyA onto bodyB, which may include reflection.
        MathTransform? xformCom = null;
        bool coincident;
        try
        {
            coincident = bodyA.GetCoincidenceTransform2((object)bodyB, out xformCom);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCoincidenceTransform2 threw for {A} vs {B}", nameA, nameB);
            return Fail("GetCoincidenceTransform2 unavailable");
        }

        if (!coincident || xformCom is not IMathTransform xform)
        {
            _logger.LogDebug("Bodies not coincident: {A} vs {B}", nameA, nameB);
            return new BodyEquivalenceResult(false, PartClassification.Distinct, null,
                "Bodies are not coincident — geometrically distinct");
        }

        var det = ExtractRotationDeterminant(xform, nameA, nameB);
        if (det == null)
        {
            _logger.LogWarning("Coincident but could not read transform for {A} vs {B}", nameA, nameB);
            return new BodyEquivalenceResult(true, PartClassification.PossibleMatch, null,
                "Bodies coincident but transform unreadable — review needed");
        }

        // det ≈ +1: rotation-only (proper rigid transform) → same part
        // det ≈ -1: includes a reflection → mirror/handed variant
        const double tol = 1e-5;
        if (Math.Abs(det.Value - 1.0) <= tol)
        {
            _logger.LogInformation("ExactGeometryMatch confirmed by proper rigid transform (det={D:F6}): {A} ≡ {B}",
                det.Value, nameA, nameB);
            return new BodyEquivalenceResult(true, PartClassification.ExactGeometryMatch, det,
                $"Proper rigid transform confirmed (det(R)={det.Value:F6})");
        }

        if (Math.Abs(det.Value + 1.0) <= tol)
        {
            _logger.LogInformation("MirrorOrHandedVariant confirmed by reflection transform (det={D:F6}): {A} ↔ {B}",
                det.Value, nameA, nameB);
            return new BodyEquivalenceResult(true, PartClassification.MirrorOrHandedVariant, det,
                $"Reflection transform confirmed (det(R)={det.Value:F6}) — opposite-handed parts");
        }

        _logger.LogWarning("Unexpected transform determinant {D:F4} for {A} vs {B}", det.Value, nameA, nameB);
        return new BodyEquivalenceResult(true, PartClassification.PossibleMatch, det,
            $"Bodies coincident but unexpected det(R)={det.Value:F4} — review needed");
    }

    // Extracts det([XAxis | YAxis | ZAxis]) from the rotation part of the SW transform.
    // Uses IMathTransform.GetData (Object& params) which is easier to call than IGetData.
    // VALIDATED: GetData exists on IMathTransform in installed SW 2024 interop.
    private double? ExtractRotationDeterminant(IMathTransform xform, string nameA, string nameB)
    {
        object xa = null!, ya = null!, za = null!, ta = null!;
        try
        {
            double scale = 0;
            xform.GetData(ref xa, ref ya, ref za, ref ta, ref scale);

            static double[]? ToVec(object obj)
            {
                if (obj is IMathVector mv)
                {
                    var data = mv.ArrayData;
                    if (data is double[] da) return da;
                    if (data is object[] oa && oa.Length >= 3)
                        return Array.ConvertAll(oa, o => Convert.ToDouble(o));
                }
                return null;
            }

            var x = ToVec(xa); // column 1 of rotation matrix (X axis direction)
            var y = ToVec(ya); // column 2
            var z = ToVec(za); // column 3
            if (x == null || y == null || z == null) return null;

            // Scalar triple product: det([x|y|z]) = x · (y × z)
            double det = x[0] * (y[1] * z[2] - y[2] * z[1])
                       - x[1] * (y[0] * z[2] - y[2] * z[0])
                       + x[2] * (y[0] * z[1] - y[1] * z[0]);
            return det;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read transform determinant for {A} vs {B}", nameA, nameB);
            return null;
        }
    }

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
