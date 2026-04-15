using System.Diagnostics;

namespace Suru.Tests;

[Collection("Integration")]
public class FileIoTests
{
    private readonly string _readExe;
    private readonly string _writeExe;
    private readonly string _exitExe;

    public FileIoTests(CompiledFixtures fixtures)
    {
        _readExe  = fixtures.GetExecutable("file_io");
        _writeExe = fixtures.GetExecutable("file_io_write");
        _exitExe  = fixtures.GetExecutable("exit_test");
    }

    [Fact]
    public void ReadFile_EchoesFileContent()
    {
        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, "hello from file");
            var stdout = Run(_readExe, tmpPath);
            Assert.Equal("hello from file\n", stdout);
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }

    [Fact]
    public void WriteFile_CopiesContent()
    {
        var inPath  = Path.GetTempFileName();
        var outPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(inPath, "suru stage 7");
            Run(_writeExe, inPath, outPath);
            Assert.Equal("suru stage 7", File.ReadAllText(outPath));
        }
        finally
        {
            File.Delete(inPath);
            File.Delete(outPath);
        }
    }

    [Fact]
    public void Exit_ReturnsCode()
    {
        var exitCode = RunGetExitCode(_exitExe);
        Assert.Equal(42, exitCode);
    }

    private static string Run(string exe, params string[] args)
    {
        var escaped = string.Join(" ", args.Select(a => $"\"{a}\""));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = escaped,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private static int RunGetExitCode(string exe)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
