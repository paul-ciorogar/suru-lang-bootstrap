using Suru.Compiler;
using Suru.Compiler.Debug;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests;

/// <summary>
/// The <c>#</c> test directives through the front end: what a production build does with
/// them (nothing at all), what a test build parses them into, and every diagnostic they
/// add. No LLVM and no C toolchain — running them is <see cref="TestModeTests"/>.
/// </summary>
public class DirectiveTests
{
    // A production build drops the line in the lexer, so none of these reach the parser.

    [Fact]
    public void ProductionIgnoresEveryDirective()
    {
        var module = Source.Parse("""
            let x i64: 1
            #mock x: 2
            #view x:
            #assert(x, 1): pass
            """);

        Assert.IsType<LetStatement>(Assert.Single(module.Statements));
    }

    [Fact]
    public void ProductionIgnoresADirectiveItCouldNotParse()
    {
        // The whole point of skipping in the lexer: syntax this compiler does not know
        // still cannot break a release build.
        Assert.Empty(Source.Parse("#whatever <- this is not Suru at all").Statements);
    }

    [Fact]
    public void ProductionStopsIgnoringAtTheEndOfTheLine()
    {
        var module = Source.Parse("#view x:\nprintLn(1)");

        Assert.IsType<ExpressionStatement>(Assert.Single(module.Statements));
    }

    // Parsing, in test mode.

    [Fact]
    public void ParsesMock()
    {
        var mock = ParseSingle<MockDirective>("let x i64: 1\n#mock x: 2");

        Assert.Equal("x", mock.Name);
        Assert.Equal(2L, Assert.IsType<IntLiteral>(mock.Value).Value);
        Assert.Equal(1, mock.Position.Column);
        Assert.Equal(7, mock.NamePosition.Column);
    }

    [Fact]
    public void ParsesViewOfAnExpression()
    {
        var view = ParseSingle<ViewDirective>("#view 1 + 2:");

        Assert.IsType<BinaryExpression>(view.Subject);
    }

    [Fact]
    public void ParsesAssert()
    {
        var assert = ParseSingle<AssertDirective>("#assert(1, 2)");

        Assert.Equal(1L, Assert.IsType<IntLiteral>(assert.Actual).Value);
        Assert.Equal(2L, Assert.IsType<IntLiteral>(assert.Expected).Value);
    }

    [Fact]
    public void NumbersReportingDirectivesInSourceOrder()
    {
        var module = Source.Parse("#view 1:\n#assert(1, 1)\n#view 2:", BuildMode.Test);

        Assert.Collection(module.Statements,
            first => Assert.Equal(0, Assert.IsType<ViewDirective>(first).Id),
            second => Assert.Equal(1, Assert.IsType<AssertDirective>(second).Id),
            third => Assert.Equal(2, Assert.IsType<ViewDirective>(third).Id));
    }

    [Fact]
    public void DirectiveNamesAreNotKeywords()
    {
        var module = Source.Analyzed("let view i64: 1\nlet mock i64: 2\nlet assert i64: 3");

        Assert.Equal(3, module.Statements.Count);
    }

    // Reading back a line the compiler has already annotated.

    [Fact]
    public void ReparsesAnAnnotatedView()
    {
        var view = ParseSingle<ViewDirective>("#view 1: 42");

        Assert.Equal(1L, Assert.IsType<IntLiteral>(view.Subject).Value);
    }

    [Fact]
    public void ReparsesAnAnnotationThatIsNotEvenLexable()
    {
        // '1e+20' is how printf renders a large f64, and the literal scanner rejects it;
        // 'fail, got 2' is not an expression. Both must be discarded, not parsed.
        Assert.IsType<ViewDirective>(Assert.Single(Source.Parse("#view 1: 1e+20", BuildMode.Test).Statements));
        Assert.IsType<AssertDirective>(
            Assert.Single(Source.Parse("#assert(1, 2): fail, got 2", BuildMode.Test).Statements));
    }

    [Fact]
    public void AnAnnotationEndsAtTheLine()
    {
        var module = Source.Parse("#view 1: 42\nprintLn(9)", BuildMode.Test);

        Assert.Collection(module.Statements,
            first => Assert.IsType<ViewDirective>(first),
            second => Assert.IsType<ExpressionStatement>(second));
    }

    [Fact]
    public void AColonOnTheNextLineIsNotAnAnnotation()
    {
        var module = Source.Parse("let x i64: 1\n#view x\nx: 2", BuildMode.Test);

        Assert.Collection(module.Statements,
            first => Assert.IsType<LetStatement>(first),
            second => Assert.IsType<ViewDirective>(second),
            third => Assert.IsType<AssignmentStatement>(third));
    }

