namespace Suru.Tests;

[Collection("Integration")]
public class ArrayTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("arrays");
    private bool _testPassed;

    [Fact]
    public void Array_LenAtSetAddSlice()
    {
        Assert.Equal("3\n10\n30\n99\n4\n40\n2\n99\n4\n10\n777\n10\n2\nhello\n4\n1\n4\n1 one\n5\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("arrays"); }
}
