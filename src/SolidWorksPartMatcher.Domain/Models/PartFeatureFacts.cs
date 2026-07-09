namespace SolidWorksPartMatcher.Domain.Models;

/// <summary>
/// Feature-level facts derived from a <see cref="PartFingerprint"/>'s SOLIDWORKS feature data.
/// Lives in Domain so both the scan pipeline (which classifies on these) and the UI (which reports
/// them) read the same definition — the two can never drift apart.
/// </summary>
public static class PartFeatureFacts
{
    /// <summary>
    /// SOLIDWORKS reports Hole Wizard features with a <c>GetTypeName2()</c> beginning with this
    /// prefix (e.g. "HoleWzd"). A hole cut with a plain cut-extrude carries no such feature.
    /// </summary>
    public const string HoleWizardTypePrefix = "HoleWzd";

    /// <summary>
    /// SOLIDWORKS names every cut-style feature with "cut" in its <c>GetTypeName2()</c>
    /// (cut-extrude, swept cut, revolved cut, …), while additive features (Extrusion, Fillet, …)
    /// never do — and neither does a Hole Wizard feature, which uses the prefix above. Matching on
    /// the fragment therefore recognises the family without hard-coding each individual type name.
    /// </summary>
    public const string CutFeatureNameFragment = "cut";

    /// <summary>True when the part contains at least one Hole Wizard feature.</summary>
    public static bool HasHoleWizard(PartFingerprint fp) =>
        fp.FeatureTypeHistogram.Keys.Any(
            k => k.StartsWith(HoleWizardTypePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the part contains at least one plain (non-Hole-Wizard) cut feature. Note this means
    /// "a cut exists", not "a hole exists" — a cut extrude may equally be a slot or a pocket. It is
    /// used only to distinguish a part that was cut by hand from one with no cut features at all, so
    /// the UI never claims a hole-less part "uses a plain cut extrude".
    /// </summary>
    public static bool HasPlainCutFeature(PartFingerprint fp) =>
        fp.FeatureTypeHistogram.Keys.Any(
            k => k.Contains(CutFeatureNameFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Number of engraved text features (sketch text driving a cut). 0 means no engraving.
    /// STEP files have no feature tree, so this is always 0 for them.
    /// </summary>
    public static int EngravedTextCount(PartFingerprint fp) => fp.SketchTextCutCount;
}
