using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests;

// Stage 13f — Cross-validates the Suru-implemented semantic analyzer (suru-check)
// against the C# SemanticAnalyzer on a shared corpus of valid and invalid programs.
//
// Strategy:
//   For each corpus file we run two semantic checks independently:
//     C# side  — new SuruCompiler(path).GenerateIr() includes semantic analysis
//     Suru side — run the compiled suru-check binary with the file path
//   and assert both agree on whether the program is valid (0 errors) or invalid
//   (≥1 error).  For invalid programs we also verify the Suru binary prints at
//   least one line containing the expected error keyword and exits with code 1.
//
// Corpus files live in tests/fixtures/suru-check/corpus/ and are all include-free
// programs, which avoids false positives from the Suru analyzer's lack of include
// resolution.
//
// Milestone tests (CrossValidation_*_NoErrors for existing fixtures) verify that
// the Suru analyzer produces zero false positives on valid programs that the C#
// compiler already accepts.
[Collection("IntegrationIR")]
public class IRSuruSemanticCrossValidationTests(CompiledFixturesIR fixtures)
    : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("suru-check");
    private bool _testPassed;

    // ─── Path helpers ─────────────────────────────────────────────────────────

    private static string CorpusPath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "suru-check", "corpus", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Corpus file '{fileName}' not found");
    }

    private static string FixturePath(string name)
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

    // ─── C# reference helpers ─────────────────────────────────────────────────

    private static bool CsSucceeds(string path)
        => new SuruCompiler(path).GenerateIr().Success;

    private static IReadOnlyList<string> CsErrors(string path)
    {
        var result = new SuruCompiler(path).GenerateIr();
        return result.Success ? [] : result.Errors;
    }

    // ─── Valid corpus programs ─────────────────────────────────────────────────

    // Strips the "path: " prefix from a semantic error line, returning just the message body.
    private static string ErrorBody(string errorLine)
        => errorLine.Substring(errorLine.IndexOf(": ") + 2);

    // ─── Valid corpus programs ─────────────────────────────────────────────────

    [Fact]
    public void CrossValidation_ValidHello_NoErrors()
    {
        var path = CorpusPath("valid_hello.suru");
        Assert.True(CsSucceeds(path), "C# unexpectedly reported errors for a valid program");

        var output   = Run(_exe, path).Trim();
        var exitCode = RunGetExitCode(_exe, path);

        Assert.Equal(0, exitCode);
        Assert.Equal("", output);
        _testPassed = true;
    }

    [Fact]
    public void CrossValidation_ValidFunctions_NoErrors()
    {
        var path = CorpusPath("valid_functions.suru");
        Assert.True(CsSucceeds(path), "C# unexpectedly reported errors for a valid program");

        var output   = Run(_exe, path).Trim();
        var exitCode = RunGetExitCode(_exe, path);

        Assert.Equal(0, exitCode);
        Assert.Equal("", output);
        _testPassed = true;
    }

    // ─── Invalid corpus programs ───────────────────────────────────────────────

    [Fact]
    public void CrossValidation_InvalidUndefVar_ReportsError()
    {
        var path     = CorpusPath("invalid_undef_var.suru");
        var csErrors = CsErrors(path);
        Assert.True(csErrors.Count > 0, "C# should have detected an undefined variable");

        var output   = Run(_exe, path).Trim();
        var exitCode = RunGetExitCode(_exe, path);

        Assert.Equal(1, exitCode);
        Assert.Contains("undefined variable", output);

        // Both sides agree on the error message body (path prefix stripped).
        var firstSuruLine = output.Split('\n')[0].Trim();
        Assert.Equal(ErrorBody(csErrors[0]), ErrorBody(firstSuruLine));
        _testPassed = true;
    }

    [Fact]
    public void CrossValidation_InvalidDupType_ReportsError()
    {
        var path     = CorpusPath("invalid_dup_type.suru");
        var csErrors = CsErrors(path);
        Assert.True(csErrors.Count > 0, "C# should have detected a duplicate type declaration");

        var output   = Run(_exe, path).Trim();
        var exitCode = RunGetExitCode(_exe, path);

        Assert.Equal(1, exitCode);
        Assert.Contains("already declared", output);

        var firstSuruLine = output.Split('\n')[0].Trim();
        Assert.Equal(ErrorBody(csErrors[0]), ErrorBody(firstSuruLine));
        _testPassed = true;
    }

    [Fact]
    public void CrossValidation_InvalidArity_ReportsError()
    {
        var path     = CorpusPath("invalid_arity.suru");
        var csErrors = CsErrors(path);
        Assert.True(csErrors.Count > 0, "C# should have detected an arity mismatch");

        var output   = Run(_exe, path).Trim();
        var exitCode = RunGetExitCode(_exe, path);

        Assert.Equal(1, exitCode);
        Assert.Contains("argument(s)", output);

        var firstSuruLine = output.Split('\n')[0].Trim();
        Assert.Equal(ErrorBody(csErrors[0]), ErrorBody(firstSuruLine));
        _testPassed = true;
    }

    // ─── Milestone: no false positives on existing fixtures ───────────────────

    [Fact]
    public void CrossValidation_Arithmetic_NoErrors()
    {
        var path = FixturePath("arithmetic");
        Assert.True(CsSucceeds(path));

        var exitCode = RunGetExitCode(_exe, path);
        Assert.Equal(0, exitCode);
        Assert.Equal("", Run(_exe, path).Trim());
        _testPassed = true;
    }

    [Fact]
    public void CrossValidation_Fibonacci_NoErrors()
    {
        var path = FixturePath("fibonacci");
        Assert.True(CsSucceeds(path));

        var exitCode = RunGetExitCode(_exe, path);
        Assert.Equal(0, exitCode);
        Assert.Equal("", Run(_exe, path).Trim());
        _testPassed = true;
    }

    [Fact]
    public void CrossValidation_ControlFlow_NoErrors()
    {
        var path = FixturePath("control-flow");
        Assert.True(CsSucceeds(path));

        var exitCode = RunGetExitCode(_exe, path);
        Assert.Equal(0, exitCode);
        Assert.Equal("", Run(_exe, path).Trim());
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("suru-check"); }
}
