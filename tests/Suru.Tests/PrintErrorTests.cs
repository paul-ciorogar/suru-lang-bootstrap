namespace Suru.Tests;

[Collection("Integration")]
public class PrintErrorTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("print-error");
    private bool _testPassed;

    [Fact]
    public void PrintError_WritesToStderr()
    {
        var (stdout, stderr) = RunGetStreams(_exe);
        Assert.Equal("error: something went wrong\n", stderr);
        _testPassed = true;
    }

    [Fact]
    public void PrintError_DoesNotWriteToStdout()
    {
        var (stdout, _) = RunGetStreams(_exe);
        Assert.Equal("stdout line\n", stdout);
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("print-error"); }
}
