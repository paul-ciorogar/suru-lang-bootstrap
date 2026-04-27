namespace Suru.Tests;

// Verifies the IR backend compiles the Stage-9 self-hosting milestone: a complete
// lexer for the Suru language, written in Suru itself (tests/fixtures/suru-lexer/main.suru).
//
// ── What the fixture does ────────────────────────────────────────────────────
//
// suru-lexer reads a .suru source file (passed as args.at(1)), tokenizes it
// character-by-character, and prints one line per token:
//
//   <kind> <text> <line>:<col>
//
// Token kinds are module-level Int64 constants (TOK_EOF=0 … TOK_WHILE=26).
// The main entry point returns void — the process exits 0 implicitly.
//
// ── IR features exercised ────────────────────────────────────────────────────
//
// This is the most comprehensive integration fixture in the IR test suite.
// It exercises nearly every feature of IRCodeGenerator in combination:
//
//   Module-level constants  — 27 TOK_* let bindings emitted as LLVM internal
//                             globals in the pre-pass (pass-0 constants loop).
//
//   User-defined functions  — 15 functions (isDigit, isLetter, makeToken,
//                             keywordKind, readIdent, readNumber, escapeChar,
//                             readString, tokenize, and more). The pre-pass
//                             registers all signatures before any body is emitted.
//
//   Struct tokens           — makeToken returns { kind, text, line, col }; each
//                             field is a %suru.Field node in a heap-allocated
//                             linked list. suru_find_field is emitted once and
//                             reused by every field access / assignment.
//
//   Array<Struct>           — tokenize accumulates tokens via tokens.add(tok)
//                             (realloc growth) and returns the full Array<Struct>.
//                             _arrayElementTypes["tokens"] = SuruType.Struct lets
//                             EmitArrayAt reconstruct the struct pointer correctly.
//
//   String methods          — source.at(pos) (single-char slice), text.append(...),
//                             text.len(), ch.equals("..."), ch.ord() (ASCII value
//                             used in isDigit / isLetter range checks).
//
//   Match-as-expression     — keywordKind, escapeChar, and several helpers return
//                             match expressions. The result alloca is emitted before
//                             the test chain so it dominates all arm blocks.
//
//   While loops             — readIdent, readDigits, tokenize, and main all use
//                             while. Each site gets unique while_cond_N / while_body_N
//                             / while_after_N labels from _whileCounter.
//
//   readFile / args.at(1)   — main reads the source file from the first CLI argument.
//                             args.at(i) uses the special argv-Seq GEP path (char**,
//                             not i64 buffer), followed by strlen and String Seq wrapping.
//
//   printLn(String)         — outputs each formatted token line via @printf with %s\n.
[Collection("IntegrationIR")]
public class IRSuruLexerTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("suru-lexer");

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
        // print/main.suru: fn main(args Array<String>) — <,> not in singleCharKind,
        // so they tokenize as TOK_EOF (0). String tokenizes as IDENT (1).
        var expected =
            "22 fn 1:1\n" +
            "1 main 1:4\n" +
            "7 ( 1:8\n" +
            "1 args 1:9\n" +
            "1 Array 1:14\n" +
            "27 < 1:19\n" +
            "1 String 1:20\n" +
            "28 > 1:26\n" +
            "8 ) 1:27\n" +
            "12 { 1:29\n" +
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
            "13 } 6:1\n" +
            "0  7:1\n";

        Assert.Equal(expected, Run(_exe, FixturePath("print")));
    }

    [Fact]
    public void Lexer_TokenizesArithmeticKeywords()
    {
        var output = Run(_exe, FixturePath("arithmetic"));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("22 fn 1:1", lines[0]);   // first token is 'fn'
        Assert.StartsWith("0 ", lines[^1]);     // last token is EOF
    }

    [Fact]
    public void Lexer_TokenizesSelf()
    {
        var output = Run(_exe, FixturePath("suru-lexer"));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // First token of the lexer source is 'let' (constants precede all functions)
        Assert.Equal("16 let 1:1", lines[0]);
        // Last token is EOF
        Assert.StartsWith("0 ", lines[^1]);
        // Should be a substantial number of tokens
        Assert.True(lines.Length > 1000, $"Expected >1000 tokens, got {lines.Length}");
    }

    [Fact]
    public void Lexer_HandlesStringLiterals()
    {
        var output = Run(_exe, FixturePath("structs"));
        Assert.NotEmpty(output);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("0 ", lines[^1]);  // ends with EOF
    }
}
