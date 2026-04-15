using System.Diagnostics;

namespace Suru.Tests;

public abstract class IntegrationTestBase
{
    protected static string Run(string exe, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    protected static int RunGetExitCode(string exe, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
