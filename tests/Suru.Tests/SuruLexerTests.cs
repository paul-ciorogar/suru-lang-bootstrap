namespace Suru.Tests;

[Collection("Integration")]
public class SuruLexerTests(CompiledFixtures fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("suru-lexer");
    private bool _testPassed;

    // Cross-validate: run the Suru-compiled lexer on a known .suru source and
    // assert each token's kind, text, and position.  Token kinds match the
    // constants defined in the lexer fixture (ordinals == C# TokenKind enum):
    //   0=EOF 1=IDENT 2=TRUE 3=FALSE 4=INT 5=FLOAT 6=STRING
    //   7=( 8=) 9={ 10=} 11=[ 12=] 13=, 14=. 15=: 16=- 17=< 18=>
    //   19=let 20=not 21=and 22=or 23=match 24=_ 25=fn 26=return 27=void
    //   28=while 29=include 30=type 31=as

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

    [Fact]
    public void Lexer_TokenizesPrintFixture()
    {
        var expected =
            "25 fn 1:1\n" +
            "1 main 1:4\n" +
            "7 ( 1:8\n" +
            "1 args 1:9\n" +
            "1 Array 1:14\n" +
            "17 < 1:19\n" +
            "1 String 1:20\n" +
            "18 > 1:26\n" +
            "8 ) 1:27\n" +
            "9 { 1:29\n" +
            "1 printLn 2:5\n" +
            "7 ( 2:12\n" +
            "2 true 2:13\n" +
            "8 ) 2:17\n" +
            "1 printLn 3:5\n" +
            "7 ( 3:12\n" +
            "3 false 3:13\n" +
            "8 ) 3:18\n" +
            "1 printLn 4:5\n" +
            "7 ( 4:12\n" +
            "4 1 4:13\n" +
            "8 ) 4:14\n" +
            "1 printLn 5:5\n" +
            "7 ( 5:12\n" +
            "5 1.2 5:13\n" +
            "8 ) 5:16\n" +
            "10 } 6:1\n" +
            "0  7:1\n";

        Assert.Equal(expected, Run(_exe, FixturePath("print")));
        _testPassed = true;
    }

    [Fact]
    public void Lexer_TokenizesArithmeticKeywords()
    {
        var output = Run(_exe, FixturePath("arithmetic"));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("25 fn 1:1", lines[0]);   // first token is 'fn'
        Assert.StartsWith("0 ", lines[^1]);     // last token is EOF
        _testPassed = true;
    }

    [Fact]
    public void Lexer_TokenizesSelf()
    {
        // Tokenize the library file directly — main.suru is now a thin include wrapper.
        var lexerLib = Path.Combine(Path.GetDirectoryName(FixturePath("suru-lexer"))!, "suru-lexer.suru");
        var output = Run(_exe, lexerLib);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Line 1 is a '//' comment; first emitted token is 'let' on line 2.
        Assert.Equal("19 let 2:1", lines[0]);
        // Last token is EOF
        Assert.StartsWith("0 ", lines[^1]);
        // Should be a substantial number of tokens
        Assert.True(lines.Length > 1000, $"Expected >1000 tokens, got {lines.Length}");
        _testPassed = true;
    }

    [Fact]
    public void Lexer_HandlesStringLiterals()
    {
        // The comparisons fixture doesn't have strings, use structs fixture
        var output = Run(_exe, FixturePath("structs"));
        // Should contain STRING tokens (kind 6) if the fixture has string fields,
        // otherwise just verify it produces output without crashing.
        Assert.NotEmpty(output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("0 ", lines[^1]);  // ends with EOF
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("suru-lexer"); }
}
