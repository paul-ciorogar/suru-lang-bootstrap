using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 15c + 15d — Variant Creation, Field Access & Match Dispatch (Suru-in-Suru).
//
// Cross-validates the Suru-in-Suru heap codegen against the C# codegen for
// tests/fixtures/sum-types/main.suru, which exercises:
//   15c: let c Circle: { radius: 2283 }  (variant creation — type_tag=7 + variant_idx written in-place)
//   15c: c.radius  (field access on a variant via @suru_variant_inner identity call)
//   15d: match c { Circle: ... Square: ... }  (variant match dispatch via @suru_variant_tag)
//
// Strategy (matches Stage 14e):
//   1. Run the pre-compiled suru-codegen-driver-heap on sum-types/main.suru → .ll file.
//   2. Write the five Suru runtime modules to a temp dir.
//   3. Compile all .ll files together with clang.
//   4. Run the binary and assert stdout == "2283\ncircle\nsquare\n".

[Collection("IntegrationIR")]
public class IRSuruStage15cTests(CompiledFixturesIR fixtures)
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

    private string RunFixture(string fixtureName, string llFileName)
    {
        var fixtureSuru = FindFixturePath(fixtureName);
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15c-{Guid.NewGuid():N}");
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
    public void VariantCreationAndFieldAccessAndMatchDispatch()
    {
        var output = RunFixture("sum-types", "sum-types.ll");
        Assert.Equal("2283\ncircle\nsquare\n", output);
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("suru-codegen-driver-heap");
    }
}
