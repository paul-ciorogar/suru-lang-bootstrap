using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 15e — Semantic Analysis Integration in Driver.
//
// Verifies that suru-codegen-driver-full runs semantic analysis before codegen:
//   1. ValidProgramStillCompiles — pre-existing valid programs still compile and
//      produce correct output after the semantic gating step is inserted.
//   2. InvalidProgramExitsWithCode1 — a program with a semantic error causes the
//      driver to exit with code 1 without writing a .ll file.
//
// Strategy:
//   1. Compile suru-codegen-driver-full to a native binary.
//   2a. (Valid) Run driver on while-loop/main.suru → link with Suru runtime → assert output.
//   2b. (Invalid) Run driver on suru-semantic-reject/main.suru → assert exit code 1 + no .ll.

[Collection("IntegrationIR")]
public class IRSuruStage15eTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _driverExe = fixtures.GetExecutable("suru-codegen-driver-full");
    private bool _testPassed;

    private static string? FindClang()
    {
        foreach (var name in new[] { "clang", "clang-20", "clang-19", "clang-18", "clang-17", "clang-16", "clang-15" })
        {
            using var which = Process.Start(new ProcessStartInfo
            {
                FileName = "which", Arguments = name,
                RedirectStandardOutput = true, UseShellExecute = false,
            })!;
            which.WaitForExit();
            if (which.ExitCode == 0) return name;
        }
        return null;
    }

    private static string? CompileAndLink(IEnumerable<string> llPaths, string exePath)
    {
        var clang = FindClang();
        if (clang is null) return "clang not found";

        var args = string.Join(" ", llPaths.Select(p => $"\"{p}\"")) + $" -o \"{exePath}\"";
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = clang, Arguments = args,
            RedirectStandardError = true, UseShellExecute = false,
        })!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0 ? null : stderr.Trim();
    }

    private static string FindFixturePath(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", name, "main.suru");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException($"Fixture '{name}' not found");
    }

    // Compile the driver output for a valid fixture and return its stdout.
    private string RunValidFixture(string fixtureName, string llFileName)
    {
        var fixtureSuru = FindFixturePath(fixtureName);
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var userLlPath = Path.Combine(tempDir, llFileName);
            var exePath    = Path.Combine(tempDir, "out");

            var emitExitCode = RunGetExitCode(_driverExe, fixtureSuru, userLlPath);
            Assert.Equal(0, emitExitCode);
            Assert.True(File.Exists(userLlPath), "driver did not write the .ll file");

            var runtimes = new[]
            {
                ("suru_box",     SuruRuntime.GenerateBoxRuntime()),
                ("suru_string",  SuruRuntime.GenerateStringRuntime()),
                ("suru_array",   SuruRuntime.GenerateArrayRuntime()),
                ("suru_struct",  SuruRuntime.GenerateStructRuntime()),
                ("suru_variant", SuruRuntime.GenerateVariantRuntime()),
            };
            var runtimeLlPaths = new List<string>();
            foreach (var (rtName, rtIr) in runtimes)
            {
                var rtPath = Path.Combine(tempDir, rtName + ".ll");
                File.WriteAllText(rtPath, rtIr);
                runtimeLlPaths.Add(rtPath);
            }

            var allLlPaths = new[] { userLlPath }.Concat(runtimeLlPaths);
            var clangError = CompileAndLink(allLlPaths, exePath);
            Assert.True(clangError is null,
                $"clang failed to compile Suru-generated IR for {fixtureName}:\n{clangError}");

            return Run(exePath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // Valid program regression: semantic analysis must not block correct programs.
    [Fact]
    public void ValidProgramStillCompiles()
    {
        var output = RunValidFixture("while-loop", "while-loop.ll");
        Assert.Equal("1\n2\n3\n4\n5\n55\nxxx\n", output);
        _testPassed = true;
    }

    // Semantic rejection: driver must exit 1 and not write a .ll file.
    [Fact]
    public void InvalidProgramExitsWithCode1()
    {
        var fixtureSuru = FindFixturePath("suru-semantic-reject");
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15e-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dummyLl = Path.Combine(tempDir, "reject.ll");
            var exitCode = RunGetExitCode(_driverExe, fixtureSuru, dummyLl);
            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(dummyLl), "driver must not write .ll when semantic errors exist");
            _testPassed = true;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("suru-codegen-driver-full");
    }
}
