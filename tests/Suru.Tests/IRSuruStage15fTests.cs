using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 15f — Complete suru build Driver CLI.
//
// Verifies that tests/fixtures/suru-build/main.suru (the canonical Suru compiler
// source) correctly compiles arithmetic.suru end-to-end:
//
//   1. Compile suru-build/main.suru with the C# bootstrap compiler → driver binary.
//   2. Run the driver binary on arithmetic/main.suru → arithmetic.ll.
//   3. Link arithmetic.ll with the five Suru runtime modules using clang.
//   4. Run the resulting native binary and assert correct output.
//
// This fixture is the entry point for Stages 15g and 15h (bootstrapping), so its
// correctness is a prerequisite for self-hosting validation.

[Collection("IntegrationIR")]
public class IRSuruStage15fTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _driverExe = fixtures.GetExecutable("suru-build");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15f-{Guid.NewGuid():N}");
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

    // Core milestone: suru-build correctly compiles arithmetic.suru end-to-end.
    [Fact]
    public void ArithmeticCompilesToCorrectOutput()
    {
        var output = RunValidFixture("arithmetic", "arithmetic.ll");
        Assert.Equal("5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n", output);
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("suru-build");
    }
}
