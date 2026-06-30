using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Domain.Models;
using System.Runtime.InteropServices;

namespace SolidWorksPartMatcher.SolidWorks;

/// <summary>
/// Real fingerprint extractor: opens each .SLDPRT silently, reads geometry and
/// mass properties via the SolidWorks COM API, then closes the file.
/// All COM calls are routed through <see cref="StaSolidWorksWorker"/>.
///
/// COM lifecycle rules applied here:
///   - Every COM object obtained from a factory method (GetBodies2, GetFeatures,
///     CreateMassProperty2, get_CustomPropertyManager) is explicitly released via
///     Marshal.ReleaseComObject after use.
///   - Interface casts of existing objects (IPartDoc = (IPartDoc)doc) share the
///     same RCW and are NOT separately released.
///   - IModelDoc2 doc is released after sw.CloseDoc() in the finally block.
///   - A non-blocking GC hint is issued after each file to drain any surviving RCWs.
/// </summary>
public sealed class SolidWorksPartFingerprintExtractor : IPartFingerprintExtractor
{
    public int ExtractorVersion => 8;

    private const string ExtractorLabel = "sw2024-real-8";
    private const int SwDocPart = (int)swDocumentTypes_e.swDocPART;

    // ReadOnly is intentionally omitted so SetSuppression2 calls succeed.
    // The file is never saved (Save3 is never called), so nothing is written to disk.
    private const int OpenOpts = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;

    // swFeatureSuppressionState_e: 0 = fully unsuppressed, 2 = suppressed
    private const int SwUnsuppressed = 0;
    private const int SwSuppressed   = 2;

    // swInConfigurationOpts_e: 1 = this configuration only
    private const int SwThisConfig = 1;

    // SW revision major thresholds for API availability.
    private readonly StaSolidWorksWorker _worker;
    private readonly ILogger<SolidWorksPartFingerprintExtractor> _logger;

