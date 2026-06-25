using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Orchestration;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class MaterialVariantTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PartFingerprint MakeFp(string sha = "abc", string? material = "Steel") => new(
        Id: Guid.NewGuid(),
        ScannedFileId: Guid.NewGuid(),
        FileSha256: sha,
        ConfigName: "Default",
        ExtractorVersion: 4,
        SolidBodyCount: 1,
        SurfaceBodyCount: 0,
        SortedBoundingBoxM: [0.05, 0.10, 0.20],
        VolumeM3: 0.001,
        SurfaceAreaM2: 0.05,
        MassKg: null,
        CenterOfMassM: null,
        FaceCount: 20,
        EdgeCount: 30,
        VertexCount: 15,
        FeatureCount: 3,
        FeatureTypeHistogram: new Dictionary<string, int> { ["Extrude"] = 2 },
        Material: material,
        CustomProperties: new Dictionary<string, string>(),
        SolidWorksVersion: "2024",
        ExtractorVersionLabel: "test-4",
        ExtractedUtc: DateTime.UtcNow,
        ChiralitySign: null,
        CoMOffsetInBB: null,
        SketchTextCutCount: 0,
        SuppressedSolidBodyCount: null,
        SuppressedBoundingBoxM: null,
        SuppressedVolumeM3: null,
        SuppressedSurfaceAreaM2: null,
        SuppressedFaceCount: null,
        SuppressedEdgeCount: null,
        SuppressedVertexCount: null);

    // ── ReclassifyMaterialVariant ────────────────────────────────────────────

    [Fact]
    public void DifferentMaterials_ExactGeometryMatch_BecomesMetadataVariant()
    {
        var a = MakeFp("sha1", "Steel");
        var b = MakeFp("sha2", "Aluminum");
        string reason = "initial";

        var result = ScanOrchestrationService.ReclassifyMaterialVariant(
            a, b, PartClassification.ExactGeometryMatch, ref reason);

        result.Should().Be(PartClassification.GeometryMatchMetadataVariant);
        reason.Should().Contain("material difference");
        reason.Should().Contain("Steel");
        reason.Should().Contain("Aluminum");
    }

    [Fact]
    public void SameMaterial_ExactGeometryMatch_StaysExactGeometryMatch()
    {
        var a = MakeFp("sha1", "Steel");
        var b = MakeFp("sha2", "Steel");
        string reason = "initial";

        var result = ScanOrchestrationService.ReclassifyMaterialVariant(
            a, b, PartClassification.ExactGeometryMatch, ref reason);

        result.Should().Be(PartClassification.ExactGeometryMatch);
    }

    [Fact]
    public void SameMaterial_CaseInsensitive_StaysExactGeometryMatch()
    {
        var a = MakeFp("sha1", "steel");
        var b = MakeFp("sha2", "STEEL");
        string reason = "initial";

        var result = ScanOrchestrationService.ReclassifyMaterialVariant(
            a, b, PartClassification.ExactGeometryMatch, ref reason);

        result.Should().Be(PartClassification.ExactGeometryMatch);
    }

    [Fact]
    public void BothMaterialsNull_StaysExactGeometryMatch()
    {
        var a = MakeFp("sha1", null);
        var b = MakeFp("sha2", null);
        string reason = "initial";

        var result = ScanOrchestrationService.ReclassifyMaterialVariant(
            a, b, PartClassification.ExactGeometryMatch, ref reason);

        result.Should().Be(PartClassification.ExactGeometryMatch);
    }

    [Fact]
    public void NonExactClassification_IsPassedThrough_Unchanged()
    {
        // Only ExactGeometryMatch is eligible for reclassification.
        var a = MakeFp("sha1", "Steel");
        var b = MakeFp("sha2", "Aluminum");
        string reason = "initial";

        var result = ScanOrchestrationService.ReclassifyMaterialVariant(
            a, b, PartClassification.PossibleMatch, ref reason);

        result.Should().Be(PartClassification.PossibleMatch);
    }

    [Fact]
    public void OneMaterialEmpty_OtherNonEmpty_BecomesMetadataVariant()
    {
        var a = MakeFp("sha1", "");
        var b = MakeFp("sha2", "Aluminum");
        string reason = "initial";

        // Empty string is not null — materials differ.
        var result = ScanOrchestrationService.ReclassifyMaterialVariant(
            a, b, PartClassification.ExactGeometryMatch, ref reason);

        result.Should().Be(PartClassification.GeometryMatchMetadataVariant);
    }
}
