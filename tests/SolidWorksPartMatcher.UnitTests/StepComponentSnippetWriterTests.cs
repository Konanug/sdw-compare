using FluentAssertions;
using SolidWorksPartMatcher.Domain.Models;
using SolidWorksPartMatcher.Infrastructure.Step;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class StepComponentSnippetWriterTests : IDisposable
{
    private const string SourceP21 = """
        ISO-10303-21;
        HEADER;
        FILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));
        ENDSEC;
        DATA;
        #900=APPLICATION_CONTEXT('mechanical design');
        #100=PRODUCT('PART-A','PART-A',$,());
        #102=PRODUCT_DEFINITION_FORMATION('','001',#100);
        #103=PRODUCT_DEFINITION('PART-A','PART-A',#102,#900);
        #104=PRODUCT_DEFINITION_SHAPE('',$,#103);
        #105=SHAPE_DEFINITION_REPRESENTATION(#104,#106);
        #106=ADVANCED_BREP_SHAPE_REPRESENTATION('',(#110),#900);
        #107=CARTESIAN_POINT('',(0.,0.,0.));
        #108=DIRECTION('',(0.,0.,1.));
        #109=DIRECTION('',(1.,0.,0.));
        #111=AXIS2_PLACEMENT_3D('',#107,#108,#109);
        #112=PLANE('',#111);
        #113=CARTESIAN_POINT('',(10.,0.,0.));
        #114=CARTESIAN_POINT('',(10.,20.,0.));
        #115=CARTESIAN_POINT('',(0.,20.,0.));
        #110=ADVANCED_FACE('',(#113,#114,#115),#112,.T.);

        #500=PRODUCT('PART-B','PART-B',$,());
        #502=PRODUCT_DEFINITION_FORMATION('','001',#500);
        #503=PRODUCT_DEFINITION('PART-B','PART-B',#502,#900);
        ENDSEC;
        END-ISO-10303-21;
        """;

    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"snippet_test_{Guid.NewGuid():N}.step");

    [Fact]
    public void WriteSnippet_ProducesReparseableFile_WithSameFaceCount()
    {
        var sourceReader = StepP21Reader.ParseText(SourceP21);
        var structure = new StepAssemblyStructureReader(sourceReader).Read();
        var component = structure.Components.Single(c => c.MatchKey == "PART-A");

        StepComponentSnippetWriter.WriteSnippet(sourceReader, component, _tempPath);

        File.Exists(_tempPath).Should().BeTrue();

        var reparsed = StepP21Reader.ParseFile(_tempPath);
        reparsed.GetAdvancedFaces().Should().HaveCount(component.FaceCount);
        reparsed.SchemaName.Should().Be("CONFIG_CONTROL_DESIGN");
    }

    [Fact]
    public void WriteSnippet_ContainsFullClosure_IncludingProductIdentityChain()
    {
        var sourceReader = StepP21Reader.ParseText(SourceP21);
        var structure = new StepAssemblyStructureReader(sourceReader).Read();
        var component = structure.Components.Single(c => c.MatchKey == "PART-A");

        StepComponentSnippetWriter.WriteSnippet(sourceReader, component, _tempPath);
        var reparsed = StepP21Reader.ParseFile(_tempPath);

        foreach (var id in component.EntityClosure)
            reparsed.TryGetRaw(id, out _).Should().BeTrue();

        // The PRODUCT/PRODUCT_DEFINITION/FORMATION identity chain (#100/#102/#103) MUST be
        // present — without it, OCCT/build123d parses the snippet cleanly but recognizes no
        // transferable shape (confirmed against the real Test6 files: a closure containing only
        // geometry imports as an empty Compound; adding the identity chain fixes it).
        reparsed.TryGetRaw(100, out _).Should().BeTrue();
        reparsed.TryGetRaw(102, out _).Should().BeTrue();
        reparsed.TryGetRaw(103, out _).Should().BeTrue();

        // PART-B's entities are unrelated to PART-A's closure and must NOT leak in.
        reparsed.TryGetRaw(500, out _).Should().BeFalse();
        reparsed.TryGetRaw(502, out _).Should().BeFalse();
        reparsed.TryGetRaw(503, out _).Should().BeFalse();
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
