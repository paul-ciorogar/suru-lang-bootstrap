using Suru.Compiler.Debug;

namespace Suru.Tests;

/// <summary>
/// Stage dumps are a debugging interface, so their exact text is the contract:
/// asserting on the whole string is what keeps them diffable between stages.
/// </summary>
public class DumpTests
{
    [Fact]
    public void DumpsTheTokenStreamInSourceOrder()
    {
        var dump = TokenPrinter.Print("printLn(1)", Source.Path);

        Assert.Equal(
            """
            (1,1)     Identifier printLn
            (1,8)     LeftParen
            (1,9)     IntLiteral 1
            (1,10)    RightParen
            (1,11)    Eof

            """,
            dump);
    }

    [Fact]
    public void TheTokenDumpStopsWhereTheLexerDid()
    {
        // The parse reports the error properly; the dump only has to not throw.
        var dump = TokenPrinter.Print("printLn($)", Source.Path);

        Assert.Equal(
            """
            (1,1)     Identifier printLn
            (1,8)     LeftParen
            <lex error: test.suru(1,9): unexpected character '$'>

            """,
            dump);
    }

    [Fact]
    public void DumpsTheParsedAstWithoutTypes()
    {
        var dump = AstPrinter.Print(Source.Parse("printLn(1)\nprintLn(1.5)"));

        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                CallExpression (1,1) printLn
                  IntLiteral (1,9) 1
              ExpressionStatement (2,1)
                CallExpression (2,1) printLn
                  FloatLiteral (2,9) 1.5

            """,
            dump);
    }

    [Fact]
    public void DumpsTheAnalyzedAstWithTypes()
    {
        var dump = AstPrinter.Print(Source.Analyzed("printLn(true)"), withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                CallExpression (1,1) printLn : void
                  BoolLiteral (1,9) true : bool

            """,
            dump);
    }

    [Fact]
    public void UntypedExpressionsAreMarkedRatherThanOmitted()
    {
        // Dumped straight from the parser, so nothing has been typed yet.
        var dump = AstPrinter.Print(Source.Parse("printLn(1)"), withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                CallExpression (1,1) printLn : ?
                  IntLiteral (1,9) 1 : ?

            """,
            dump);
    }

    [Fact]
    public void DumpsBindingsOperatorsAndVariableUses()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed("let total i64: 1 + 2\ntotal: -total\nprintLn(not false)"),
            withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) total i64
                BinaryExpression (1,16) + : i64
                  IntLiteral (1,16) 1 : i64
                  IntLiteral (1,20) 2 : i64
              AssignmentStatement (2,1) total
                UnaryExpression (2,8) - : i64
                  IdentifierExpression (2,9) total : i64
              ExpressionStatement (3,1)
                CallExpression (3,1) printLn : void
                  UnaryExpression (3,9) not : bool
                    BoolLiteral (3,13) false : bool

            """,
            dump);
    }

    [Fact]
    public void DumpsABlockWithItsStatementsIndentedOneLevel()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed("let x i64: 1\n{\nlet x f64: 1.5\nprintLn(x)\n}\nprintLn(x)"),
            withTypes: true);

        // The two 'x' uses differ only in type: the inner one resolves to the shadow.
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) x i64
                IntLiteral (1,12) 1 : i64
              BlockStatement (2,1)
                LetStatement (3,1) x f64
                  FloatLiteral (3,12) 1.5 : f64
                ExpressionStatement (4,1)
                  CallExpression (4,1) printLn : void
                    IdentifierExpression (4,9) x : f64
              ExpressionStatement (6,1)
                CallExpression (6,1) printLn : void
                  IdentifierExpression (6,9) x : i64

            """,
            dump);
    }

    [Fact]
    public void DumpsAnIfWithItsConditionAndArms()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed("let x i64: 1\nif x > 0 {\nprintLn(x)\n} else {\nprintLn(0)\n}"),
            withTypes: true);

        // The condition types as 'bool' although both its operands are 'i64' — that is what
        // makes it a condition at all.
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) x i64
                IntLiteral (1,12) 1 : i64
              IfStatement (2,1)
                BinaryExpression (2,4) > : bool
                  IdentifierExpression (2,4) x : i64
                  IntLiteral (2,8) 0 : i64
                BlockStatement (2,10)
                  ExpressionStatement (3,1)
                    CallExpression (3,1) printLn : void
                      IdentifierExpression (3,9) x : i64
                BlockStatement (4,8)
                  ExpressionStatement (5,1)
                    CallExpression (5,1) printLn : void
                      IntLiteral (5,9) 0 : i64

            """,
            dump);
    }

    [Fact]
    public void DumpsIfAndElseAsKeywordTokens()
    {
        var dump = TokenPrinter.Print("if {} else {}", Source.Path);

        // Keywords carry their text, unlike the punctuation around them.
        Assert.Equal(
            """
            (1,1)     If if
            (1,4)     LeftBrace
            (1,5)     RightBrace
            (1,7)     Else else
            (1,12)    LeftBrace
            (1,13)    RightBrace
            (1,14)    Eof

            """,
            dump);
    }

    [Fact]
    public void DumpsBracesAsTokens()
    {
        var dump = TokenPrinter.Print("{}", Source.Path);

        Assert.Equal(
            """
            (1,1)     LeftBrace
            (1,2)     RightBrace
            (1,3)     Eof

            """,
            dump);
    }

    [Theory]
    [InlineData("tokens", Dump.Tokens)]
    [InlineData("ast", Dump.Ast)]
    [InlineData("typed-ast", Dump.TypedAst)]
    [InlineData("llvm", Dump.Llvm)]
    [InlineData("all", Dump.All)]
    [InlineData("tokens,llvm", Dump.Tokens | Dump.Llvm)]
    [InlineData(" tokens , ast ", Dump.Tokens | Dump.Ast)]
    [InlineData("TOKENS", Dump.Tokens)]
    public void ParsesStageLists(string spec, Dump expected)
    {
        Assert.True(DumpSpec.TryParse(spec, out var dump, out _));

        Assert.Equal(expected, dump);
    }

    [Fact]
    public void RejectsAnUnknownStageRatherThanIgnoringIt()
    {
        Assert.False(DumpSpec.TryParse("tokens,nope", out var dump, out var error));

        Assert.Equal(Dump.None, dump);
        Assert.Equal("unknown dump stage 'nope'; expected one of: tokens, ast, typed-ast, llvm, all", error);
    }

    [Fact]
    public void WritesEnabledSectionsUnderATitle()
    {
        var writer = new StringWriter();
        var dumps = new DumpOptions(Dump.Ast, writer);

        dumps.Section(Dump.Ast, "ast after parse", () => "body\n");

        Assert.Equal("===== ast after parse =====\nbody\n\n", writer.ToString());
    }

    [Fact]
    public void ADisabledSectionNeverRunsItsPrinter()
    {
        var writer = new StringWriter();
        var dumps = new DumpOptions(Dump.Ast, writer);

        dumps.Section(Dump.Tokens, "tokens", () => throw new InvalidOperationException("printed"));

        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public void DumpsAreOffByDefault()
    {
        Assert.Equal(Dump.None, DumpOptions.Off.Enabled);
        Assert.False(DumpOptions.Off.IsEnabled(Dump.Tokens));
    }
}
