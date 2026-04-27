namespace Suru.Tests;

[Collection("Integration")]
public class NegativeLiteralTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("negative-literals");
    private bool _testPassed;

    [Fact]
    public void PrintsExpectedOutput()
    {
        Assert.Equal("-5\n-2.5\n-8\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("negative-literals"); }
}
