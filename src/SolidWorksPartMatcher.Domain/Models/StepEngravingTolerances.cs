namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Thresholds for STEP engraving detection (scan orchestrator Stage 3.7). A STEP file has no feature
/// tree, so the SLDPRT engraving checks (SketchTextCutCount, feature suppression) cannot run; an
/// engraved STEP part must be recognised from geometry alone.
///
/// The shape of an engraving: the bounding box is unchanged (the cut goes inward), the volume barely
/// moves (a shallow etch removes a sliver), the surface area rises slightly (letter side-walls are
/// new surface), and the face count jumps a lot (every letter stroke is new faces) — while every one
/// of the base part's original faces survives.
///
/// Defaults are starting points, not calibrated final values, with one exception:
/// <see cref="VolumeDeltaFraction"/> was chosen by the user.
/// </summary>
public sealed record StepEngravingTolerances(
    // Each sorted bounding-box dimension must agree within this absolute distance. Matches the
    // SLDPRT engraving detector and WeightedCandidateScorer's own feature tolerance.
    double BoundingBoxToleranceM = 0.0005,               // 0.5 mm

    // |Δvolume| / max(volume) must be at or below this. An engraving removes a sliver of material;
    // anything more is a design change. This gate and RequireKernelMeasuredGeometry are the two
    // things standing between this detector and a false-positive generator — do not relax either
    // one to "make it fire".
    double VolumeDeltaFraction = 0.005,                  // 0.5%

    // The engraved side must have materially more faces. Absolute rule OR ratio rule — the absolute
    // one rejects "+1 drilled hole" (+2 faces) on a big part; the ratio one covers low-face-count
    // parts where +8 would be a large relative change.
    int MinAddedFaces = 8,
    double MinFaceCountRatio = 1.25,

    // Base-geometry containment: this fraction of the PLAIN side's faces must be found in the
    // engraved side's. An engraving preserves every original surface (descriptors encode surface
    // type/axis/radius, not the trim loops a cut changes) and merely adds more, so containment
    // should be ~1.0. MinAbsoluteBaseFaceMisses is a slack floor so a 12-face part isn't held to
    // literally 100%.
    double BaseContainmentFraction = 0.98,
    int MinAbsoluteBaseFaceMisses = 2,

    // Of the faces the engraved side ADDED, at most this fraction may be curved. Letter strokes are
    // planar walls and floors; a drilled-hole or fillet pattern — which can otherwise pass the box,
    // volume and containment gates — adds almost entirely cylinders/tori. THE ONE UNCALIBRATED VALUE:
    // deliberately loose, and every rejection is logged with the measured number so the first real
    // scan tells us what to set it to. Erring loose costs a false negative (today's behaviour), not
    // a false positive.
    double MaxCurvedFractionOfAddedFaces = 0.60,

    // Two face radii count as equal within this relative tolerance. Matches StepMatchTolerances.
    double RadiusRelativeTolerance = 0.01,

    // Both volumes must exceed this to be considered measurable at all.
    double MinMeasurableVolumeM3 = 1e-12,

    // Require both fingerprints to carry kernel-measured (OCCT) geometry. Load-bearing: without OCCT,
    // StepGeometryEstimator's volume (0.55 × bbVolume) and surface area (the box formula) are PURE
    // FUNCTIONS OF THE BOUNDING BOX, so two different parts sharing a box get bit-identical volume
    // and area — the box, volume and area gates would all pass VACUOUSLY and the detector would
    // collapse to "same box + more faces", merging genuinely different parts.
    bool RequireKernelMeasuredGeometry = true)
{
    public static readonly StepEngravingTolerances Default = new();
}
