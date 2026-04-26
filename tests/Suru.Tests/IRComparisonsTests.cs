namespace Suru.Tests;

// Verifies the IR backend's comparison method calls:
//   lt / gt / lte / gte (relational) and ord() (ASCII value of first string byte).
// Integer comparisons use icmp with signed predicates; float comparisons use fcmp
// with ordered predicates. All comparisons produce a Bool (i1) result.
[Collection("IntegrationIR")]
public class IRComparisonsTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("comparisons");

    [Fact]
    public void PrintsExpectedOutput()
        => Assert.Equal("true\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\nfalse\n65\n48\n", Run(_exe));
}
