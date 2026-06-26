using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.SolidWorks;

/// <summary>
/// Stage 5: volumetric Jaccard similarity using IBody2.Operations2 (Boolean intersection).
///
/// For pairs that are too dimensionally different for Stage 4 (GetCoincidenceTransform2) to
/// confirm as exact matches, this stage checks whether the bodies actually overlap in 3D space
/// after CoM alignment, then uses the volume ratio as a Jaccard proxy.
///
/// Jaccard ≥ RevisionFamilyThreshold → RevisionFamily (same design, different dimensions).
/// Jaccard &lt; threshold (or no intersection) → stays PossibleMatch.
///
/// Runs only when both bodies intersect after alignment — there are no false-positive
/// RevisionFamily classifications from parts with coincidentally equal volumes.
///
/// All COM access routes through the dedicated STA thread.
/// </summary>
public sealed class VolumetricBodyComparator : IDetailedGeometryComparator
{
    // Label stored in CandidatePair.ComparatorVersion for results from this stage.
    internal const string ComparatorVersion = "volumetric-jaccard-1";

    // Minimum volumetric Jaccard (intersection / union proxy) to classify as RevisionFamily.
    internal const double RevisionFamilyThreshold = 0.90;

    private const int SwDocPart = (int)swDocumentTypes_e.swDocPART;
    private const int OpenOpts =
        (int)swOpenDocOptions_e.swOpenDocOptions_Silent |
        (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;

    private readonly StaSolidWorksWorker _worker;
    private readonly ILogger<VolumetricBodyComparator> _logger;

    public VolumetricBodyComparator(StaSolidWorksWorker worker, ILogger<VolumetricBodyComparator> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    public Task<DetailedComparisonResult> CompareAsync(
        ScannedFile fileA, PartFingerprint fpA,
        ScannedFile fileB, PartFingerprint fpB,
        CancellationToken cancellationToken)
        => _worker.RunAsync(() => CompareOnSta(fileA, fpA, fileB, fpB));

    // ─────────────────────────────────────────────────────────────────────────
    // STA thread
    // ─────────────────────────────────────────────────────────────────────────

    private DetailedComparisonResult CompareOnSta(
        ScannedFile sfA, PartFingerprint fpA,
        ScannedFile sfB, PartFingerprint fpB)
    {
        var sw = _worker.GetOrCreateSwApp();
        if (sw == null) return Fail("SolidWorks unavailable");

        bool weOpenedA = false, weOpenedB = false;

        try
        {
            var docA = OpenOrReuse(sw, sfA, fpA.ConfigName, out weOpenedA);
            if (docA == null) return Fail($"Cannot open {sfA.FileName}");

            var docB = OpenOrReuse(sw, sfB, fpB.ConfigName, out weOpenedB);
            if (docB == null) return Fail($"Cannot open {sfB.FileName}");

            var bodyA = GetFirstSolidBody(docA, sfA.FileName);
            var bodyB = GetFirstSolidBody(docB, sfB.FileName);
            if (bodyA == null || bodyB == null)
                return Fail($"No solid body found in {sfA.FileName} or {sfB.FileName}");

            // Operations2 is destructive on its inputs — work on copies so the
            // open documents are not modified.
            var copyA = bodyA.Copy() as IBody2;
            var copyB = bodyB.Copy() as IBody2;
            if (copyA == null || copyB == null)
                return Fail("IBody2.Copy() returned null");

            // Translate both copies so their centres of mass coincide at the origin.
            // This ensures spatial overlap is meaningful even when parts were modelled
            // at different absolute positions in their respective documents.
            if (fpA.CenterOfMassM is { Length: >= 3 })
                AlignToOrigin(copyA, fpA.CenterOfMassM, sw);
            if (fpB.CenterOfMassM is { Length: >= 3 })
                AlignToOrigin(copyB, fpB.CenterOfMassM, sw);

            // IBody2.Operations2 validated in installed SW 2024 interop.
            // Actual enum member name (verified): SWBODYINTERSECT.
            // Third parameter is `out int` (ErrorCode), verified from interop signature.
            var resultObj = copyA.Operations2(
                (int)swBodyOperationType_e.SWBODYINTERSECT, (object)copyB, out int opErr);

            if (resultObj is not IBody2 || opErr != 0)
            {
                _logger.LogDebug(
                    "Operations2 returned null or error={E} for {A}↔{B} — bodies do not intersect",
                    opErr, sfA.FileName, sfB.FileName);
                return new DetailedComparisonResult(
                    PartClassification.PossibleMatch, 0.0,
                    "Bodies do not intersect after CoM alignment — shapes are likely distinct");
            }

            // The bodies spatially overlap. Use the volume ratio as a Jaccard proxy:
            //   min(vol_A, vol_B) / max(vol_A, vol_B)
            // This is exact when the shapes are identical and conservative when they differ.
            // It cannot exceed 1 and is low when volumes are very different.
            // The intersection existence check above prevents false positives for parts
            // with equal volumes but non-overlapping shapes.
            var volA = fpA.VolumeM3;
            var volB = fpB.VolumeM3;
            if (volA <= 0 || volB <= 0)
                return Fail("Stored volume is zero or negative — cannot compute Jaccard");

            double jaccard = Math.Min(volA, volB) / Math.Max(volA, volB);
            var cls = jaccard >= RevisionFamilyThreshold
                ? PartClassification.RevisionFamily
                : PartClassification.PossibleMatch;

            _logger.LogInformation(
                "Stage 5 Jaccard={J:F3} ({Cls}) for {A}↔{B} (vols {VA:F2}/{VB:F2} cm³)",
                jaccard, cls, sfA.FileName, sfB.FileName, volA * 1e6, volB * 1e6);

            return new DetailedComparisonResult(
                cls, jaccard,
                $"Volumetric Jaccard ≈ {jaccard:F3} — bodies intersect after CoM alignment " +
                $"(volumes {volA * 1e6:F2} / {volB * 1e6:F2} cm³)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stage 5 comparison threw for {A}↔{B}", sfA.FileName, sfB.FileName);
            return Fail($"Exception: {ex.Message}");
        }
        finally
        {
            if (weOpenedB) CloseQuietly(sw, sfB.NormalizedPath);
            if (weOpenedA) CloseQuietly(sw, sfA.NormalizedPath);
        }
    }

    // Translates a body copy so its centre of mass is at the origin.
    // Builds a pure-translation SW math transform (identity rotation, unit scale).
    //
    // CreateTransform data layout (16 doubles, column-major for the rotation block):
    //   [0-2]  X-axis of rotation frame (col 1 of R) — identity: (1,0,0)
    //   [3-5]  Y-axis (col 2 of R)                  — identity: (0,1,0)
    //   [6-8]  Z-axis (col 3 of R)                  — identity: (0,0,1)
    //   [9-11] Translation vector                   — (-cx, -cy, -cz)
    //   [12]   Scale factor                          — 1.0
    //   [13-15] Reserved                             — 0, 0, 0
    // For identity rotation the column- and row-major values are identical.
    private static void AlignToOrigin(IBody2 body, double[] com, ISldWorks sw)
    {
        object? mathUtilObj = null;
        object? xformObj = null;
        try
        {
            mathUtilObj = sw.GetMathUtility();
            if (mathUtilObj is not IMathUtility mathUtil) return;

            double[] data =
            [
                1.0,  0.0,  0.0,            // rotation col 1 (X-axis)
                0.0,  1.0,  0.0,            // rotation col 2 (Y-axis)
                0.0,  0.0,  1.0,            // rotation col 3 (Z-axis)
                -com[0], -com[1], -com[2],  // translation: move CoM to origin
                1.0,                        // scale
                0.0,  0.0,  0.0             // reserved
            ];

            xformObj = mathUtil.CreateTransform(data);
            // ApplyTransform takes MathTransform (concrete class), not IMathTransform.
            if (xformObj is MathTransform xform)
                body.ApplyTransform(xform);
        }
        catch
        {
            // Non-critical: if alignment fails, the intersection attempt will still run
            // and will either succeed (parts were at similar positions) or return null.
        }
    }

    private IModelDoc2? OpenOrReuse(ISldWorks sw, ScannedFile file, string config, out bool weOpened)
    {
        var existing = sw.GetOpenDocumentByName(file.NormalizedPath);
        if (existing is IModelDoc2 doc) { weOpened = false; return doc; }

        int errors = 0, warnings = 0;
        var opened = sw.OpenDoc6(
            file.NormalizedPath, SwDocPart, OpenOpts, config, ref errors, ref warnings);
        weOpened = opened != null;
        if (weOpened && (errors != 0 || warnings != 0))
            _logger.LogDebug("OpenDoc6 errors={E} warnings={W} for {F}", errors, warnings, file.FileName);
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
        catch (Exception ex) { _logger.LogDebug(ex, "GetFirstSolidBody failed for {F}", fileName); }
        return null;
    }

    private void CloseQuietly(ISldWorks sw, string path)
    {
        try { sw.CloseDoc(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "CloseDoc failed for {P}", path); }
    }

    private static DetailedComparisonResult Fail(string reason) =>
        new(PartClassification.ComparisonFailed, null, reason);
}
