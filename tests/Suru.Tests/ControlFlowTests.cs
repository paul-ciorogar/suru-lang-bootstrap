namespace Suru.Tests;

[Collection("Integration")]
public class ControlFlowTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("control-flow");
    private bool _testPassed;

    [Fact]
    public void ControlFlow_PrintsExpectedOutput()
    {
        Assert.Equal("1\n1\ntrue\ntrue\nfalse\n1\n1\n-1\nFriday\nFriday\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("control-flow"); }
}
