using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// <see cref="StepGeometryExtractor"/> against the committed known-geometry fixture. Pure P21 parsing
/// — no SolidWorks, no OCCT, no display.
/// </summary>
public sealed class StepGeometryExtractorTests
{
    private static readonly string BoxFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "known-box.step");

    private static PartFingerprint ExtractBox()
    {
        var file = new ScannedFile(
            Guid.NewGuid(), BoxFixture, "known-box.step", 0, DateTime.UtcNow,
            "SHA-BOX", Path.GetDirectoryName(BoxFixture)!, FileStatus.Hashed, null);

        var fp = new StepGeometryExtractor(NullLogger<StepGeometryExtractor>.Instance).Extract(file);
        fp.Should().NotBeNull();
        return fp!;
    }

    [Fact]
    public void Extract_KnownBox_PopulatesRealEdgeAndVertexCounts()
    {
        // These were hardcoded to 0 before extractor v102, which floored TopologySimilarity at 0.667
        // for every STEP pair: ScalarSimilarity(0, 0) returns a free 1.0, so two of its three terms
        // were always perfect regardless of the geometry. A box has 6 faces, 12 edges, 8 vertices.
        var box = ExtractBox();

        box.FaceCount.Should().Be(6);
        box.EdgeCount.Should().Be(12);
        box.VertexCount.Should().Be(8);
    }

    [Fact]
    public void Extract_TagsGeometryAsEstimated_UntilTheOcctPassUpgradesIt()
    {
        // The extractor itself only ever produces P21 estimates. The scan orchestrator's OCCT pass is
        // what overrides the volume/area/box and promotes this to "occt". Anything downstream that
        // compares fine-grained deltas (StepEngravingDetector) keys off exactly this.
        ExtractBox().GeometrySource.Should().Be("step-estimate");
    }

    [Fact]
    public void Extract_StampsTheBumpedExtractorVersion_SoStaleCachedStepFingerprintsMiss()
    {
        var box = ExtractBox();

        box.ExtractorVersion.Should().Be(102);
        box.ExtractorVersionLabel.Should().Be("step-p21-3");
        box.SourceFormat.Should().Be("STEP");
    }
}
