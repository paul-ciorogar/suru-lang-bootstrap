using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class StringTests
{
    private readonly string _exe;

    public StringTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("strings");

    [Fact]
    public void String_LenEqualsAppendSliceFromToString()
    {
        var stdout = Run(_exe);
        Assert.Equal("5\ntrue\nfalse\nhello world\nel\n42\n42\n", stdout);
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
