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

    /// <summary>True when the part contains at least one Hole Wizard feature.</summary>
    public static bool HasHoleWizard(PartFingerprint fp) =>
        fp.FeatureTypeHistogram.Keys.Any(
            k => k.StartsWith(HoleWizardTypePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Number of engraved text features (sketch text driving a cut). 0 means no engraving.
    /// STEP files have no feature tree, so this is always 0 for them.
    /// </summary>
    public static int EngravedTextCount(PartFingerprint fp) => fp.SketchTextCutCount;
}
