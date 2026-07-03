using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Step;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

/// <summary>
/// Synthetic assembly: ROOT contains SUBASM (x2, via two separate NAUO edges) and PART-A
/// (directly, x1). SUBASM contains PART-A and PART-B (x1 each). SUBASM and ROOT are pure
/// containers (placement-only representation, no ADVANCED_FACE) and must NOT appear as leaf
/// components. PART-A is wired directly (SDR -> representation with the face straight on it);
/// PART-B is wired through the SHAPE_REPRESENTATION_RELATIONSHIP indirection observed in the
/// real Test6 sample files (placement-only SHAPE_REPRESENTATION -> SRR -> geometry-bearing
/// ADVANCED_BREP_SHAPE_REPRESENTATION), to cover both real-world wiring patterns.
///
/// Expected instance counts (root-to-leaf path counting):
///   PART-A: ROOT-direct (1) + ROOT-&gt;SUBASM(x2)-&gt;PART-A (2) = 3
///   PART-B: ROOT-&gt;SUBASM(x2)-&gt;PART-B (2) = 2
/// </summary>
public sealed class StepAssemblyStructureReaderTests
{
    private const string SyntheticAssembly = """
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

        #200=PRODUCT('PART-B','PART-B',$,());
        #202=PRODUCT_DEFINITION_FORMATION('','001',#200);
        #203=PRODUCT_DEFINITION('PART-B','PART-B',#202,#900);
        #204=PRODUCT_DEFINITION_SHAPE('',$,#203);
        #205=SHAPE_DEFINITION_REPRESENTATION(#204,#206);
        #206=SHAPE_REPRESENTATION('',(#207),#900);
        #207=AXIS2_PLACEMENT_3D('',#208,#209,#210);
        #208=CARTESIAN_POINT('',(0.,0.,0.));
        #209=DIRECTION('',(0.,0.,1.));
        #210=DIRECTION('',(1.,0.,0.));
        #211=SHAPE_REPRESENTATION_RELATIONSHIP('SRR','None',#206,#212);
        #212=ADVANCED_BREP_SHAPE_REPRESENTATION('',(#220),#900);
        #213=CARTESIAN_POINT('',(0.,0.,0.));
        #214=DIRECTION('',(0.,0.,1.));
        #215=DIRECTION('',(1.,0.,0.));
        #216=AXIS2_PLACEMENT_3D('',#213,#214,#215);
        #217=PLANE('',#216);
        #221=CARTESIAN_POINT('',(5.,0.,0.));
        #222=CARTESIAN_POINT('',(5.,8.,0.));
        #223=CARTESIAN_POINT('',(0.,8.,0.));
        #220=ADVANCED_FACE('',(#221,#222,#223),#217,.T.);

        #300=PRODUCT('SUBASM','SUBASM',$,());
        #302=PRODUCT_DEFINITION_FORMATION('','001',#300);
        #303=PRODUCT_DEFINITION('SUBASM','SUBASM',#302,#900);
        #304=PRODUCT_DEFINITION_SHAPE('',$,#303);
        #305=SHAPE_DEFINITION_REPRESENTATION(#304,#306);
        #306=SHAPE_REPRESENTATION('',(#307),#900);
        #307=AXIS2_PLACEMENT_3D('',#308,#309,#310);
        #308=CARTESIAN_POINT('',(0.,0.,0.));
        #309=DIRECTION('',(0.,0.,1.));
        #310=DIRECTION('',(1.,0.,0.));

        #400=PRODUCT('ROOT','ROOT',$,());
        #402=PRODUCT_DEFINITION_FORMATION('','001',#400);
        #403=PRODUCT_DEFINITION('ROOT','ROOT',#402,#900);
        #404=PRODUCT_DEFINITION_SHAPE('',$,#403);
        #405=SHAPE_DEFINITION_REPRESENTATION(#404,#406);
        #406=SHAPE_REPRESENTATION('',(#407),#900);
        #407=AXIS2_PLACEMENT_3D('',#408,#409,#410);
        #408=CARTESIAN_POINT('',(0.,0.,0.));
        #409=DIRECTION('',(0.,0.,1.));
        #410=DIRECTION('',(1.,0.,0.));

        #500=NEXT_ASSEMBLY_USAGE_OCCURRENCE('N1','N1','N1',#403,#303,'N1');
        #501=NEXT_ASSEMBLY_USAGE_OCCURRENCE('N2','N2','N2',#403,#303,'N2');
        #502=NEXT_ASSEMBLY_USAGE_OCCURRENCE('N3','N3','N3',#403,#103,'N3');
        #503=NEXT_ASSEMBLY_USAGE_OCCURRENCE('N4','N4','N4',#303,#103,'N4');
        #504=NEXT_ASSEMBLY_USAGE_OCCURRENCE('N5','N5','N5',#303,#203,'N5');

        ENDSEC;
        END-ISO-10303-21;
        """;

    [Fact]
    public void Read_FindsOnlyLeafComponents_ExcludingContainers()
    {
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        structure.Components.Select(c => c.MatchKey).Should()
            .BeEquivalentTo(["PART-A", "PART-B"]);
    }

    [Fact]
    public void Read_ResolvesGeometryThroughShapeRepresentationRelationshipIndirection()
    {
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        var partB = structure.Components.Single(c => c.MatchKey == "PART-B");
        partB.FaceCount.Should().Be(1);
    }

    [Fact]
    public void Read_EntityClosure_IncludesProductIdentityChainAndSrrLinkEntity()
    {
        // A standalone snippet built from this closure must be self-describing enough for a
        // real STEP reader (OCCT/build123d) to recognize a transferable shape — verified against
        // the real Test6 files, where geometry alone parses but yields zero shapes. Both PART-A
        // (wired directly) and PART-B (wired via the SHAPE_REPRESENTATION_RELATIONSHIP
        // indirection) must carry their own PRODUCT/PRODUCT_DEFINITION/FORMATION/PDS/SDR chain,
        // and PART-B must additionally carry the SRR entity itself (#211) — neither #206 nor
        // #212 references it back, so it's only included because it's explicitly seeded.
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        var partA = structure.Components.Single(c => c.MatchKey == "PART-A");
        partA.EntityClosure.Should().Contain([100, 102, 103, 104, 105]); // PRODUCT..SDR chain

        var partB = structure.Components.Single(c => c.MatchKey == "PART-B");
        partB.EntityClosure.Should().Contain([200, 202, 203, 204, 205, 211]); // chain + SRR link
    }

    [Fact]
    public void Read_ComputesInstanceCounts_MultipliedThroughNestedSubAssembly()
    {
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        var partA = structure.Components.Single(c => c.MatchKey == "PART-A");
        var partB = structure.Components.Single(c => c.MatchKey == "PART-B");

        partA.InstanceCount.Should().Be(3); // 1 direct + 2 via SUBASM
        partB.InstanceCount.Should().Be(2); // 2 via SUBASM only
    }

    [Fact]
    public void Read_NoWarnings_ForWellFormedAssembly()
    {
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        structure.Warnings.Should().BeEmpty();
    }
}
