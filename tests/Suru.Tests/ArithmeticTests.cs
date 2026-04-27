namespace Suru.Tests;

[Collection("Integration")]
public class ArithmeticTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("arithmetic");
    private bool _testPassed;

    [Fact]
    public void ArithmeticAndVariables_PrintsExpectedOutput()
    {
        Assert.Equal("5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("arithmetic"); }
}
