namespace Suru.Tests;

// Verifies match statement block-body arms: early returns, empty arms, let bindings
// inside arm bodies, void match statements, and backward-compat single-expression arms.
[Collection("IntegrationIR")]
public class IRMatchStatementTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("match-statement");
    private bool _testPassed;

    [Fact]
    public void PrintsExpectedOutput()
    {
        Assert.Equal(
            "zero\none\nmany\nnegative\nnon-negative\nyes\nno\nbackward-compat\n",
            Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("match-statement"); }
}
