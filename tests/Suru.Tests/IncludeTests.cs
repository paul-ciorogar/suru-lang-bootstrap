namespace Suru.Tests;

[Collection("Integration")]
public class IncludeTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("include-test");
    private bool _testPassed;

    [Fact]
    public void Include_CallsNamespacedFunctions()
    {
        Assert.Equal("42\nHello, Suru!\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("include-test"); }
}
