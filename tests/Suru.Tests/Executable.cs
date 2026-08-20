using System.Diagnostics;

namespace Suru.Tests;

/// <summary>Runs a compiled fixture and captures what it printed.</summary>
internal static class Executable
{
    public static string Run(string exe)
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
