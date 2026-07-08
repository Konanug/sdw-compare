using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Assembly;
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

        #600=CARTESIAN_POINT('',(0.,0.,0.));
        #601=DIRECTION('',(0.,0.,1.));
        #602=DIRECTION('',(1.,0.,0.));
        #603=DIRECTION('',(0.,1.,0.));
        #604=AXIS2_PLACEMENT_3D('',#600,#601,#602);

        #610=CARTESIAN_POINT('',(100.,0.,0.));
        #611=AXIS2_PLACEMENT_3D('',#610,#601,#602);
        #612=ITEM_DEFINED_TRANSFORMATION('','',#604,#611);
        #613=REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#612);
        #614=PRODUCT_DEFINITION_SHAPE('',$,#500);
        #615=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#613,#614);

        #620=CARTESIAN_POINT('',(0.,0.,0.));
        #621=AXIS2_PLACEMENT_3D('',#620,#601,#603);
        #622=ITEM_DEFINED_TRANSFORMATION('','',#604,#621);
        #623=REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#622);
        #624=PRODUCT_DEFINITION_SHAPE('',$,#501);
        #625=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#623,#624);

        #630=CARTESIAN_POINT('',(200.,0.,0.));
        #631=AXIS2_PLACEMENT_3D('',#630,#601,#602);
        #632=ITEM_DEFINED_TRANSFORMATION('','',#604,#631);
        #633=REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#632);
        #634=PRODUCT_DEFINITION_SHAPE('',$,#502);
        #635=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#633,#634);

        #640=CARTESIAN_POINT('',(10.,0.,0.));
        #641=AXIS2_PLACEMENT_3D('',#640,#601,#602);
        #642=ITEM_DEFINED_TRANSFORMATION('','',#604,#641);
        #643=REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#642);
        #644=PRODUCT_DEFINITION_SHAPE('',$,#503);
        #645=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#643,#644);

        #650=CARTESIAN_POINT('',(0.,5.,0.));
        #651=AXIS2_PLACEMENT_3D('',#650,#601,#602);
        #652=ITEM_DEFINED_TRANSFORMATION('','',#604,#651);
        #653=REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#652);
        #654=PRODUCT_DEFINITION_SHAPE('',$,#504);
        #655=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#653,#654);

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

    // ── Occurrence position resolution ──────────────────────────────────────────────────────

    private static bool ContainsPosition(IReadOnlyList<double[]> positions, double x, double y, double z)
        => positions.Any(p => Math.Abs(p[0] - x) < 1e-9 && Math.Abs(p[1] - y) < 1e-9 && Math.Abs(p[2] - z) < 1e-9);

    [Fact]
    public void Read_ResolvesGlobalPosition_ForEveryOccurrence_IncludingMultiInstance()
    {
        // PART-A has THREE occurrences (1 direct under ROOT + 1 per SUBASM instance ×2). The old
        // code resolved a placement only for single-instance products, so this multi-instance
        // part got nothing — this is the core "misses most instances" bug being fixed. Positions
        // are in metres (fixture points are mm, scaled ×1e-3):
        //   • ROOT→PART-A (#502, +200mm X)                     → (0.2,  0,    0)
        //   • ROOT→SUBASM#1 (+100mm X) → PART-A (#503, +10mm X) → (0.11, 0,    0)
        //   • ROOT→SUBASM#2 (rot 90° Z) → PART-A (#503, +10mm X)→ (0,    0.01, 0)   ← rotation-composed
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        var partA = structure.Components.Single(c => c.MatchKey == "PART-A");
        partA.OccurrencePositionsM.Should().HaveCount(3);
        ContainsPosition(partA.OccurrencePositionsM, 0.2, 0.0, 0.0).Should().BeTrue();
        ContainsPosition(partA.OccurrencePositionsM, 0.11, 0.0, 0.0).Should().BeTrue();

        // The third occurrence sits under a SUBASM instance rotated 90° about Z, so PART-A's
        // local +10mm X offset composes to +10mm along the ROOT Y axis → (0, 0.01, 0). A
        // translation-only composition (ignoring the parent rotation) would instead place it at
        // (0.01, 0, 0) — so asserting the rotated value present AND the un-rotated value absent
        // pins down that rotation-aware composition is actually happening.
        ContainsPosition(partA.OccurrencePositionsM, 0.0, 0.01, 0.0).Should().BeTrue();
        ContainsPosition(partA.OccurrencePositionsM, 0.01, 0.0, 0.0).Should().BeFalse();
    }

    [Fact]
    public void Read_OccurrenceCount_MatchesInstanceCount()
    {
        var reader = StepP21Reader.ParseText(SyntheticAssembly);
        var structure = new StepAssemblyStructureReader(reader).Read();

        foreach (var c in structure.Components)
            c.OccurrencePositionsM.Should().HaveCount(c.InstanceCount!.Value,
                $"every counted instance of '{c.MatchKey}' should get exactly one resolved position");
    }

    [Fact]
    public void Read_NestedSubAssemblyShift_ChangesGlobalPosition_WhereParentRelativeWouldNot()
    {
        // Between two revisions, only SUBASM instance #2's own placement moves (its target point
        // #620 shifts +50mm in Z). The SUBASM→PART-A hop (#503) is byte-identical in both — so a
        // comparison that only looked at the child's own immediate (parent-relative) transform
        // would see no change. But PART-A's occurrence under that SUBASM instance moves in the
        // global frame, which the composed position captures.
        var fixtureB = SyntheticAssembly.Replace(
            "#620=CARTESIAN_POINT('',(0.,0.,0.));",
            "#620=CARTESIAN_POINT('',(0.,0.,50.));");

        var a = new StepAssemblyStructureReader(StepP21Reader.ParseText(SyntheticAssembly)).Read()
            .Components.Single(c => c.MatchKey == "PART-A");
        var b = new StepAssemblyStructureReader(StepP21Reader.ParseText(fixtureB)).Read()
            .Components.Single(c => c.MatchKey == "PART-A");

        // Global set differs (the rotation-composed occurrence moved from z=0 to z=0.05)...
        ContainsPosition(a.OccurrencePositionsM, 0.0, 0.01, 0.0).Should().BeTrue();
        ContainsPosition(b.OccurrencePositionsM, 0.0, 0.01, 0.05).Should().BeTrue();
        // ...and the position comparer flags it as moved.
        OccurrencePositionComparer.PositionChanged(a.OccurrencePositionsM, b.OccurrencePositionsM, 0.0005)
            .Should().BeTrue();
    }

    [Fact]
    public void Read_UnresolvableTransformChain_DegradesToIdentity_WithWarning_NeverDropsOccurrence()
    {
        // Break the ROOT→PART-A direct hop by removing its CONTEXT_DEPENDENT_SHAPE_REPRESENTATION.
        var broken = SyntheticAssembly.Replace(
            "#635=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#633,#634);", "");

        var structure = new StepAssemblyStructureReader(StepP21Reader.ParseText(broken)).Read();
        var partA = structure.Components.Single(c => c.MatchKey == "PART-A");

        // The occurrence is not dropped — still 3 — but the unresolved hop falls back to identity,
        // so PART-A's direct occurrence now sits at the origin instead of (0.2, 0, 0).
        partA.OccurrencePositionsM.Should().HaveCount(3);
        ContainsPosition(partA.OccurrencePositionsM, 0.0, 0.0, 0.0).Should().BeTrue();
        ContainsPosition(partA.OccurrencePositionsM, 0.2, 0.0, 0.0).Should().BeFalse();
        structure.Warnings.Should().Contain(w => w.Contains("could not be resolved"));
    }
}
