using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class ControlFlowTests
{
    private readonly string _exe;

    public ControlFlowTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("control-flow");

    [Fact]
    public void ControlFlow_PrintsExpectedOutput()
    {
        var stdout = Run(_exe);
        Assert.Equal("1\n1\ntrue\ntrue\nfalse\n", stdout);
    }

    private static string Run(string exe)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }
}
