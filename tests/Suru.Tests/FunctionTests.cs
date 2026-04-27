namespace Suru.Tests;

[Collection("Integration")]
public class FunctionTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("fibonacci");
    private bool _testPassed;

    [Fact]
    public void Fibonacci_PrintsExpectedOutput()
    {
        Assert.Equal("0\n1\n5\n55\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("fibonacci"); }
}
