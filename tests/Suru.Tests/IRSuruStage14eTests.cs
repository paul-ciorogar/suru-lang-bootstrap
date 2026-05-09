using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 14e — Full Pipeline & Cross-Validation.
//
// Cross-validates the Suru-in-Suru heap codegen against the C# codegen on six
// additional corpus programs:
//   Simple (no includes): while-loop, comparisons, negative-literals, print, exit_test
//   With includes:        include-test (via suru-codegen-driver-full)
//
// Strategy (matches Stage 14d):
//   1. Compile the relevant Suru driver fixture to a native binary.
//   2. Run it on the target fixture to emit a .ll file.
//   3. Write the five Suru runtime modules to a temp dir.
//   4. Compile all .ll files together with clang to a native binary.
//   5. Run the binary and assert its stdout (or exit code) matches the expected output.

// ─── Simple programs — use the existing heap driver ───────────────────────────

[Collection("IntegrationIR")]
public class IRSuruStage14eSimpleTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _driverExe = fixtures.GetExecutable("suru-codegen-driver-heap");
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

    private (string stdout, int exitCode) RunFixture(string fixtureName, string llFileName)
    {
        var fixtureSuru = FindFixturePath(fixtureName);
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-14e-{Guid.NewGuid():N}");
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

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true, UseShellExecute = false,
            })!;
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (stdout, proc.ExitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void WhileLoopCrossValidation()
    {
        var (output, _) = RunFixture("while-loop", "while-loop.ll");
        Assert.Equal("1\n2\n3\n4\n5\n55\nxxx\n", output);
        _testPassed = true;
    }

    [Fact]
    public void ComparisonsCrossValidation()
    {
        var (output, _) = RunFixture("comparisons", "comparisons.ll");
        Assert.Equal("true\nfalse\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\nfalse\n65\n48\n", output);
        _testPassed = true;
    }

    [Fact]
    public void NegativeLiteralsCrossValidation()
    {
        var (output, _) = RunFixture("negative-literals", "negative-literals.ll");
        Assert.Equal("-5\n-2.5\n-8\n", output);
        _testPassed = true;
    }

    [Fact]
    public void PrintCrossValidation()
    {
        var (output, _) = RunFixture("print", "print.ll");
        Assert.Equal("true\nfalse\n1\n1.2\n", output);
        _testPassed = true;
    }

    [Fact]
    public void ExitTestCrossValidation()
    {
        var (output, exitCode) = RunFixture("exit_test", "exit_test.ll");
        Assert.Equal(42, exitCode);
        Assert.Equal("", output);
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("suru-codegen-driver-heap");
    }
}

// ─── Include-test — use the full pipeline driver ──────────────────────────────

[Collection("IntegrationIR")]
public class IRSuruStage14eFullTests(CompiledFixturesIR fixtures)
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

    private string RunFixture(string fixtureName, string llFileName)
    {
        var fixtureSuru = FindFixturePath(fixtureName);
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-14e-full-{Guid.NewGuid():N}");
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

    [Fact]
    public void IncludeTestCrossValidation()
    {
        var output = RunFixture("include-test", "include-test.ll");
        Assert.Equal("42\nHello, Suru!\n", output);
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("suru-codegen-driver-full");
    }
}
