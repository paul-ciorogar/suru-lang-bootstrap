namespace Suru.Tests;

[Collection("Integration")]
public class WhileLoopTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("while-loop");
    private bool _testPassed;

    [Fact]
    public void WhileLoop_PrintsExpectedOutput()
    {
        Assert.Equal("1\n2\n3\n4\n5\n55\nxxx\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("while-loop"); }
}
