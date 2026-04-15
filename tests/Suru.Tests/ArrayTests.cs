using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class ArrayTests
{
    private readonly string _exe;

    public ArrayTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("arrays");

    [Fact]
    public void Array_LenAtSetAddSlice()
    {
        var stdout = Run(_exe);
        Assert.Equal("3\n10\n30\n99\n4\n40\n2\n99\n", stdout);
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
