using Suru.Compiler;
using Suru.Compiler.Parse;
using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests;

// Verifies the IR backend compiles the Stage-12 self-hosting milestone: a complete
// recursive-descent parser for the Suru language, written in Suru itself
// (tests/fixtures/suru-parser/main.suru).
//
// ── What the fixture does ────────────────────────────────────────────────────
//
// suru-parser reads a .suru source file (passed as args.at(1)), tokenizes it
// using the suru-lexer fixture, runs the Suru-implemented parser over the token
// stream, then pretty-prints the AST in the same indented-tree format produced
// by the C# AstPrinter.  The output is used to cross-validate both parsers.
//
// ── Cross-validation strategy ────────────────────────────────────────────────
//
// For each test input we compare two strings:
//   csOutput  — AstPrinter.Print(new Compiler(path).ParseFile().Value!)
//   suruOutput — Run(_exe, path)
//
// The two printers must produce identical output; any divergence is a parser bug.
[Collection("IntegrationIR")]
public class IRSuruParserTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("suru-parser");
    private bool _testPassed;

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

    private static string CsAst(string path)
    {
        var result = new SuruCompiler(path).ParseFile();
        if (!result.Success)
            throw new InvalidOperationException(
                $"C# parse failed for '{path}':\n{string.Join("\n", result.Errors)}");
        return AstPrinter.Print(result.Value!).TrimEnd();
    }

    [Fact]
    public void Parser_CrossValidatesPrintFixture()
    {
        var path = FixturePath("print");
        var suruOutput = Run(_exe, path).TrimEnd();
        var csOutput   = CsAst(path);
        Assert.Equal(csOutput, suruOutput);
        _testPassed = true;
    }

    [Fact]
    public void Parser_CrossValidatesLexerSource()
    {
        // suru-lexer.suru has no includes and no float literals — safe for exact
        // string comparison.  This is the Stage-12 milestone: the Suru parser
        // produces correct AST for a substantial real-world Suru source file.
        var dir = AppContext.BaseDirectory;
        string? lexerPath = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "suru-lexer", "suru-lexer.suru");
            if (File.Exists(candidate)) { lexerPath = candidate; break; }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(lexerPath);

        var suruOutput = Run(_exe, lexerPath!).TrimEnd();
        var csOutput   = CsAst(lexerPath!);
        Assert.Equal(csOutput, suruOutput);
        _testPassed = true;
    }

    [Fact]
    public void Parser_ParsesSelf()
    {
        // Feed the parser its own source file.  The C# parser would expand the
        // include directive and merge the lexer's functions, so we cannot do an
        // exact cross-validation here — instead we check the output is well-formed.
        var dir = AppContext.BaseDirectory;
        string? selfPath = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "tests", "fixtures", "suru-parser", "suru-parser.suru");
            if (File.Exists(candidate)) { selfPath = candidate; break; }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(selfPath);

        var output = Run(_exe, selfPath!);
        var lines  = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Module [", lines[0]);
        Assert.True(lines.Length > 500, $"Expected >500 lines from parsing suru-parser.suru, got {lines.Length}");
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("suru-parser"); }
}
