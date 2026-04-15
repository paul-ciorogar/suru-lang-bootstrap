using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class StructTests
{
    private readonly string _exe;

    public StructTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("structs");

    [Fact]
    public void Struct_FieldReadWriteCloneDrop()
    {
        var stdout = Run(_exe);
        Assert.Equal("true\n2283\nfalse\nfalse\n", stdout);
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
