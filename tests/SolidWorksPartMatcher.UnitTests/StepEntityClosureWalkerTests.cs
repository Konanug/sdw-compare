using FluentAssertions;
using SolidWorksPartMatcher.Infrastructure.Step;
using SolidWorksPartMatcher.Infrastructure.Step.Assembly;
using Xunit;

namespace SolidWorksPartMatcher.UnitTests;

public sealed class StepEntityClosureWalkerTests
{
    private const string SyntheticP21 = """
        ISO-10303-21;
        HEADER;
        FILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));
        ENDSEC;
        DATA;
        #1=FOO('a',#2,#3);
        #2=BAR('b');
        #3=BAZ('c',#4);
        #4=QUX('d');
        #10=UNRELATED('z');
        ENDSEC;
        END-ISO-10303-21;
        """;

    [Fact]
    public void ComputeClosure_FollowsTransitiveReferences_ButStopsAtUnrelatedEntities()
    {
        var reader = StepP21Reader.ParseText(SyntheticP21);

        var closure = StepEntityClosureWalker.ComputeClosure(reader, startId: 1);

        closure.Should().BeEquivalentTo([1, 2, 3, 4]);
        closure.Should().NotContain(10);
    }

    [Fact]
    public void ComputeClosure_SingleIsolatedEntity_ReturnsJustItself()
    {
        var reader = StepP21Reader.ParseText(SyntheticP21);

        var closure = StepEntityClosureWalker.ComputeClosure(reader, startId: 10);

        closure.Should().BeEquivalentTo([10]);
    }
}
