using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Orchestration;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// A Hole Wizard hole and a plain cut-extrude are different engineering specifications, so a pair
/// where exactly one side uses the Hole Wizard must never auto-merge — and the report must be able
/// to name <em>which</em> file uses which. Engraving is surfaced the same way.
/// </summary>
public sealed class HoleSpecAndEngravingTests
{
    private static PartFingerprint Part(
        Dictionary<string, int>? features = null, int engravedTextCuts = 0) => new(
            Id: new Guid("11111111-1111-1111-1111-111111111111"),
            ScannedFileId: new Guid("22222222-2222-2222-2222-222222222222"),
            FileSha256: "sha",
            ConfigName: "Default",
            ExtractorVersion: 8,
            SolidBodyCount: 1,
            SurfaceBodyCount: 0,
            SortedBoundingBoxM: [0.01, 0.02, 0.03],
            VolumeM3: 0.001,
            SurfaceAreaM2: 0.05,
            MassKg: null,
            CenterOfMassM: null,
            FaceCount: 12,
            EdgeCount: 20,
            VertexCount: 10,
            FeatureCount: 3,
            FeatureTypeHistogram: features ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            Material: null,
            CustomProperties: new Dictionary<string, string>(),
            SolidWorksVersion: "2024",
            ExtractorVersionLabel: "test",
            ExtractedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ChiralitySign: null,
            CoMOffsetInBB: null,
            SketchTextCutCount: engravedTextCuts,
            SuppressedSolidBodyCount: null,
            SuppressedBoundingBoxM: null,
            SuppressedVolumeM3: null,
            SuppressedSurfaceAreaM2: null,
            SuppressedFaceCount: null,
            SuppressedEdgeCount: null,
            SuppressedVertexCount: null);

    private static Dictionary<string, int> Features(params string[] types)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types) d[t] = 1;
        return d;
    }

    // ── PartFeatureFacts ────────────────────────────────────────────────────

    [Theory]
    [InlineData("HoleWzd")]
    [InlineData("HoleWzd3")]      // SW appends a suffix per hole type
    [InlineData("holewzd")]       // case-insensitive
    public void HasHoleWizard_RecognisesHoleWizardFeatureTypes(string typeName)
    {
        PartFeatureFacts.HasHoleWizard(Part(Features("Extrusion", typeName))).Should().BeTrue();
    }

    [Fact]
    public void HasHoleWizard_False_ForPlainCutExtrudeOnly()
    {
        PartFeatureFacts.HasHoleWizard(Part(Features("Extrusion", "Cut", "Fillet"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("Cut")]          // cut-extrude
    [InlineData("SweptCut")]
    [InlineData("CutRevolve")]
    [InlineData("cut")]          // case-insensitive
    public void HasPlainCutFeature_RecognisesCutFamilyFeatures(string typeName)
    {
        PartFeatureFacts.HasPlainCutFeature(Part(Features("Extrusion", typeName))).Should().BeTrue();
    }

    [Fact]
    public void HasPlainCutFeature_False_WhenPartHasNoCutAtAll()
    {
        // The distinction that matters: a part with no cut features must never be reported as
        // "uses a plain cut extrude".
        PartFeatureFacts.HasPlainCutFeature(Part(Features("Extrusion", "Fillet"))).Should().BeFalse();
    }

    [Fact]
    public void HasPlainCutFeature_False_ForHoleWizardOnly()
    {
        // "HoleWzd" contains no "cut", so a wizard-only part is not mistaken for a plain cut.
        PartFeatureFacts.HasPlainCutFeature(Part(Features("Extrusion", "HoleWzd"))).Should().BeFalse();
    }

    [Fact]
    public void EngravedTextCount_ReflectsSketchTextCutCount()
    {
        PartFeatureFacts.EngravedTextCount(Part(engravedTextCuts: 2)).Should().Be(2);
        PartFeatureFacts.EngravedTextCount(Part()).Should().Be(0);
    }

    // ── HoleSpecConflict ────────────────────────────────────────────────────

    [Fact]
    public void HoleSpecConflict_WhenOnlyAUsesHoleWizard_ReportsConflictAndNamesA()
    {
        var a = Part(Features("HoleWzd"));
        var b = Part(Features("Cut"));

        var (conflict, aUsesWizard) = ScanOrchestrationService.HoleSpecConflict(a, b);

        conflict.Should().BeTrue();
        aUsesWizard.Should().BeTrue("the report must be able to say which file uses the Hole Wizard");
    }

    [Fact]
    public void HoleSpecConflict_WhenOnlyBUsesHoleWizard_ReportsConflictAndNamesB()
    {
        var a = Part(Features("Cut"));
        var b = Part(Features("HoleWzd"));

        var (conflict, aUsesWizard) = ScanOrchestrationService.HoleSpecConflict(a, b);

        conflict.Should().BeTrue();
        aUsesWizard.Should().BeFalse();
    }

    [Fact]
    public void HoleSpecConflict_False_WhenBothUseHoleWizard()
    {
        var both = Part(Features("HoleWzd"));
        ScanOrchestrationService.HoleSpecConflict(both, both).Conflict.Should().BeFalse();
    }

    [Fact]
    public void HoleSpecConflict_False_WhenNeitherUsesHoleWizard()
    {
        var neither = Part(Features("Cut", "Extrusion"));
        ScanOrchestrationService.HoleSpecConflict(neither, neither).Conflict.Should().BeFalse();
    }
}
