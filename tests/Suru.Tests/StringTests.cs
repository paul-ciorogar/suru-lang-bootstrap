namespace Suru.Tests;

[Collection("Integration")]
public class StringTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("strings");
    private bool _testPassed;

    [Fact]
    public void String_LenEqualsAppendSliceAtFromToString()
    {
        Assert.Equal("5\ntrue\nfalse\nhello world\nel\n42\n42\nh\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("strings"); }
}
