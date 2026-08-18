using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests;

public class ParserTests
{
    [Fact]
    public void ParsesTrue()
    {
        var call = ParseSingleCall("printLn(true)");

        Assert.Equal("printLn", call.Name);
        var literal = Assert.IsType<BoolLiteral>(Assert.Single(call.Args));
        Assert.True(literal.Value);
    }

    [Fact]
    public void ParsesFalse()
    {
        var literal = Assert.IsType<BoolLiteral>(Assert.Single(ParseSingleCall("printLn(false)").Args));

        Assert.False(literal.Value);
    }

    [Fact]
    public void ParsesIntLiteral()
    {
        var literal = Assert.IsType<IntLiteral>(Assert.Single(ParseSingleCall("printLn(1)").Args));

        Assert.Equal(1L, literal.Value);
    }

    [Fact]
    public void ParsesFloatLiteral()
    {
        var literal = Assert.IsType<FloatLiteral>(Assert.Single(ParseSingleCall("printLn(1.2)").Args));

        Assert.Equal(1.2, literal.Value);
    }

    [Fact]
    public void ParsesEmptySourceAsAnEmptyModule()
    {
        Assert.Empty(Source.Parse("").Statements);
    }

    [Fact]
    public void ParsesTheSourcePathIntoTheModule()
    {
        Assert.Equal(Source.Path, Source.Parse("printLn(1)").SourcePath);
    }

    [Fact]
    public void ParsesOneStatementPerLineWithoutATerminator()
    {
        var module = Source.Parse("printLn(1)\nprintLn(2)\nprintLn(3)");

        Assert.Collection(module.Statements,
            statement => AssertCall(statement, "printLn", 1),
            statement => AssertCall(statement, "printLn", 2),
            statement => AssertCall(statement, "printLn", 3));
    }

    [Fact]
    public void ParsesAnEmptyArgumentList()
    {
        // Arity is a semantic concern; the grammar allows it.
        Assert.Empty(ParseSingleCall("printLn()").Args);
    }

    [Fact]
    public void ParsesMultipleArguments()
    {
        var call = ParseSingleCall("printLn(1, 2)");

        Assert.Collection(call.Args,
            arg => Assert.Equal(1L, Assert.IsType<IntLiteral>(arg).Value),
            arg => Assert.Equal(2L, Assert.IsType<IntLiteral>(arg).Value));
    }

    [Fact]
    public void ParsesNestedCalls()
    {
        var inner = Assert.IsType<CallExpression>(Assert.Single(ParseSingleCall("printLn(printLn(1))").Args));

        Assert.Equal("printLn", inner.Name);
    }

    [Fact]
    public void ParsesABareLiteralAsAStatement()
    {
        // Rejected later by semantic analysis, but it is a valid parse.
        var expression = Source.SingleExpression(Source.Parse("42"));

        Assert.Equal(42L, Assert.IsType<IntLiteral>(expression).Value);
    }

    [Fact]
    public void RecordsThePositionOfACallAndItsArgument()
    {
        var call = ParseSingleCall("printLn(1)");

        Assert.Equal(new SourcePosition(1, 1), call.Position);
        Assert.Equal(new SourcePosition(1, 9), Assert.Single(call.Args).Position);
    }

    [Fact]
    public void RecordsThePositionOfStatementsOnLaterLines()
    {
        var module = Source.Parse("printLn(1)\n  printLn(2)");

        Assert.Collection(module.Statements,
            statement => Assert.Equal(new SourcePosition(1, 1), statement.Position),
            statement => Assert.Equal(new SourcePosition(2, 3), statement.Position));
    }

    [Fact]
    public void ReportsAMissingClosingParen()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("printLn(1"));

        Assert.Equal("test.suru(1,10): expected RightParen, got Eof", exception.Message);
    }

    [Fact]
    public void ReportsAMissingOpeningParen()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("printLn 1"));

        Assert.Equal("test.suru(1,9): expected LeftParen, got IntLiteral", exception.Message);
    }

    [Fact]
    public void ReportsATrailingComma()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("printLn(1,)"));

        Assert.Equal("test.suru(1,11): unexpected token RightParen", exception.Message);
    }

    [Fact]
    public void ReportsAnExpressionThatCannotStartAStatement()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("(1)"));

        Assert.Equal("test.suru(1,1): unexpected token LeftParen", exception.Message);
    }

    private static CallExpression ParseSingleCall(string text) =>
        Assert.IsType<CallExpression>(Source.SingleExpression(Source.Parse(text)));

    private static void AssertCall(Statement statement, string name, long argument)
    {
        var call = Assert.IsType<CallExpression>(Assert.IsType<ExpressionStatement>(statement).Expression);
        Assert.Equal(name, call.Name);
        Assert.Equal(argument, Assert.IsType<IntLiteral>(Assert.Single(call.Args)).Value);
    }
}
