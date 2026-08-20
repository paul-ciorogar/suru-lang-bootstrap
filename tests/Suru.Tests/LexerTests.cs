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
    [InlineData("1_000", 1000L)]
    [InlineData("1_000_000", 1000000L)]
    [InlineData("0xff", 255L)]
    [InlineData("0xFF", 255L)]    // the digits may be either case, only the prefix may not
    [InlineData("0x7fff_ffff_ffff_ffff", long.MaxValue)]
    [InlineData("0b1010", 10L)]
    [InlineData("0b1010_1010", 170L)]
    [InlineData("0o755", 493L)]
    public void LexesIntegerLiterals(string text, long value)
    {
        Assert.Equal(value, Assert.IsType<IntLiteral>(Source.SingleExpression(Source.Parse(text))).Value);
    }

    [Theory]
    [InlineData("1.2", 1.2)]
    [InlineData("0.5", 0.5)]
    [InlineData("1_000.000_1", 1000.0001)]
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

    [Theory]
    [InlineData("0x")]        // a prefix with no digits
    [InlineData("0b")]
    [InlineData("0o")]
    [InlineData("0xg")]       // not a digit of the base
    [InlineData("0b2")]
    [InlineData("0o8")]
    [InlineData("0xffz")]     // junk butted up against a complete literal
    [InlineData("1i64")]
    [InlineData("1abc")]
    [InlineData("1_")]        // a separator that does not separate two digits
    [InlineData("1__0")]
    [InlineData("0x_ff")]
    [InlineData("1_.0")]
    public void RejectsMalformedNumbers(string text)
    {
        Assert.Throws<LexException>(() => Source.Parse(text));
    }

    [Theory]
    [InlineData("0XFF")]
    [InlineData("0B1010")]
    [InlineData("0O777")]
    public void RejectsAnUppercaseBasePrefix(string text)
    {
        Assert.Throws<LexException>(() => Source.Parse(text));
    }

    [Fact]
    public void ReportsAnUppercaseBasePrefixWhereItSits()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("printLn(0XFF)"));

        Assert.Equal($"{Source.Path}(1,10): base prefix 'X' must be lowercase", exception.Message);
    }

    [Theory]
    [InlineData("9223372036854775809")]
    [InlineData("0xFFFFFFFFFFFFFFFF")]
    public void RejectsIntegerLiteralsOutOfRange(string text)
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse(text));

        Assert.Equal($"{Source.Path}(1,1): integer literal is out of range for 'i64'", exception.Message);
    }

    [Fact]
    public void ASignDoesNotRescueAMagnitudeTheLexerCannotHold()
    {
        // One past the i64 minimum: out of range whether or not the '-' folds in, so the
        // lexer still reports it against the digits.
        var exception = Assert.Throws<LexException>(() => Source.Parse("-9223372036854775809"));

        Assert.Equal($"{Source.Path}(1,2): integer literal is out of range for 'i64'", exception.Message);
    }

    [Fact]
    public void ReportsAnInvalidDigitWhereItSits()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("printLn(0xffz)"));

        Assert.Equal($"{Source.Path}(1,13): invalid digit 'z' in hexadecimal literal", exception.Message);
    }

    [Fact]
    public void ReportsAMisplacedSeparatorWhereItSits()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("printLn(1_000_)"));

        Assert.Equal($"{Source.Path}(1,14): '_' must separate digits", exception.Message);
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
    public void ASingleSlashIsDivisionAndNotAComment()
    {
        var binary = Assert.IsType<BinaryExpression>(Source.SingleExpression(Source.Parse("6 / 2 // half")));

        Assert.Equal(BinaryOperator.Divide, binary.Operator);
        Assert.Equal(6L, Assert.IsType<IntLiteral>(binary.Left).Value);
        Assert.Equal(2L, Assert.IsType<IntLiteral>(binary.Right).Value);
    }

    [Theory]
    [InlineData("1 + 2", BinaryOperator.Add)]
    [InlineData("1 - 2", BinaryOperator.Subtract)]
    [InlineData("1 * 2", BinaryOperator.Multiply)]
    [InlineData("1 / 2", BinaryOperator.Divide)]
    [InlineData("1 % 2", BinaryOperator.Remainder)]
    [InlineData("1 = 2", BinaryOperator.Equal)]
    [InlineData("1 <> 2", BinaryOperator.NotEqual)]
    [InlineData("1 < 2", BinaryOperator.Less)]
    [InlineData("1 <= 2", BinaryOperator.LessOrEqual)]
    [InlineData("1 > 2", BinaryOperator.Greater)]
    [InlineData("1 >= 2", BinaryOperator.GreaterOrEqual)]
    [InlineData("1 and 2", BinaryOperator.And)]
    [InlineData("1 or 2", BinaryOperator.Or)]
    public void LexesBinaryOperators(string text, BinaryOperator op)
    {
        Assert.Equal(op, Assert.IsType<BinaryExpression>(Source.SingleExpression(Source.Parse(text))).Operator);
    }

    [Fact]
    public void OperatorsNeedNoSurroundingWhitespace()
    {
        var binary = Assert.IsType<BinaryExpression>(Source.SingleExpression(Source.Parse("1<=2")));

        Assert.Equal(BinaryOperator.LessOrEqual, binary.Operator);
    }

    [Fact]
    public void WordOperatorsAreKeywordsAndNotIdentifiers()
    {
        // "ands" starts with "and" but is a single identifier.
        var identifier = Assert.IsType<IdentifierExpression>(Source.SingleExpression(Source.Parse("ands")));

        Assert.Equal("ands", identifier.Name);
    }

    [Fact]
    public void ReportsAnUnexpectedCharacterWithItsPosition()
    {
        var exception = Assert.Throws<LexException>(() => Source.Parse("printLn(1)\n$"));

        Assert.Equal("test.suru(2,1): unexpected character '$'", exception.Message);
    }
}
