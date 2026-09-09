using Suru.Compiler;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests.Compiler.Semantic;

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

    [Theory]
    [InlineData("printLn(1 + 2)", "i64")]
    [InlineData("printLn(1.0 * 2.0)", "f64")]
    [InlineData("printLn(1 < 2)", "bool")]
    [InlineData("printLn(1.0 <> 2.0)", "bool")]
    [InlineData("printLn(true and false)", "bool")]
    [InlineData("printLn(true = false)", "bool")]
    [InlineData("printLn(-1)", "i64")]
    [InlineData("printLn(not true)", "bool")]
    public void AnnotatesAnOperatorWithItsResultType(string text, string typeName)
    {
        var call = Assert.IsType<CallExpression>(Source.SingleExpression(Source.Analyzed(text)));

        Assert.Equal(new SuruType(typeName), Assert.Single(call.Args).Type);
    }

    [Fact]
    public void AnnotatesAVariableWithTheTypeItWasBoundAt()
    {
        var module = Source.Analyzed("let count i64: 1\nprintLn(count)");

        var call = Assert.IsType<CallExpression>(
            Assert.IsType<ExpressionStatement>(module.Statements[1]).Expression);
        Assert.Equal(SuruType.I64, Assert.Single(call.Args).Type);
    }

    [Fact]
    public void AcceptsAnAssignmentOfTheBoundType()
    {
        Assert.Empty(Source.Analyze("let count i64: 1\ncount: count + 1"));
    }

    [Theory]
    [InlineData("printLn(1 + 1.5)", "test.suru(1,9): operator '+' cannot be applied to 'i64' and 'f64'")]
    [InlineData("printLn(true = 1)", "test.suru(1,9): operator '=' cannot be applied to 'bool' and 'i64'")]
    [InlineData("printLn(true < false)", "test.suru(1,9): operator '<' cannot be applied to 'bool' and 'bool'")]
    [InlineData("printLn(1 and 2)", "test.suru(1,9): operator 'and' cannot be applied to 'i64' and 'i64'")]
    [InlineData("printLn(not 1)", "test.suru(1,9): operator 'not' cannot be applied to 'i64'")]
    [InlineData("printLn(-true)", "test.suru(1,9): operator '-' cannot be applied to 'bool'")]
    public void ReportsAnOperatorAppliedToTheWrongTypes(string text, string expected)
    {
        Assert.Equal(expected, Assert.Single(Source.Analyze(text)));
    }

    [Fact]
    public void AnOperandThatFailedReportsOnlyOneError()
    {
        // The left operand has no type, so the operator check stays quiet rather
        // than piling a second error onto the same expression.
        var error = Assert.Single(Source.Analyze("printLn(missing + 1)"));

        Assert.Equal("test.suru(1,9): unknown variable 'missing'", error);
    }

    [Fact]
    public void ReportsAnUnknownTypeAtTheTypeName()
    {
        var error = Assert.Single(Source.Analyze("let count int: 1"));

        Assert.Equal("test.suru(1,11): unknown type 'int'", error);
    }

    [Fact]
    public void ReportsVoidAsAnUnknownType()
    {
        // 'void' is the type of a printLn call, not something a program can write down.
        var error = Assert.Single(Source.Analyze("let nothing void: printLn(1)"));

        Assert.Equal("test.suru(1,13): unknown type 'void'", error);
    }

    [Fact]
    public void ReportsARedeclaredBinding()
    {
        var error = Assert.Single(Source.Analyze("let count i64: 1\nlet count i64: 2"));

        Assert.Equal("test.suru(2,1): 'count' is already declared", error);
    }

    [Fact]
    public void ReportsABindingWhoseValueHasTheWrongType()
    {
        var error = Assert.Single(Source.Analyze("let count i64: 1.5"));

        Assert.Equal("test.suru(1,16): cannot bind a value of type 'f64' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ReportsAnAssignmentOfTheWrongType()
    {
        var error = Assert.Single(Source.Analyze("let count i64: 1\ncount: true"));

        Assert.Equal("test.suru(2,8): cannot assign a value of type 'bool' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ReportsAnUnknownVariable()
    {
        var errors = Source.Analyze("printLn(count)\ncount: 1");

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(1,9): unknown variable 'count'", error),
            error => Assert.Equal("test.suru(2,1): unknown variable 'count'", error));
    }

    [Fact]
    public void ABindingWithABadValueIsStillDeclared()
    {
        // One error for the initialiser; the later use resolves rather than
        // reporting 'unknown variable' as well.
        var error = Assert.Single(Source.Analyze("let count i64: 1.5\nprintLn(count)"));

        Assert.Equal("test.suru(1,16): cannot bind a value of type 'f64' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ReportsAVariableUsedBeforeItIsBound()
    {
        var error = Assert.Single(Source.Analyze("printLn(count)\nlet count i64: 1"));

        Assert.Equal("test.suru(1,9): unknown variable 'count'", error);
    }

    [Fact]
    public void ABindingInsideABlockShadowsAnOuterOne()
    {
        var module = Source.Analyzed("let x i64: 1\n{\nlet x f64: 1.5\nprintLn(x)\n}");
        var block = Assert.IsType<BlockStatement>(module.Statements[1]);

        Assert.Equal(SuruType.F64, PrintedArgument(block.Statements[1]).Type);
    }

    [Fact]
    public void TheOuterBindingComesBackAfterTheBlock()
    {
        var module = Source.Analyzed("let x i64: 1\n{\nlet x f64: 1.5\n}\nprintLn(x)");

        Assert.Equal(SuruType.I64, PrintedArgument(module.Statements[2]).Type);
    }

    [Fact]
    public void ABindingDoesNotEscapeItsBlock()
    {
        var error = Assert.Single(Source.Analyze("{\nlet x i64: 1\n}\nprintLn(x)"));

        Assert.Equal("test.suru(4,9): unknown variable 'x'", error);
    }

    [Fact]
    public void ReportsARedeclarationWithinTheSameBlock()
    {
        // Shadowing crosses nesting levels; it does not license rebinding in one scope.
        var error = Assert.Single(Source.Analyze("{\nlet x i64: 1\nlet x i64: 2\n}"));

        Assert.Equal("test.suru(3,1): 'x' is already declared", error);
    }

    [Fact]
    public void ABlockCanBindANameThatIsFreeOutsideIt()
    {
        Assert.Empty(Source.Analyze("{\nlet x i64: 1\nprintLn(x)\n}\n{\nlet x i64: 2\nprintLn(x)\n}"));
    }

    [Fact]
    public void ABlockCanReadAndAssignAnOuterBinding()
    {
        Assert.Empty(Source.Analyze("let count i64: 1\n{\ncount: count + 1\n}\nprintLn(count)"));
    }

    [Fact]
    public void AnAssignmentInsideABlockStillChecksTheOuterType()
    {
        var error = Assert.Single(Source.Analyze("let count i64: 1\n{\ncount: true\n}"));

        Assert.Equal("test.suru(3,8): cannot assign a value of type 'bool' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ABlockReportsTheErrorsInsideIt()
    {
        var errors = Source.Analyze("{\nprintLn(missing)\n1\n}");

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(2,9): unknown variable 'missing'", error),
            error => Assert.Equal("test.suru(3,1): only call expressions are allowed as statements", error));
    }

    [Theory]
    [InlineData("if true { }")]
    [InlineData("if not false { }")]
    [InlineData("let x i64: 1\nif x = 1 { } else { }")]
    [InlineData("let ok bool: true\nif ok { } else if not ok { } else { }")]
    public void AcceptsABooleanCondition(string text)
    {
        Assert.Empty(Source.Analyze(text));
    }

    [Theory]
    [InlineData("if 1 { }", "i64")]
    [InlineData("if 1.5 { }", "f64")]
    [InlineData("if printLn(1) { }", "void")]
    public void ReportsANonBooleanCondition(string text, string typeName)
    {
        // Nothing converts implicitly, so a number is not a truth value.
        Assert.Equal(
            $"test.suru(1,4): 'if' cannot branch on a value of type '{typeName}'; expected 'bool'",
            Assert.Single(Source.Analyze(text)));
    }

    [Fact]
    public void SaysNothingMoreWhenTheConditionAlreadyFailed()
    {
        Assert.Equal("test.suru(1,4): unknown variable 'missing'",
            Assert.Single(Source.Analyze("if missing { }")));
    }

    [Fact]
    public void ChecksTheConditionOfEveryArmInAChain()
    {
        Assert.Equal("test.suru(1,21): 'if' cannot branch on a value of type 'i64'; expected 'bool'",
            Assert.Single(Source.Analyze("if true { } else if 1 { }")));
    }

    [Fact]
    public void EachArmIsItsOwnScope()
    {
        // Both arms bind 'x', which is only legal if neither can see the other's.
        Assert.Equal("test.suru(6,9): unknown variable 'x'",
            Assert.Single(Source.Analyze("if true {\nlet x i64: 1\n} else {\nlet x f64: 1.5\n}\nprintLn(x)")));
    }

    [Fact]
    public void ReportsTheErrorsInBothArms()
    {
        var errors = Source.Analyze("if true {\nfoo()\n} else {\nbar()\n}");

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(2,1): unknown function 'foo'", error),
            error => Assert.Equal("test.suru(4,1): unknown function 'bar'", error));
    }

    [Fact]
    public void ErrorsAreReportedWithTheSourcePathAndPosition()
    {
        var error = Assert.Single(Source.Analyze("42"));

        Assert.Matches(@"^test\.suru\(\d+,\d+\): ", error);
    }

    /// <summary>The lone argument of a <c>printLn</c> statement, to read its resolved type off.</summary>
    private static Expression PrintedArgument(Statement statement)
    {
        var call = Assert.IsType<CallExpression>(Assert.IsType<ExpressionStatement>(statement).Expression);
        return Assert.Single(call.Args);
    }
}
