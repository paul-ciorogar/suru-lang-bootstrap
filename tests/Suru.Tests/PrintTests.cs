using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class PrintTests
{
    private readonly string _exe;

    public PrintTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("print");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Run(_exe);
        Assert.Equal("true\nfalse\n1\n1.2\n", stdout);
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