    // Parse diagnostics.

    [Fact]
    public void RejectsAnUnknownDirective()
    {
        Assert.Equal(
            $"{Source.Path}(1,2): unknown directive '#show'; expected '#mock', '#view' or '#assert'",
            ParseError("#show 1"));
    }

    [Fact]
    public void RejectsAnAssertThatIsNotAPair()
    {
        Assert.Equal(
            $"{Source.Path}(1,1): '#assert' expects 2 arguments, got 1",
            ParseError("#assert(1)"));
    }

    [Fact]
    public void RejectsADirectiveSpreadOverTwoLines()
    {
        // The continuation rule that joins '+ 1' to the line above is exactly what a
        // directive must not inherit — it is a comment as far as a production build knows.
        Assert.Equal(
            $"{Source.Path}(2,3): a directive must be written on one line; '#' is on line 1",
            ParseError("#view 1\n+ 1"));
    }

    // Semantic diagnostics.

    [Fact]
    public void MockReportsAnUnknownVariable()
    {
        Assert.Equal(
            $"{Source.Path}(1,7): unknown variable 'nope'",
            Assert.Single(Source.Analyze("#mock nope: 1", BuildMode.Test)));
    }

    [Fact]
    public void MockReportsATypeMismatchTheSameWayAnAssignmentDoes()
    {
        var mocked = Assert.Single(Source.Analyze("let x i64: 1\n#mock x: 1.5", BuildMode.Test));
        var assigned = Assert.Single(Source.Analyze("let x i64: 1\nx: 1.5"));

        Assert.Equal($"{Source.Path}(2,10): cannot assign a value of type 'f64' to 'x' of type 'i64'", mocked);
        Assert.Equal(assigned.Replace("(2,4)", "(2,10)"), mocked);
    }

    [Fact]
    public void MockObeysBlockScope()
    {
        Assert.Equal(
            $"{Source.Path}(4,7): unknown variable 'x'",
            Assert.Single(Source.Analyze("{\n  let x i64: 1\n}\n#mock x: 2", BuildMode.Test)));
    }

    [Fact]
    public void ViewRejectsAnUnprintableValue()
    {
        Assert.Equal(
            $"{Source.Path}(1,7): '#view' cannot show a value of type 'void'; expected 'bool', 'i64', 'f64'",
            Assert.Single(Source.Analyze("#view printLn(1):", BuildMode.Test)));
    }

    [Fact]
    public void AssertRejectsMismatchedOperands()
    {
        Assert.Equal(
            $"{Source.Path}(1,1): '#assert' cannot compare 'i64' with 'bool'",
            Assert.Single(Source.Analyze("#assert(1, true)", BuildMode.Test)));
    }

    [Fact]
    public void DirectivesAreTypedLikeAnyOtherExpression()
    {
        var module = Source.Analyzed("let x i64: 1\n#view x + 1:\n#assert(x, 1)", BuildMode.Test);

        var view = Assert.IsType<ViewDirective>(module.Statements[1]);
        Assert.Equal(SuruType.I64, view.Subject.Type);
    }

    // Dumps.

    [Fact]
    public void DumpsDirectivesWithTheirIds()
    {
        var module = Source.Analyzed("let x i64: 1\n#mock x: 2\n#view x:\n#assert(x, 2)", BuildMode.Test);

        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) x i64
                IntLiteral (1,12) 1
              MockDirective (2,1) x
                IntLiteral (2,10) 2
              ViewDirective (3,1) #0
                IdentifierExpression (3,7) x
              AssertDirective (4,1) #1
                IdentifierExpression (4,9) x
                IntLiteral (4,12) 2

            """,
            AstPrinter.Print(module));
    }

    [Fact]
    public void DumpsNoHashTokenInAProductionBuild()
    {
        Assert.DoesNotContain("Hash", TokenPrinter.Print("#view 1:", Source.Path));
        Assert.Contains("Hash", TokenPrinter.Print("#view 1:", Source.Path, BuildMode.Test));
    }

    private static T ParseSingle<T>(string text) where T : Statement =>
        Assert.IsType<T>(Source.Parse(text, BuildMode.Test).Statements[^1]);

    private static string ParseError(string text) =>
        Assert.Throws<ParseException>(() => Source.Parse(text, BuildMode.Test)).Message;
}
