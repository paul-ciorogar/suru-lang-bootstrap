using Suru.Compiler;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests;

public class SemanticTests
{
    [Theory]
    [InlineData("printLn(true)")]
    [InlineData("printLn(false)")]
    [InlineData("printLn(1)")]
    [InlineData("printLn(1.2)")]
    public void AcceptsPrintableArguments(string text)
    {
        Assert.Empty(Source.Analyze(text));
    }

    [Fact]
    public void AcceptsAnEmptyModule()
    {
        Assert.Empty(Source.Analyze(""));
    }

    [Theory]
    [InlineData("printLn(true)", "bool")]
    [InlineData("printLn(1)", "i64")]
    [InlineData("printLn(1.2)", "f64")]
    public void AnnotatesTheArgumentWithItsType(string text, string typeName)
    {
        var call = Assert.IsType<CallExpression>(Source.SingleExpression(Source.Analyzed(text)));

        Assert.Equal(new SuruType(typeName), Assert.Single(call.Args).Type);
    }

    [Fact]
    public void AnnotatesAPrintLnCallAsVoid()
    {
        var call = Source.SingleExpression(Source.Analyzed("printLn(1)"));

        Assert.Equal(SuruType.Void, call.Type);
    }

    [Fact]
    public void ReportsUnknownFunction()
    {
        var error = Assert.Single(Source.Analyze("printLn(1)\nfoo(2)"));

        Assert.Equal("test.suru(2,1): unknown function 'foo'", error);
    }

    [Fact]
    public void ReportsWrongArity()
    {
        var errors = Source.Analyze("printLn(1, 2)\nprintLn()");

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(1,1): 'printLn' expects 1 argument, got 2", error),
            error => Assert.Equal("test.suru(2,1): 'printLn' expects 1 argument, got 0", error));
    }

    [Fact]
    public void ReportsUnprintableArgumentAtTheArgumentPosition()
    {
        var error = Assert.Single(Source.Analyze("printLn(printLn(1))"));

        Assert.Equal(
            "test.suru(1,9): 'printLn' cannot print a value of type 'void'; expected 'bool', 'i64', 'f64'",
            error);
    }

    [Fact]
    public void ReportsNonCallStatement()
    {
        var error = Assert.Single(Source.Analyze("42"));

        Assert.Equal("test.suru(1,1): only call expressions are allowed as statements", error);
    }

    [Fact]
    public void AnUnknownFunctionArgumentReportsOnlyOneError()
    {
        // The argument's type is null, so the printability check stays quiet
        // rather than piling a second error onto the same expression.
        var error = Assert.Single(Source.Analyze("printLn(foo())"));

        Assert.Equal("test.suru(1,9): unknown function 'foo'", error);
    }

    [Fact]
    public void ErrorsAreReportedWithTheSourcePathAndPosition()
    {
        var error = Assert.Single(Source.Analyze("42"));

        Assert.Matches(@"^test\.suru\(\d+,\d+\): ", error);
    }
}
