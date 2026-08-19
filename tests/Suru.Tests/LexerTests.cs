using Suru.Compiler.Debug;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests;

/// <summary>
/// The lexer is pull-based and <c>NextToken</c> is internal, so it is exercised
/// through the parser: what the tokens were shows up in the AST it produces.
/// </summary>
public class LexerTests
{
    [Theory]
    [InlineData("printLn")]
    [InlineData("print_ln")]
    [InlineData("p1")]
    [InlineData("a_1b")]
    [InlineData("aB")]
    public void LexesIdentifiers(string name)
    {
        var call = Assert.IsType<CallExpression>(Source.SingleExpression(Source.Parse($"{name}()")));

        Assert.Equal(name, call.Name);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void LexesBooleanKeywords(string text, bool value)
    {
        Assert.Equal(value, Assert.IsType<BoolLiteral>(Source.SingleExpression(Source.Parse(text))).Value);
    }

    [Fact]
    public void KeywordsAreNotIdentifiers()
    {
        // "trueish" starts with "true" but is a single identifier.
        var call = Assert.IsType<CallExpression>(Source.SingleExpression(Source.Parse("trueish()")));

        Assert.Equal("trueish", call.Name);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void LexesIntegerLiterals(string text, long value)
    {
        Assert.Equal(value, Assert.IsType<IntLiteral>(Source.SingleExpression(Source.Parse(text))).Value);
    }

    [Theory]
    [InlineData("1.2", 1.2)]
    [InlineData("0.5", 0.5)]
    public void LexesFloatLiterals(string text, double value)
    {
        Assert.Equal(value, Assert.IsType<FloatLiteral>(Source.SingleExpression(Source.Parse(text))).Value);
    }

    [Fact]
    public void ATrailingDotIsNotPartOfANumber()
    {
        // "1." lexes as IntLiteral(1) followed by an unexpected '.'.
        Assert.Throws<LexException>(() => Source.Parse("1."));
    }

    [Fact]
    public void SkipsWhitespaceBetweenTokens()
    {
        var call = Assert.IsType<CallExpression>(
            Source.SingleExpression(Source.Parse("  printLn  (  1  )  ")));

        Assert.Equal(1L, Assert.IsType<IntLiteral>(Assert.Single(call.Args)).Value);
    }

    [Fact]
    public void SkipsLineComments()
    {
        var module = Source.Parse("""
            // a leading comment
            printLn(1) // a trailing comment
            // printLn(2)
            printLn(3)
            """);

        // The positions show the comments were skipped without disturbing the line count.
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (2,1)
                CallExpression (2,1) printLn
                  IntLiteral (2,9) 1
              ExpressionStatement (4,1)
                CallExpression (4,1) printLn
                  IntLiteral (4,9) 3

            """,
            AstPrinter.Print(module));
    }

    [Fact]
    public void ACommentEndsAtTheNewline()
    {
        var module = Source.Parse("printLn( // note\n1)");

        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                CallExpression (1,1) printLn
                  IntLiteral (2,1) 1

            """,
            AstPrinter.Print(module));
    }

    [Fact]
    public void ACommentCanRunToTheEndOfTheFile()
    {
        var module = Source.Parse("printLn(1) // no newline");

        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                CallExpression (1,1) printLn
                  IntLiteral (1,9) 1

            """,
            AstPrinter.Print(module));
    }

    [Fact]
    public void AFileOfOnlyCommentsIsAnEmptyProgram()
    {
        Assert.Empty(Source.Parse("// nothing here\n// or here").Statements);
    }

    [Fact]
    public void ACommentDoesNotDisturbLaterPositions()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("// comment\nprintLn(1) $"));

        Assert.Equal("test.suru(2,12): unexpected character '$'", exception.Message);
    }

    [Fact]
    public void ASingleSlashIsNotAComment()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("printLn(1) / 2"));

        Assert.Equal("test.suru(1,12): unexpected character '/'", exception.Message);
    }

    [Fact]
    public void ReportsAnUnexpectedCharacterWithItsPosition()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("printLn(1)\n$"));

        Assert.Equal("test.suru(2,1): unexpected character '$'", exception.Message);
    }
}
