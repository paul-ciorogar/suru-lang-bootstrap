namespace Suru.Tests;

// Verifies the IR backend's comparison method calls:
//   lt / gt / lte / gte (relational) and ord() (ASCII value of first string byte).
// Integer comparisons use icmp with signed predicates; float comparisons use fcmp
// with ordered predicates. All comparisons produce a Bool (i1) result.
[Collection("IntegrationIR")]
public class IRComparisonsTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("comparisons");
    private bool _testPassed;

    [Fact]
    public void PrintsExpectedOutput()
    {
        Assert.Equal("true\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\nfalse\n65\n48\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("comparisons"); }
}