    public SolidWorksPartFingerprintExtractor(
        StaSolidWorksWorker worker,
        ILogger<SolidWorksPartFingerprintExtractor> logger)
    {
        _worker = worker;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // COM release helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void ReleaseCom(object? obj)
    {
        if (obj != null && Marshal.IsComObject(obj))
        {
            try { Marshal.ReleaseComObject(obj); }
            catch { /* ignore — object may already be released */ }
        }
    }

    private static void ReleaseComArray(IEnumerable<object?> objects)
    {
        foreach (var obj in objects) ReleaseCom(obj);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Routes extraction to the dedicated STA thread.</summary>
    public Task<PartFingerprint?> ExtractAsync(
        ScannedFile file, string configName, CancellationToken cancellationToken)
        => _worker.RunAsync(() => ExtractOnSta(file, configName));

    // ─────────────────────────────────────────────────────────────────────────
    // STA thread: open → extract → close
    // ─────────────────────────────────────────────────────────────────────────

    private PartFingerprint? ExtractOnSta(ScannedFile file, string configName)
    {
        var sw = _worker.GetOrCreateSwApp();
        if (sw == null)
        {
            _logger.LogError("SolidWorks is not available; cannot extract fingerprint for {File}", file.FileName);
            return null;
        }

        bool weOpened = false;
        IModelDoc2? doc = null;

        try
        {
            // Reuse an already-open document rather than opening a second copy.
            var existingObj = sw.GetOpenDocumentByName(file.NormalizedPath);
            if (existingObj is IModelDoc2 existing)
            {
                doc = existing;
                weOpened = false;
            }
            else
            {
                int errors = 0, warnings = 0;
                var openedObj = sw.OpenDoc6(
                    file.NormalizedPath, SwDocPart, OpenOpts,
                    configName, ref errors, ref warnings);

                if (openedObj is not IModelDoc2 opened)
                {
                    _logger.LogWarning(
                        "OpenDoc6 returned null for {File} (errors={E}, warnings={W})",
                        file.FileName, errors, warnings);
                    return null;
                }

                doc = opened;
                weOpened = true;

                if (errors != 0)
                {
                    _logger.LogWarning(
                        "OpenDoc6 errors={E} warnings={W} for {File} — continuing extraction",
                        errors, warnings, file.FileName);
                }
            }

            return BuildFingerprint(file, configName, doc, sw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fingerprint extraction failed for {File}", file.FileName);
            return null;
        }
        finally
        {
            if (weOpened && doc != null)
            {
                try { sw.CloseDoc(file.NormalizedPath); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CloseDoc failed for {File}", file.FileName);
                }
                // Release the document RCW so SW can free the COM object's memory.
                ReleaseCom(doc);
            }

            // Non-blocking GC hint: drains surviving RCWs without blocking the STA thread.
            // Prevents COM RCW accumulation on long scans of large folders.
            GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Geometry extraction helpers (all on STA thread)
    // ─────────────────────────────────────────────────────────────────────────

    private PartFingerprint BuildFingerprint(
        ScannedFile file, string configName, IModelDoc2 doc, ISldWorks sw)
    {
        var (solidCount, sheetCount, sortedBB, rawBB, faces, edges, verts) = ExtractBodies(doc, file.FileName);
        var (volumeM3, surfAreaM2, massKg, com) = ExtractMassProps(doc, file.FileName);
        var (featureCount, histogram, sketchTextCutCount, suppressionCandidates) =
            ExtractFeatures(doc, file.FileName);
        var (material, customProps) = ExtractMetadata(doc, configName, file.FileName);
        var chiralitySign = ExtractChirality(doc, file.FileName);
        var comOffsetInBB = ComputeCoMOffsetInBB(com, rawBB);
        var faceSignature = ExtractFaceGeometricSignature(doc, file.FileName);

        if (sketchTextCutCount > 0)
            _logger.LogInformation(
                "Sketch text detected in {File}: {Count} feature(s) with ISketchText; " +
                "{S} candidate(s) for suppression",
                file.FileName, sketchTextCutCount, suppressionCandidates.Count);

        // Suppress engraving features, rebuild, and capture base-part geometry.
        int? suppSolidCount = null;
        double[]? suppBB    = null;
        double? suppVol     = null;
        double? suppSA      = null;
        int? suppFace       = null;
        int? suppEdge       = null;
        int? suppVert       = null;

        if (suppressionCandidates.Count > 0)
        {
            try
            {
                (suppSolidCount, suppBB, suppVol, suppSA, suppFace, suppEdge, suppVert) =
                    TryExtractSuppressedGeometry(doc, suppressionCandidates, file.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Suppression block threw unexpectedly for {File} — suppressed fields cleared",
                    file.FileName);
            }
            finally
            {
                // Release suppression candidate IFeature COM objects now that suppression is done.
                ReleaseComArray(suppressionCandidates.Cast<object?>());
                suppressionCandidates.Clear();
            }
        }

        string swVersion = "SW2024";
        try { swVersion = sw.RevisionNumber(); }
        catch { /* non-critical */ }

        return new PartFingerprint(
            Id: Guid.NewGuid(),
            ScannedFileId: file.Id,
            FileSha256: file.Sha256 ?? string.Empty,
            ConfigName: configName,
            ExtractorVersion: ExtractorVersion,
            SolidBodyCount: solidCount,
            SurfaceBodyCount: sheetCount,
            SortedBoundingBoxM: sortedBB,
            VolumeM3: volumeM3,
            SurfaceAreaM2: surfAreaM2,
            MassKg: massKg > 0 ? massKg : null,
            CenterOfMassM: com,
            FaceCount: faces,
            EdgeCount: edges,
            VertexCount: verts,
            FeatureCount: featureCount,
            FeatureTypeHistogram: histogram,
            Material: material,
            CustomProperties: customProps,
            SolidWorksVersion: swVersion,
            ExtractorVersionLabel: ExtractorLabel,
            ExtractedUtc: DateTime.UtcNow,
            ChiralitySign: chiralitySign,
            CoMOffsetInBB: comOffsetInBB,
            SketchTextCutCount: sketchTextCutCount,
            SuppressedSolidBodyCount: suppSolidCount,
            SuppressedBoundingBoxM: suppBB,
            SuppressedVolumeM3: suppVol,
            SuppressedSurfaceAreaM2: suppSA,
            SuppressedFaceCount: suppFace,
            SuppressedEdgeCount: suppEdge,
            SuppressedVertexCount: suppVert,
            SourceFormat: "SLDPRT",
            FaceGeometricSignature: faceSignature);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Face geometric signature (cross-format comparison key)
    // Extracts sorted canonical face descriptor strings in the same format produced
    // by StepGeometryExtractor so signatures are directly comparable across formats.
    //
    // SW API refs (verified against SolidWorks.Interop.sldworks, SW 2024):
    //   IBody2.GetFaces()         → object[] of IFace2
    //   IFace2.GetSurface()       → object (cast to ISurface)
    //   ISurface.IsPlane/IsCylinder/IsCone/IsSphere/IsTorus() → bool
    //   ISurface.PlaneParams      → double[6]: [nx,ny,nz, ox,oy,oz]
    //   ISurface.CylinderParams   → double[7]: [ox,oy,oz, nx,ny,nz, r]   (SI metres)
    //   ISurface.ConeParams       → double[8]: [ox,oy,oz, nx,ny,nz, ha, r]
    //   ISurface.SphereParams     → double[4]: [cx,cy,cz, r]
    //   ISurface.TorusParams      → double[8]: [cx,cy,cz, nx,ny,nz, R_major, r_minor]
    // ─────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<string>? ExtractFaceGeometricSignature(IModelDoc2 doc, string fileName)
    {
        var toRelease = new List<object>();
        try
        {
            var partDoc = (IPartDoc)doc;
            var solidObj = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, false);
            if (solidObj is not object[] bodies || bodies.Length == 0) return null;

            var descriptors = new List<string>();

            foreach (var bObj in bodies)
            {
                if (bObj is not IBody2 body) { if (bObj != null) toRelease.Add(bObj); continue; }
                toRelease.Add(body);

                var facesObj = body.GetFaces();
                if (facesObj is not object[] faceArr) continue;

                foreach (var fObj in faceArr)
                {
                    if (fObj is not IFace2 face) { if (fObj != null) toRelease.Add(fObj); continue; }
                    toRelease.Add(face);

                    var surfObj = face.GetSurface();
                    if (surfObj is not ISurface surf)
                    {
                        if (surfObj != null) toRelease.Add(surfObj);
                        continue;
                    }
                    toRelease.Add(surf);

                    var desc = BuildSurfaceDescriptor(surf);
                    if (desc != null) descriptors.Add(desc);
                }
            }

            if (descriptors.Count == 0) return null;
            descriptors.Sort(StringComparer.Ordinal);
            return descriptors.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Face geometric signature extraction failed for {File}", fileName);
            return null;
        }
        finally
        {
            ReleaseComArray(toRelease);
        }
    }

    private static string? BuildSurfaceDescriptor(ISurface surf)
    {
        try
        {
            if (surf.IsPlane())
            {
                if (surf.PlaneParams is not double[] p || p.Length < 6) return null;
                double[] n = [p[0], p[1], p[2]];
                CanonicalizeAxis(n);
                return $"PLANE|{n[0]:F4}|{n[1]:F4}|{n[2]:F4}";
            }
            if (surf.IsCylinder())
            {
                if (surf.CylinderParams is not double[] p || p.Length < 7) return null;
                double[] axis = [p[3], p[4], p[5]];
                CanonicalizeAxis(axis);
                return $"CYLINDER|{p[6]:R}|{axis[0]:F4}|{axis[1]:F4}|{axis[2]:F4}";
            }
            if (surf.IsCone())
            {
                if (surf.ConeParams is not double[] p || p.Length < 8) return null;
                double[] axis = [p[3], p[4], p[5]];
                CanonicalizeAxis(axis);
                return $"CONE|{p[6]:F6}|{p[7]:R}|{axis[0]:F4}|{axis[1]:F4}|{axis[2]:F4}";
            }
            if (surf.IsSphere())
            {
                if (surf.SphereParams is not double[] p || p.Length < 4) return null;
                return $"SPHERE|{p[3]:R}";
            }
            if (surf.IsTorus())
            {
                if (surf.TorusParams is not double[] p || p.Length < 8) return null;
                return $"TORUS|{p[6]:R}|{p[7]:R}";
            }
            return null; // B-spline or complex surface — skip for signature
        }
        catch { return null; }
    }

    // Normalizes a direction vector so the dominant component is positive.
    // Same logic as StepGeometryExtractor.CanonicalizeAxis — ensures cross-format
    // descriptors are comparable when axis directions differ by sign convention.
    private static void CanonicalizeAxis(double[] axis)
    {
        if (axis.Length < 3) return;
        int dom = 0;
        for (int i = 1; i < 3; i++)
            if (Math.Abs(axis[i]) > Math.Abs(axis[dom])) dom = i;
        if (axis[dom] < 0)
            for (int i = 0; i < axis.Length; i++) axis[i] = -axis[i];
        for (int i = 0; i < axis.Length; i++)
            if (Math.Abs(axis[i]) < 1e-9) axis[i] = 0.0;
    }

    // Computes the sign of det([principalAxis1 | principalAxis2 | principalAxis3]).
    // Returns +1 for right-handed geometry, -1 for its mirror, null if unavailable.
    // VALIDATION NOTE: IMassProperty2.PrincipalAxesOfInertia must be verified against
    // the installed SW 2024 interop assembly before relying on this value in production.
    private double? ExtractChirality(IModelDoc2 doc, string fileName)
    {
        object? mpObj = null;
        try
        {
            mpObj = doc.Extension.CreateMassProperty2();
            if (mpObj is not IMassProperty2 mp) return null;

            mp.UseSystemUnits = true;
            if (!mp.Recalculate()) return null;

            // PrincipalAxesOfInertia is an indexed property: [nAxis] → double[3] {x,y,z}.
            // VALIDATION NOTE: verify axis indices 0-2 against installed SW 2024 interop.
            static double[]? ToAxis(object? obj)
            {
                if (obj is double[] da && da.Length >= 3) return da;
                if (obj is object[] oa && oa.Length >= 3) return Array.ConvertAll(oa, o => Convert.ToDouble(o));
                return null;
            }

            var a3 = ToAxis(mp.PrincipalAxesOfInertia[0]);
            var b3 = ToAxis(mp.PrincipalAxesOfInertia[1]);
            var c3 = ToAxis(mp.PrincipalAxesOfInertia[2]);
            if (a3 == null || b3 == null || c3 == null) return null;

            // det([a|b|c]) = a·(b×c)
            double ax = a3[0], ay = a3[1], az = a3[2];
            double bx = b3[0], by = b3[1], bz = b3[2];
            double cx = c3[0], cy = c3[1], cz = c3[2];

            double det = ax * (by * cz - bz * cy)
                       - ay * (bx * cz - bz * cx)
                       + az * (bx * cy - by * cx);

            if (Math.Abs(det) < 1e-9) return null;
            return det > 0 ? 1.0 : -1.0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Chirality extraction failed for {File}", fileName);
            return null;
        }
        finally
        {
            ReleaseCom(mpObj);
        }
    }

    private (int solidCount, int sheetCount, double[] sortedBB, double[] rawBB,
             int faces, int edges, int verts)
        ExtractBodies(IModelDoc2 doc, string fileName)
    {
        object[]? solidBodies = null;
        object[]? sheetBodies = null;
        try
        {
            var partDoc = (IPartDoc)doc;

            var solidObj = partDoc.GetBodies2((int)swBodyType_e.swSolidBody, false);
            solidBodies = solidObj is object[] s ? s : [];

            var sheetObj = partDoc.GetBodies2((int)swBodyType_e.swSheetBody, false);
            sheetBodies = sheetObj is object[] sh ? sh : [];

            int faces = 0, edges = 0, verts = 0;
            double xMin = double.MaxValue, yMin = double.MaxValue, zMin = double.MaxValue;
            double xMax = double.MinValue, yMax = double.MinValue, zMax = double.MinValue;
            bool hasBB = false;

            foreach (var obj in solidBodies)
            {
                if (obj is not IBody2 body) continue;
                faces += body.GetFaceCount();
                edges += body.GetEdgeCount();
                verts += body.GetVertexCount();

                var boxObj = body.GetBodyBox();
                if (boxObj is double[] box && box.Length == 6)
                {
                    xMin = Math.Min(xMin, box[0]); yMin = Math.Min(yMin, box[1]); zMin = Math.Min(zMin, box[2]);
                    xMax = Math.Max(xMax, box[3]); yMax = Math.Max(yMax, box[4]); zMax = Math.Max(zMax, box[5]);
                    hasBB = true;
                }
            }

            double[] sortedBB = [0.0, 0.0, 0.0];
            // rawBB preserves signed axis order for CoM-offset computation.
            double[] rawBB = hasBB
                ? [xMin, yMin, zMin, xMax, yMax, zMax]
                : [0, 0, 0, 0, 0, 0];

            if (hasBB)
            {
                var dims = new[] { xMax - xMin, yMax - yMin, zMax - zMin };
                Array.Sort(dims);
                sortedBB = dims;
            }

            return (solidBodies.Length, sheetBodies.Length, sortedBB, rawBB, faces, edges, verts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Body extraction failed for {File}", fileName);
            return (0, 0, [0.0, 0.0, 0.0], [0, 0, 0, 0, 0, 0], 0, 0, 0);
        }
        finally
        {
            // Release IBody2 COM objects — these are real separate COM objects from the document.
            if (solidBodies != null) ReleaseComArray(solidBodies.Cast<object?>());
            if (sheetBodies != null) ReleaseComArray(sheetBodies.Cast<object?>());
        }
    }

    // CoM position as a fraction (0..1) of each BB dimension, sorted by dimension size.
    // Mirrored parts have ratioA[k] + ratioB[k] ≈ 1 on the mirrored axis.
    private static double[]? ComputeCoMOffsetInBB(double[]? com, double[] rawBB)
    {
        if (com == null || com.Length < 3) return null;
        var dimRatios = new[]
        {
            (size: rawBB[3] - rawBB[0], ratio: (com[0] - rawBB[0]) / Math.Max(rawBB[3] - rawBB[0], 1e-10)),
            (size: rawBB[4] - rawBB[1], ratio: (com[1] - rawBB[1]) / Math.Max(rawBB[4] - rawBB[1], 1e-10)),
            (size: rawBB[5] - rawBB[2], ratio: (com[2] - rawBB[2]) / Math.Max(rawBB[5] - rawBB[2], 1e-10)),
        };
        Array.Sort(dimRatios, (a, b) => a.size.CompareTo(b.size));
        return [dimRatios[0].ratio, dimRatios[1].ratio, dimRatios[2].ratio];
    }

    private (double volume, double surfaceArea, double mass, double[]? centerOfMass)
        ExtractMassProps(IModelDoc2 doc, string fileName)
    {
        object? mpObj = null;
        try
        {
            mpObj = doc.Extension.CreateMassProperty2();
            if (mpObj is not IMassProperty2 mp)
                return (0, 0, 0, null);

            mp.UseSystemUnits = true;
            if (!mp.Recalculate())
            {
                _logger.LogWarning("MassProperty.Recalculate() failed for {File} — mass data may be zeroed", fileName);
            }

            double[]? com = null;
            var comObj = mp.CenterOfMass;
            if (comObj is double[] comArr)
                com = comArr;
            else if (comObj is object[] comObjArr)
                com = Array.ConvertAll(comObjArr, o => Convert.ToDouble(o));

            return (mp.Volume, mp.SurfaceArea, mp.Mass, com);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mass property extraction failed for {File}", fileName);
            return (0, 0, 0, null);
        }
        finally
        {
            ReleaseCom(mpObj);
        }
    }

    private (int count, Dictionary<string, int> histogram, int sketchTextCutCount,
             List<IFeature> suppressionCandidates)
        ExtractFeatures(IModelDoc2 doc, string fileName)
    {
        var suppressionCandidates = new List<IFeature>();
        // Features not needed after this method — release them here.
        // Suppression candidates are released in BuildFingerprint after their use.
        var releasableFeatures = new List<object>();

        try
        {
            var fm = doc.FeatureManager;
            int count = fm.GetFeatureCount(true);

            var histogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int sketchTextCuts = 0;

            var featsObj = fm.GetFeatures(true);
            if (featsObj is object[] feats)
            {
                foreach (var obj in feats)
                {
                    if (obj is not IFeature feat) continue;
                    var typeName = feat.GetTypeName2() ?? "Unknown";
                    // Instant3D extrudes report "ICE" — unwrap to the underlying type.
                    if (typeName.Equals("ICE", StringComparison.Ordinal))
                        typeName = feat.GetTypeName() ?? "Unknown";
                    histogram[typeName] = histogram.GetValueOrDefault(typeName) + 1;

                    // Count sketch text on ALL feature types (broad detection for flagging).
                    if (FeatureHasSketchText(feat))
                    {
                        sketchTextCuts++;
                        // Only CutExtrusion and WrapSketch features actually remove solid
                        // material — those are the ones worth suppressing for comparison.
                        if (IsSuppressableEngravingType(typeName))
                        {
                            suppressionCandidates.Add(feat);
                            continue; // do not add to releasableFeatures — caller releases later
                        }
                    }

                    releasableFeatures.Add(feat);
                }
            }

            return (count, histogram, sketchTextCuts, suppressionCandidates);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Feature extraction failed for {File}", fileName);
            // Also release any suppression candidates collected before the exception.
            ReleaseComArray(suppressionCandidates.Cast<object?>());
            suppressionCandidates.Clear();
            return (0, new Dictionary<string, int>(), 0, suppressionCandidates);
        }
        finally
        {
            // Release non-candidate IFeature COM objects immediately.
            ReleaseComArray(releasableFeatures);
        }
    }

    // Features that physically remove or deform solid material when suppressed.
    // Suppressing these reveals the "base part" geometry for engraving comparison.
    private static bool IsSuppressableEngravingType(string typeName) =>
        typeName.Equals("CutExtrusion", StringComparison.OrdinalIgnoreCase) ||
        typeName.StartsWith("WrapSketch", StringComparison.OrdinalIgnoreCase);

    // Suppresses all candidates, rebuilds, extracts base geometry, then restores.
    // Returns all nulls on any failure so the fingerprint falls back to full geometry.
    private (int? solidCount, double[]? sortedBB, double? volumeM3, double? surfAreaM2,
             int? faceCount, int? edgeCount, int? vertexCount)
        TryExtractSuppressedGeometry(IModelDoc2 doc, List<IFeature> candidates, string fileName)
    {
        // Pass an empty array instead of null for the configurationNames parameter.
        // On SW 2016-2018, passing null (VT_NULL VARIANT) for this parameter can cause
        // an access violation in SW native code that silently kills the process.
        object emptyConfigNames = Array.Empty<string>();

        try
        {
            foreach (var feat in candidates)
                feat.SetSuppression2(SwSuppressed, SwThisConfig, emptyConfigNames);

            bool rebuilt = doc.ForceRebuild3(false);
            if (!rebuilt)
            {
                _logger.LogDebug(
                    "ForceRebuild3 returned false after suppression in {File} — suppressed fields cleared",
                    fileName);
                return (null, null, null, null, null, null, null);
            }

            var (solidCount, _, sortedBB, _, faceCount, edgeCount, vertexCount) =
                ExtractBodies(doc, fileName);
            var (volume, surfArea, _, _) = ExtractMassProps(doc, fileName);

            if (solidCount <= 0 || volume <= 0)
            {
                _logger.LogDebug(
                    "Suppressed model returned no valid geometry (bodies={B} vol={V}) for {File}",
                    solidCount, volume, fileName);
                return (null, null, null, null, null, null, null);
            }

            _logger.LogDebug(
                "Suppressed geometry for {File}: bodies={B} vol={V:F6} faces={F}",
                fileName, solidCount, volume, faceCount);

            return (solidCount, sortedBB, volume,
                    surfArea > 0 ? surfArea : (double?)null,
                    faceCount, edgeCount, vertexCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Suppressed-geometry extraction failed for {File}", fileName);
            return (null, null, null, null, null, null, null);
        }
        finally
        {
            foreach (var feat in candidates)
            {
                try { feat.SetSuppression2(SwUnsuppressed, SwThisConfig, emptyConfigNames); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Feature restore failed in {File}", fileName);
                }
            }
            try { doc.ForceRebuild3(false); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ForceRebuild3 after restore failed in {File}", fileName);
            }
        }
    }

    // Returns true when this feature (or any of its sub-features) owns an ISketch
    // that contains at least one ISketchText segment.
    // Tracks all created COM objects and releases them in the finally block.
    private static bool FeatureHasSketchText(IFeature feat)
    {
        var toRelease = new List<object>();
        try
        {
            // Direct: plain sketch features expose ISketch via GetSpecificFeature2().
            var specificObj = feat.GetSpecificFeature2();
            if (specificObj != null)
            {
                toRelease.Add(specificObj);
                if (specificObj is ISketch directSketch)
                {
                    var segs = directSketch.GetSketchTextSegments() as object[];
                    if (segs?.Length > 0) return true;
                }
            }

            // Sub-features: profile sketches of extrusions, cuts, etc.
            IFeature? sub = feat.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                toRelease.Add(sub);
                var subSpecific = sub.GetSpecificFeature2();
                if (subSpecific != null)
                {
                    toRelease.Add(subSpecific);
                    if (subSpecific is ISketch subSketch)
                    {
                        var segs = subSketch.GetSketchTextSegments() as object[];
                        if (segs?.Length > 0) return true;
                    }
                }
                // GetNextSubFeature() is called before sub is released — sub is still valid.
                sub = sub.GetNextSubFeature() as IFeature;
            }
        }
        catch { /* non-critical — skip this feature */ }
        finally
        {
            ReleaseComArray(toRelease);
        }
        return false;
    }

    private (string? material, Dictionary<string, string> customProps)
        ExtractMetadata(IModelDoc2 doc, string configName, string fileName)
    {
        string? material = null;
        var customProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Material via part document API
        try
        {
            var partDoc = (IPartDoc)doc;
            string db = string.Empty;
            var mat = partDoc.GetMaterialPropertyName2(configName, out db);
            if (!string.IsNullOrWhiteSpace(mat)) material = mat;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Material extraction failed for {File}", fileName); }

        // Custom properties (document-level, config "")
        object? cpmObj = null;
        try
        {
            cpmObj = doc.Extension.get_CustomPropertyManager(string.Empty);
            if (cpmObj is ICustomPropertyManager mgr)
            {
                object namesObj = null!, typesObj = null!, valuesObj = null!,
                       resolvedObj = null!, linkObj = null!;
                int propCount = mgr.GetAll3(
                    ref namesObj, ref typesObj, ref valuesObj, ref resolvedObj, ref linkObj);

                if (propCount > 0
                    && namesObj is string[] names
                    && resolvedObj is string[] resolved)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        var name = names[i];
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var val = i < resolved.Length ? resolved[i] ?? string.Empty : string.Empty;
                        customProps[name] = val;

                        if (material == null
                            && name.Equals("Material", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(val))
                        {
                            material = val;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Custom property extraction failed for {File}", fileName); }
        finally
        {
            ReleaseCom(cpmObj);
        }

        return (material, customProps);
    }
}
