using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class ArithmeticTests
{
    private readonly string _exe;

    public ArithmeticTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("arithmetic");

    [Fact]
    public void ArithmeticAndVariables_PrintsExpectedOutput()
    {
        var stdout = Run(_exe);
        Assert.Equal("5\n6\n6\n3\n-5\ntrue\nfalse\n", stdout);
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
