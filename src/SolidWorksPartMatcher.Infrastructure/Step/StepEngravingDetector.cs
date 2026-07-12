using SolidWorksPartMatcher.Domain.Models;

namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>
/// Detects that two STEP parts are the same part, one of which carries an engraving (etched text,
/// a logo, a marking) — scan orchestrator Stage 3.7.
///
/// Why this exists: a STEP file has no feature tree, so the SLDPRT engraving checks
/// (<c>SketchTextCutCount</c>, feature suppression) are structurally dead for it. And an engraving
/// adds hundreds of tiny faces, which every other STEP stage reads as "different part": Stage 3.5
/// returns <see cref="PartClassification.Distinct"/> on any face-count mismatch, and three of Stage
/// 3.6's four vote flags are face-count-sensitive so it can never rescue the pair. The engraved
/// twin therefore vanishes from the UI entirely (Distinct groups are filtered out).
///
/// The recognisable shape of an engraving, and the gates below:
///   bounding box unchanged (the cut goes inward) · volume barely moves (a sliver of material) ·
///   surface area RISES (letter side-walls are new surface) · face count jumps a lot ·
///   and every one of the base part's faces survives into the engraved part.
///
/// That last one is the load-bearing signal, and it needs a primitive the codebase did not have. A
/// face descriptor encodes surface type + axis + radius but NOT position or trim loops, so an
/// engraved host face keeps its descriptor (a cut changes its loops, not its plane) and a host face
/// split by the cut merely repeats that descriptor. So the plain part's descriptors are a
/// multiset SUBSET of the engraved part's. Both existing comparers divide by <c>max(|A|,|B|)</c> —
/// an agreement score, which an engraving's hundreds of extra faces drive to near zero. Containment
/// (<c>matched / |plain|</c>, see <see cref="FaceSignatureMatcher.ContainmentFraction"/>) is the
/// question actually being asked.
///
/// NEVER auto-merges: a hit yields <see cref="PartClassification.EngravingVariant"/>, which is an
/// IsMatch() edge, so the pair joins a cluster — and every multi-member cluster is NeedsReview.
///
/// Pure C# — no SolidWorks, no subprocess — so it is fully unit-testable.
/// </summary>
internal static class StepEngravingDetector
{
    /// <param name="IsEngraving">The pair is the same part, one of them engraved.</param>
    /// <param name="Reason">Always populated. On a rejection: the gate that failed and its measured value.</param>
    /// <param name="NearMiss">
    /// Rejected, but only after clearing the size gates — the two parts genuinely share a bounding box
    /// and a volume and one has many more faces, yet a later gate said no. These are the pairs worth
    /// putting in the scan log (the callers gate their logging on this): every OTHER rejection is just
    /// two unrelated parts, and logging those would drown the signal. This is how the one uncalibrated
    /// threshold, <see cref="StepEngravingTolerances.MaxCurvedFractionOfAddedFaces"/>, gets its real
    /// number — from a real scan rather than from a guess shipped as a fact.
    /// </param>
    internal sealed record Result(bool IsEngraving, string Reason, bool NearMiss = false);

