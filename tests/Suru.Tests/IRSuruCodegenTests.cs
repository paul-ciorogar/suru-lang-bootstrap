using System.Diagnostics;
using Suru.Compiler.Codegen;

namespace Suru.Tests;

// Stage 14a — Validates the Suru IR builder primitives by:
//   1. Compiling the suru-codegen fixture to a native binary.
//   2. Running it with a temp path to produce an LLVM IR (.ll) file.
//   3. Compiling that .ll with clang to a native executable.
//   4. Running the result and asserting the expected output.
//
// This ensures the IR text emitted by the Suru IR builder is valid LLVM IR
// that the system clang accepts and that the resulting binary behaves correctly.
[Collection("IntegrationIR")]
public class IRSuruCodegenTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("suru-codegen");
    private bool _testPassed;

    // ─── Helper: find and invoke clang ───────────────────────────────────────

    // Tries clang-15 through clang-20, then "clang", matching Compiler.cs logic.
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

    // Compile irPath to an executable at exePath; returns null on success or the
    // clang stderr on failure.
    private static string? CompileIrFile(string irPath, string exePath)
    {
        var clang = FindClang();
        if (clang is null) return "clang not found";

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = clang,
            Arguments = $"\"{irPath}\" -o \"{exePath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0 ? null : stderr.Trim();
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    // Stage 14a milestone: the Suru IR builder emits a valid "Hello World!" .ll
    // program that clang compiles successfully and the binary prints the expected
    // output.
    [Fact]
    public void HelloWorldEmitsValidLl()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-codegen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var llPath  = Path.Combine(tempDir, "hello.ll");
            var exePath = Path.Combine(tempDir, "hello");

            // Step 1: run the Suru IR emitter binary to produce hello.ll
            var emitExitCode = RunGetExitCode(_exe, llPath);
            Assert.Equal(0, emitExitCode);
            Assert.True(File.Exists(llPath), "suru-codegen did not write the .ll file");

            // Step 2: compile .ll to a native executable with clang
            var clangError = CompileIrFile(llPath, exePath);
            Assert.True(clangError is null, $"clang failed to compile emitted IR:\n{clangError}");

            // Step 3: run the compiled binary and check its output
            var output = Run(exePath);
            Assert.Equal("Hello World!\n", output);

            _testPassed = true;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // Verify the emitted .ll text contains the expected structural landmarks:
    // the global string constant, the printf declaration, and both function
    // definitions.  This catches regressions in the IR builder text format
    // without requiring a full clang compilation.
    [Fact]
    public void EmittedIrContainsExpectedStructure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-codegen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var llPath = Path.Combine(tempDir, "hello.ll");
            var emitExitCode = RunGetExitCode(_exe, llPath);
            Assert.Equal(0, emitExitCode);

            var ll = File.ReadAllText(llPath);
            Assert.Contains("@str0 = private unnamed_addr constant [14 x i8]", ll);
            Assert.Contains("Hello World!", ll);
            Assert.Contains("declare i32 @printf(ptr, ...)", ll);
            Assert.Contains("define i32 @suru_main()", ll);
            Assert.Contains("getelementptr [14 x i8]", ll);
            Assert.Contains("call i32 (ptr, ...) @printf", ll);
            Assert.Contains("define i32 @main(i32 %argc, ptr %argv)", ll);

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
            fixtures.RecordFailure("suru-codegen");
    }
}

// Stage 14b — Cross-validates the Suru-in-Suru scalar codegen against the C#
// codegen on arithmetic.suru (Int64/Bool/Int32 scalars, arithmetic methods,
// printLn, fn main).
//
// Strategy:
//   1. Compile the suru-codegen-driver fixture to a native binary.
//   2. Run it on arithmetic.suru to emit a .ll file.
//   3. Write the five Suru runtime modules (suru_box, suru_string, …) to a temp dir.
//   4. Compile all .ll files together with clang to a native binary.
//   5. Run the binary and assert its stdout matches the expected arithmetic output.
[Collection("IntegrationIR")]
public class IRSuruStage14bTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _driverExe = fixtures.GetExecutable("suru-codegen-driver");
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

    // Compile and link one or more .ll files into a single native executable.
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

    [Fact]
    public void ArithmeticCrossValidation()
    {
        var arithmeticSuru = FindFixturePath("arithmetic");
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-14b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var userLlPath = Path.Combine(tempDir, "arith.ll");
            var exePath    = Path.Combine(tempDir, "arith");

            // Step 1: run driver to emit arithmetic.suru as LLVM IR.
            var emitExitCode = RunGetExitCode(_driverExe, arithmeticSuru, userLlPath);
            Assert.Equal(0, emitExitCode);
            Assert.True(File.Exists(userLlPath), "driver did not write the .ll file");

            // Step 2: write the five Suru runtime modules to the temp dir.
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

            // Step 3: compile user IR + all runtime modules together.
            var allLlPaths = new[] { userLlPath }.Concat(runtimeLlPaths);
            var clangError = CompileAndLink(allLlPaths, exePath);
            Assert.True(clangError is null,
                $"clang failed to compile Suru-generated IR:\n{clangError}");

            // Step 4: run and assert output matches expected arithmetic results.
            var output = Run(exePath);
            Assert.Equal("5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n", output);

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
            fixtures.RecordFailure("suru-codegen-driver");
    }
}

// Stage 14c — Cross-validates the Suru-in-Suru control-flow codegen against the
// C# codegen on fibonacci.suru (match expression + recursion, pure integers).
//
// Strategy:
//   1. Compile the suru-codegen-driver fixture to a native binary.
//   2. Run it on fibonacci.suru to emit a .ll file.
//   3. Write the five Suru runtime modules to a temp dir.
//   4. Compile all .ll files together with clang to a native binary.
//   5. Run the binary and assert its stdout matches the expected fibonacci output.
[Collection("IntegrationIR")]
public class IRSuruStage14cTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _driverExe = fixtures.GetExecutable("suru-codegen-driver");
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

    [Fact]
    public void FibonacciCrossValidation()
    {
        var fibSuru = FindFixturePath("fibonacci");
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-14c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var userLlPath = Path.Combine(tempDir, "fib.ll");
            var exePath    = Path.Combine(tempDir, "fib");

            // Step 1: run driver to emit fibonacci.suru as LLVM IR.
            var emitExitCode = RunGetExitCode(_driverExe, fibSuru, userLlPath);
            Assert.Equal(0, emitExitCode);
            Assert.True(File.Exists(userLlPath), "driver did not write the .ll file");

            // Step 2: write the five Suru runtime modules to the temp dir.
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

            // Step 3: compile user IR + all runtime modules together.
            var allLlPaths = new[] { userLlPath }.Concat(runtimeLlPaths);
            var clangError = CompileAndLink(allLlPaths, exePath);
            Assert.True(clangError is null,
                $"clang failed to compile Suru-generated IR:\n{clangError}");

            // Step 4: run and assert output matches expected fibonacci results.
            var output = Run(exePath);
            Assert.Equal("0\n1\n5\n55\n", output);

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
            fixtures.RecordFailure("suru-codegen-driver");
    }
}
