using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 15h — Bootstrap Round 2 & Self-Hosting Validation.
//
// Verifies two-round bootstrap:
//
//   Round 1 (15g): C# compiler → binary A (suru-build)
//   Round 2 (15h): binary A compiles suru-build/main.suru → binary B
//
// Self-hosting is confirmed when binary A and binary B produce byte-for-byte
// identical output on all corpus programs: the Suru compiler can reproduce itself.

[Collection("IntegrationIR")]
public class IRSuruStage15hTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase
{
    // Binary A: Suru compiler compiled by the C# bootstrap (cached by the collection fixture).
    private readonly string _binaryA = fixtures.GetExecutable("suru-build");

    // Corpus programs cross-validated in Stage 15g; both binaries must agree on all of them.
    private static readonly (string Name, string LlFile, string Expected)[] Corpus =
    [
        ("arithmetic", "arithmetic.ll", "5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n"),
        ("fibonacci",  "fibonacci.ll",  "0\n1\n5\n55\n"),
        ("while-loop", "while-loop.ll", "1\n2\n3\n4\n5\n55\nxxx\n"),
        ("strings",    "strings.ll",    "5\ntrue\nfalse\nhello world\nel\n42\n42\nh\n"),
    ];

    // ── helpers ────────────────────────────────────────────────────────────────

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

    // Write the five Suru runtime .ll files into tempDir and return their paths.
    private static List<string> WriteRuntimes(string tempDir)
    {
        var runtimes = new[]
        {
            ("suru_box",     SuruRuntime.GenerateBoxRuntime()),
            ("suru_string",  SuruRuntime.GenerateStringRuntime()),
            ("suru_array",   SuruRuntime.GenerateArrayRuntime()),
            ("suru_struct",  SuruRuntime.GenerateStructRuntime()),
            ("suru_variant", SuruRuntime.GenerateVariantRuntime()),
        };
        var paths = new List<string>();
        foreach (var (rtName, rtIr) in runtimes)
        {
            var rtPath = Path.Combine(tempDir, rtName + ".ll");
            File.WriteAllText(rtPath, rtIr);
            paths.Add(rtPath);
        }
        return paths;
    }

    // Use driverExe to compile a corpus fixture and return the native binary's stdout.
    private string RunFixtureWithDriver(string driverExe, string fixtureName, string llFile, string tempDir)
    {
        var fixtureSuru = FindFixturePath(fixtureName);
        var userLlPath  = Path.Combine(tempDir, llFile);
        var exePath     = Path.Combine(tempDir, "out-" + fixtureName);

        var emitExitCode = RunGetExitCode(driverExe, fixtureSuru, userLlPath);
        Assert.Equal(0, emitExitCode);
        Assert.True(File.Exists(userLlPath), $"driver did not write {llFile} for {fixtureName}");

        var runtimeLlPaths = WriteRuntimes(tempDir);
        var allLlPaths = new[] { userLlPath }.Concat(runtimeLlPaths);
        var clangError = CompileAndLink(allLlPaths, exePath);
        Assert.True(clangError is null,
            $"clang failed for {fixtureName}:\n{clangError}");

        return Run(exePath);
    }

    // Use binary A to compile the Suru compiler source → binary B; return its executable path.
    // The caller owns the tempDir lifetime.
    private string CompileBinaryB(string tempDir)
    {
        var suruBuildSuru = FindFixturePath("suru-build");
        var binaryBLlPath = Path.Combine(tempDir, "suru-build-b.ll");
        var binaryBExe    = Path.Combine(tempDir, "suru-build-b");

        // Write runtimes first so we can distinguish them from module .ll files.
        var runtimeLlPaths = WriteRuntimes(tempDir);
        var runtimeNames = new HashSet<string>(runtimeLlPaths.Select(Path.GetFileName)!);

        // Binary A compiles the Suru compiler source → per-module .ll files
        // (main module + one .ll per included source file), mirroring C# CompileIR.
        var emitExitCode = RunGetExitCode(_binaryA, suruBuildSuru, binaryBLlPath);
        Assert.Equal(0, emitExitCode);
        Assert.True(File.Exists(binaryBLlPath), "binary A did not write suru-build-b.ll");

        // Collect all module .ll files written by binary A (everything in tempDir
        // that isn't a runtime file), then link them all together with the runtimes.
        var moduleLlPaths = Directory.GetFiles(tempDir, "*.ll")
            .Where(p => !runtimeNames.Contains(Path.GetFileName(p)))
            .OrderBy(p => p);
        var allLlPaths = moduleLlPaths.Concat(runtimeLlPaths);
        var clangError = CompileAndLink(allLlPaths, binaryBExe);
        Assert.True(clangError is null,
            $"clang failed to link binary B:\n{clangError}");

        return binaryBExe;
    }

    // ── tests ──────────────────────────────────────────────────────────────────

    // Round-2 milestone: binary A successfully compiles the Suru compiler source → binary B.
    [Fact]
    public void BinaryBCompilesFromBinaryA()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15h-compile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var binaryB = CompileBinaryB(tempDir);
            Assert.True(File.Exists(binaryB), "binary B executable was not produced");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // Self-hosting milestone: binary A and binary B produce byte-for-byte identical output
    // on every corpus program.  Identical output means the Suru compiler reproduces itself.
    [Fact]
    public void BinaryAAndBinaryBProduceIdenticalOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-15h-selfhost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var binaryB = CompileBinaryB(tempDir);

            foreach (var (name, llFile, expected) in Corpus)
            {
                var dirA = Path.Combine(tempDir, "a", name);
                var dirB = Path.Combine(tempDir, "b", name);
                Directory.CreateDirectory(dirA);
                Directory.CreateDirectory(dirB);

                var outputA = RunFixtureWithDriver(_binaryA, name, llFile, dirA);
                var outputB = RunFixtureWithDriver(binaryB,  name, llFile, dirB);

                Assert.Equal(expected, outputA);
                Assert.Equal(outputA,  outputB);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