    /// <summary>
    /// Decides whether <paramref name="a"/> and <paramref name="b"/> are the same part differing only
    /// by an engraving. Symmetric in its arguments: the part with fewer faces is always treated as
    /// the plain one. <see cref="Result.Reason"/> is always populated — on a rejection it names the
    /// gate that failed and the measured value, so a scan log tells us how to calibrate.
    /// </summary>
    internal static Result Detect(PartFingerprint a, PartFingerprint b, StepEngravingTolerances tol)
    {
        // ── Scope ────────────────────────────────────────────────────────────────────────────────
        if (a.SourceFormat != "STEP" || b.SourceFormat != "STEP")
            return new Result(false, "not a STEP-STEP pair");

        if (a.FaceGeometricSignature is not { Count: > 0 } || b.FaceGeometricSignature is not { Count: > 0 })
            return new Result(false, "one or both parts have no face signature");

        if (a.SolidBodyCount != b.SolidBodyCount)
            return new Result(false, $"different body count (A={a.SolidBodyCount} B={b.SolidBodyCount})");

        // ── Kernel-measured geometry (see StepEngravingTolerances.RequireKernelMeasuredGeometry) ──
        // Without OCCT the volume and area are pure functions of the bounding box, so the three
        // numeric gates below would pass vacuously for ANY two parts that share a box.
        if (tol.RequireKernelMeasuredGeometry
            && (a.GeometrySource != "occt" || b.GeometrySource != "occt"))
            return new Result(false,
                "geometry is estimated, not kernel-measured (OCCT unavailable) — cannot distinguish "
                + "an engraving from a different part of the same size");

        // The plain part is the one with fewer faces; the engraved part adds them.
        var (plain, engraved) = a.FaceCount <= b.FaceCount ? (a, b) : (b, a);
        var plainSig = plain.FaceGeometricSignature!;
        var engravedSig = engraved.FaceGeometricSignature!;

        // ── Volume measurable, and barely different ──────────────────────────────────────────────
        double maxVol = Math.Max(plain.VolumeM3, engraved.VolumeM3);
        if (Math.Min(plain.VolumeM3, engraved.VolumeM3) <= tol.MinMeasurableVolumeM3)
            return new Result(false, "one or both volumes are unmeasurable (non-solid geometry)");

        double volDelta = Math.Abs(plain.VolumeM3 - engraved.VolumeM3) / maxVol;
        if (volDelta > tol.VolumeDeltaFraction)
            return new Result(false,
                $"volume Δ {volDelta * 100:0.###}% exceeds the {tol.VolumeDeltaFraction * 100:0.##}% "
                + "engraving limit — too much material moved");

        // ── Bounding box unchanged ───────────────────────────────────────────────────────────────
        var bbP = plain.SortedBoundingBoxM;
        var bbE = engraved.SortedBoundingBoxM;
        if (bbP.Length != 3 || bbE.Length != 3)
            return new Result(false, "bounding box unavailable");

        double worstBbDelta = 0;
        for (int i = 0; i < 3; i++)
            worstBbDelta = Math.Max(worstBbDelta, Math.Abs(bbP[i] - bbE[i]));
        if (worstBbDelta > tol.BoundingBoxToleranceM)
            return new Result(false,
                $"bounding box differs by {worstBbDelta * 1000:0.##} mm "
                + $"(limit {tol.BoundingBoxToleranceM * 1000:0.##} mm)");

        // ── Face count materially higher on the engraved side ────────────────────────────────────
        int addedFaces = engraved.FaceCount - plain.FaceCount;
        double faceRatio = plain.FaceCount > 0 ? (double)engraved.FaceCount / plain.FaceCount : 0;
        if (addedFaces < tol.MinAddedFaces && faceRatio < tol.MinFaceCountRatio)
            return new Result(false,
                $"only {addedFaces} more face(s) (×{faceRatio:0.##}) — too few to be an engraving "
                + $"(needs +{tol.MinAddedFaces} or ×{tol.MinFaceCountRatio:0.##})");

        // ── Everything past this point is a NEAR MISS if it fails ────────────────────────────────
        // The two parts share a bounding box and a volume, and one has materially more faces. They
        // really do look like a plain/engraved pair on the coarse numbers; only the fine-grained
        // surface evidence can now say otherwise. Worth logging.

        // ── Topology must not go backwards ───────────────────────────────────────────────────────
        // Adding a feature never REMOVES edges or vertices. Cheap, and it rules out a shape that
        // merely happens to have more faces. Skipped when a count is 0 (a pre-v102 cached row).
        if (plain.EdgeCount > 0 && engraved.EdgeCount > 0 && engraved.EdgeCount < plain.EdgeCount)
            return new Result(false,
                $"the many-faces part has FEWER edges ({engraved.EdgeCount} < {plain.EdgeCount}) — "
                + "not an added feature", NearMiss: true);
        if (plain.VertexCount > 0 && engraved.VertexCount > 0 && engraved.VertexCount < plain.VertexCount)
            return new Result(false,
                $"the many-faces part has FEWER vertices ({engraved.VertexCount} < {plain.VertexCount}) — "
                + "not an added feature", NearMiss: true);

        // ── Surface area must not shrink ─────────────────────────────────────────────────────────
        // An engraving strictly ADDS surface (letter side-walls). Gate on the direction, not the
        // magnitude: an ultra-shallow etch's area gain can be under export noise, and a missing area
        // must never block. The allowance is the same relative slack as the volume gate.
        double areaDelta = 0;
        if (plain.SurfaceAreaM2 > 0 && engraved.SurfaceAreaM2 > 0)
        {
            areaDelta = (engraved.SurfaceAreaM2 - plain.SurfaceAreaM2) / plain.SurfaceAreaM2;
            if (areaDelta < -tol.VolumeDeltaFraction)
                return new Result(false,
                    $"the many-faces part has LESS surface area ({areaDelta * 100:0.##}%) — an "
                    + "engraving adds surface, it does not remove it", NearMiss: true);
        }

        // ── Base-geometry containment: every face of the plain part survives into the engraved one ─
        // Exact (axis-retaining) key, deliberately: the two files are the same part from the same CAD
        // system in the same frame, so an axis disagreement is real evidence. The orientation-
        // invariant key would collapse every plane to one parameterless key, making containment
        // near-vacuous on a prismatic part.
        int matched = FaceSignatureMatcher.GreedyMatchCount(
            plainSig, engravedSig, FaceSignatureMatcher.ExactKey, tol.RadiusRelativeTolerance);

        int allowedMisses = Math.Max(
            tol.MinAbsoluteBaseFaceMisses,
            (int)Math.Ceiling((1.0 - tol.BaseContainmentFraction) * plainSig.Count));

        if (matched < plainSig.Count - allowedMisses)
            return new Result(false,
                $"only {matched}/{plainSig.Count} of the plain part's faces are present in the other "
                + $"(allowing {allowedMisses} miss(es)) — the base shapes differ, this is not an engraving",
                NearMiss: true);

        // ── Composition of the ADDED faces ───────────────────────────────────────────────────────
        // Letter strokes are planar walls and floors. A drilled-hole or fillet pattern — which can
        // otherwise pass every gate above — adds almost entirely cylinders/tori.
        var added = FaceSignatureMatcher.UnmatchedIn(
            engravedSig, plainSig, FaceSignatureMatcher.ExactKey, tol.RadiusRelativeTolerance);
        int curved = added.Count(FaceSignatureMatcher.IsCurved);
        double curvedFraction = added.Count > 0 ? (double)curved / added.Count : 0;

        if (curvedFraction > tol.MaxCurvedFractionOfAddedFaces)
            return new Result(false,
                $"{curvedFraction * 100:0}% of the {added.Count} added faces are curved "
                + $"(limit {tol.MaxCurvedFractionOfAddedFaces * 100:0}%) — this looks like added holes "
                + "or fillets, not an engraving", NearMiss: true);

        // ── Verdict ──────────────────────────────────────────────────────────────────────────────
        return new Result(true,
            $"Engraving variant: same bounding box (within {worstBbDelta * 1000:0.###} mm), "
            + $"volume Δ {volDelta * 100:0.###}% (below {tol.VolumeDeltaFraction * 100:0.##}%), "
            + $"surface area {areaDelta * 100:+0.##;-0.##;0}%; "
            + $"the engraved part has {engraved.FaceCount} faces vs {plain.FaceCount} (+{addedFaces}), "
            + $"{engraved.EdgeCount} vs {plain.EdgeCount} edges, "
            + $"{engraved.VertexCount} vs {plain.VertexCount} vertices; "
            + $"all {matched}/{plainSig.Count} base faces preserved; "
            + $"{curvedFraction * 100:0}% of the {added.Count} added faces are curved");
    }
}
