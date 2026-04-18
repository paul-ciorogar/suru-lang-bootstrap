namespace Suru.Tests;

[Collection("Integration")]
public class PrintErrorTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("print-error");

    [Fact]
    public void PrintError_WritesToStderr()
    {
        var (stdout, stderr) = RunGetStreams(_exe);
        Assert.Equal("error: something went wrong\n", stderr);
    }

    [Fact]
    public void PrintError_DoesNotWriteToStdout()
    {
        var (stdout, _) = RunGetStreams(_exe);
        Assert.Equal("stdout line\n", stdout);
    }
}
