using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class FunctionTests
{
    private readonly string _exe;

    public FunctionTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("fibonacci");

    [Fact]
    public void Fibonacci_PrintsExpectedOutput()
    {
        var stdout = Run(_exe);
        Assert.Equal("0\n1\n5\n55\n", stdout);
    }

    // TODO refactor this - move to abstract class
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
