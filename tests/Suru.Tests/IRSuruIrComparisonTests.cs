using SuruCompiler = Suru.Compiler.Compiler; // alias needed: Compiler is also a namespace

namespace Suru.Tests;

// IR text cross-validation: suru-build (Suru-in-Suru backend) vs C# bootstrap backend.
//
// For every corpus fixture:
//   1. Run C# Compiler.GenerateIr() → reference IR text per source file.
//   2. Run suru-build on the same fixture → Suru IR text (one .ll per source file).
//   3. Assert each pair of IR strings are byte-for-byte identical.
//
// This is the strongest form of bootstrapping correctness: if the Suru codegen faithfully
// reimplements the C# codegen the emitted LLVM IR must be identical.
//
// Single-file fixtures: one .ll produced by each side; direct comparison.
//
// Multi-file fixtures: C# generates one .ll per source file (main + each IncludedSourcePath).
// suru-build mirrors this — it writes main.ll to the given output path and writes one
// additional .ll (in the same directory) per transitively included source file.
// Each pair is compared by stem name (e.g. main.ll vs main.ll, lib.ll vs lib.ll).

[Collection("IntegrationIR")]
public class IRSuruIrComparisonTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _suruBuild = fixtures.GetExecutable("suru-build");
    private bool _allPassed = true;

    // Single-file fixtures (no include directives), ordered by complexity.
    public static TheoryData<string, string> Corpus => new()
    {
        { "exit_test",         "exit_test.ll" },
        { "print",             "print.ll" },
        { "print-error",       "print-error.ll" },
        { "arithmetic",        "arithmetic.ll" },
        { "negative-literals", "negative-literals.ll" },
        { "debug-match",       "debug-match.ll" },
        { "comparisons",       "comparisons.ll" },
        { "control-flow",      "control-flow.ll" },
        { "fibonacci",         "fibonacci.ll" },
        { "while-loop",        "while-loop.ll" },
        { "string-clone-drop", "string-clone-drop.ll" },
        { "strings",           "strings.ll" },
        { "file_io",           "file_io.ll" },
        { "file_io_write",     "file_io_write.ll" },
        { "arrays",            "arrays.ll" },
        { "named-types",       "named-types.ll" },
        { "structs",           "structs.ll" },
        { "match-statement",   "match-statement.ll" },
        { "sum-types",         "sum-types.ll" },
    };

    // Multi-file fixtures (have include directives), ordered by complexity.
    // The test derives all .ll file names by parsing the module's IncludedSourcePaths.
    public static TheoryData<string> MultiFileCorpus =>
    [
        "include-test",
        "include-transitive",
        "include-types-test",
        "sum-type-context-test"
    ];

    // ── helpers ────────────────────────────────────────────────────────────────

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

    // C# reference IR for the given fixture's main.suru.
    private static string CsIr(string fixtureName)
    {
        var result = new SuruCompiler(FindFixturePath(fixtureName)).GenerateIr();
        Assert.True(result.Success,
            $"C# GenerateIr() failed for {fixtureName}:\n{string.Join("\n", result.Errors)}");
        return result.Require();
    }

    // C# reference IR for an arbitrary source path (used for included files).
    private static string CsIrForPath(string sourcePath)
    {
        var result = new SuruCompiler(sourcePath).GenerateIr();
        Assert.True(result.Success,
            $"C# GenerateIr() failed for '{sourcePath}':\n{string.Join("\n", result.Errors)}");
        return result.Require();
    }

    // Suru IR for a single-file fixture: run suru-build, return the single written .ll text.
    private string SuruIr(string fixtureName, string llFile, string tempDir)
    {
        var sourcePath = FindFixturePath(fixtureName);
        var llPath = Path.Combine(tempDir, llFile);

        var exitCode = RunGetExitCode(_suruBuild, sourcePath, llPath);
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(llPath),
            $"suru-build did not write {llFile} for {fixtureName}");

        return File.ReadAllText(llPath);
    }

    // ── single-file test ───────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Corpus))]
    public void SuruIrMatchesCsIr(string fixtureName, string llFile)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-ir-cmp-{fixtureName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var csIr   = CsIr(fixtureName);
            var suruIr = SuruIr(fixtureName, llFile, tempDir);

            Assert.Equal(csIr, suruIr);
        }
        catch
        {
            _allPassed = false;
            throw;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── multi-file test ────────────────────────────────────────────────────────

    // For multi-file fixtures: C# generates one .ll per source file (main + each
    // IncludedSourcePath).  suru-build mirrors this layout.  We compare each pair
    // by stem name.
    [Theory]
    [MemberData(nameof(MultiFileCorpus))]
    public void SuruIrMatchesCsIrMultiFile(string fixtureName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"suru-ir-cmp-multi-{fixtureName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mainPath = FindFixturePath(fixtureName);

            // Parse the main file to discover all transitively included source paths.
            var parseResult = new SuruCompiler(mainPath).ParseFile();
            Assert.True(parseResult.Success,
                $"C# ParseFile() failed for {fixtureName}:\n{string.Join("\n", parseResult.Errors)}");
            var module = parseResult.Value!;

            // Build the C# reference IR map: stem.ll → ir_text.
            var csIrMap = new Dictionary<string, string>();

            var mainStem = Path.GetFileNameWithoutExtension(mainPath) + ".ll";
            csIrMap[mainStem] = CsIr(fixtureName);

            foreach (var includedPath in module.IncludedSourcePaths)
            {
                var stem = Path.GetFileNameWithoutExtension(includedPath) + ".ll";
                csIrMap[stem] = CsIrForPath(includedPath);
            }

            // Run suru-build: writes main.ll + one .ll per included source to tempDir.
            var mainLlPath = Path.Combine(tempDir, mainStem);
            var exitCode = RunGetExitCode(_suruBuild, mainPath, mainLlPath);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(mainLlPath),
                $"suru-build did not write {mainStem} for {fixtureName}");

            // Compare each C#-generated file against its suru-build counterpart.
            foreach (var (llFile, csText) in csIrMap)
            {
                var suruLlPath = Path.Combine(tempDir, llFile);
                Assert.True(File.Exists(suruLlPath),
                    $"suru-build did not write '{llFile}' for {fixtureName}");
                var suruText = File.ReadAllText(suruLlPath);
                Assert.Equal(csText, suruText);
            }
        }
        catch
        {
            _allPassed = false;
            throw;
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    public void Dispose()
    {
        if (!_allPassed)
            fixtures.RecordFailure("suru-build");
    }
}
