using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 15a — Multi-Level Include Resolution.
//
// Verifies that suru-codegen-driver-full correctly handles transitive and
// diamond includes.  The fixture include-transitive/main.suru exercises both:
//   - Transitive: main → lib-a → lib-b  (resolveIncludesRec must recurse)
//   - Diamond:    main → lib-b (direct) + main → lib-a → lib-b  (must dedup)
//
// Strategy (same as Stage 14e full-pipeline tests):
//   1. Compile suru-codegen-driver-full to a native binary.
//   2. Run it on include-transitive/main.suru to emit a .ll file.
//   3. Write the five Suru runtime modules to a temp dir.
//   4. Compile all .ll files together with clang to a native binary.
//   5. Run the binary and assert stdout == "28\n9\n".

[Collection("IntegrationIR")]
public class IRSuruStage15aTests(CompiledFixturesIR fixtures)
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

    // Locate a fixture's main.suru by walking up from the test binary directory.
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15a-{Guid.NewGuid():N}");
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
    public void TransitiveAndDiamondIncludeCrossValidation()
    {
        // quadruple(7) = triple(7) + 7 = 28; triple(3) = 9
        // lib-b is reachable via main→lib-a→lib-b AND main→lib-b directly;
        // deduplication must prevent triple() from being emitted twice.
        var output = RunFixture("include-transitive", "include-transitive.ll");
        Assert.Equal("28\n9\n", output);
        _testPassed = true;
    }

    public void Dispose()
    {
        if (!_testPassed)
            fixtures.RecordFailure("suru-codegen-driver-full");
    }
}
