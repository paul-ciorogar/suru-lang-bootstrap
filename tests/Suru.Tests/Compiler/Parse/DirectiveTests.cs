using Suru.Compiler;
using Suru.Compiler.Debug;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests.Compiler.Parse;

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
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) x i64
                IntLiteral (1,12) 1

            """,
            AstPrinter.Print(Source.Parse("""
                let x i64: 1
                #mock x: 2
                #view x:
                #assert(x, 1): pass
                """)));
    }

    [Fact]
    public void ProductionIgnoresADirectiveItCouldNotParse()
    {
        // The whole point of skipping in the lexer: syntax this compiler does not know
        // still cannot break a release build.
        Assert.Equal(
            "Module test.suru\n",
            AstPrinter.Print(Source.Parse("#whatever <- this is not Suru at all")));
    }

    [Fact]
    public void ProductionStopsIgnoringAtTheEndOfTheLine()
    {
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (2,1)
                CallExpression (2,1) printLn
                  IntLiteral (2,9) 1

            """,
            AstPrinter.Print(Source.Parse("#view x:\nprintLn(1)")));
    }

    // Parsing, in test mode.

    [Fact]
    public void ParsesMock()
    {
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) x i64
                IntLiteral (1,12) 1
              MockDirective (2,1) x
                IntLiteral (2,10) 2

            """,
            Ast("let x i64: 1\n#mock x: 2"));
    }

    [Fact]
    public void PointsAMockAtTheNameItAssignsTo()
    {
        // Not in the dump, and the only position a mock's diagnostics are reported at.
        var mock = ParseSingle<MockDirective>("let x i64: 1\n#mock x: 2");

        Assert.Equal(7, mock.NamePosition.Column);
    }

    [Fact]
    public void ParsesViewOfAnExpression()
    {
        Assert.Equal(
            """
            Module test.suru
              ViewDirective (1,1) #0
                BinaryExpression (1,7) +
                  IntLiteral (1,7) 1
                  IntLiteral (1,11) 2

            """,
            Ast("#view 1 + 2:"));
    }

    [Fact]
    public void ParsesAssert()
    {
        Assert.Equal(
            """
            Module test.suru
              AssertDirective (1,1) #0
                IntLiteral (1,9) 1
                IntLiteral (1,12) 2

            """,
            Ast("#assert(1, 2)"));
    }

    [Fact]
    public void NumbersReportingDirectivesInSourceOrder()
    {
        Assert.Equal(
            """
            Module test.suru
              ViewDirective (1,1) #0
                IntLiteral (1,7) 1
              AssertDirective (2,1) #1
                IntLiteral (2,9) 1
                IntLiteral (2,12) 1
              ViewDirective (3,1) #2
                IntLiteral (3,7) 2

            """,
            Ast("#view 1:\n#assert(1, 1)\n#view 2:"));
    }

    [Fact]
    public void DirectiveNamesAreNotKeywords()
    {
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) view i64
                IntLiteral (1,15) 1
              LetStatement (2,1) mock i64
                IntLiteral (2,15) 2
              LetStatement (3,1) assert i64
                IntLiteral (3,17) 3

            """,
            AstPrinter.Print(Source.Analyzed("let view i64: 1\nlet mock i64: 2\nlet assert i64: 3")));
    }

    // Reading back a line the compiler has already annotated.

    [Fact]
    public void ReparsesAnAnnotatedView()
    {
        Assert.Equal(
            """
            Module test.suru
              ViewDirective (1,1) #0
                IntLiteral (1,7) 1

            """,
            Ast("#view 1: 42"));
    }

    [Fact]
    public void ReparsesAnAnnotationThatIsNotEvenLexable()
    {
        // '1e+20' is how printf renders a large f64, and the literal scanner rejects it;
        // 'fail, got 2' is not an expression. Both must be discarded, not parsed.
        Assert.Equal(
            """
            Module test.suru
              ViewDirective (1,1) #0
                IntLiteral (1,7) 1

            """,
            Ast("#view 1: 1e+20"));

        Assert.Equal(
            """
            Module test.suru
              AssertDirective (1,1) #0
                IntLiteral (1,9) 1
                IntLiteral (1,12) 2

            """,
            Ast("#assert(1, 2): fail, got 2"));
    }

    [Fact]
    public void AnAnnotationEndsAtTheLine()
    {
        Assert.Equal(
            """
            Module test.suru
              ViewDirective (1,1) #0
                IntLiteral (1,7) 1
              ExpressionStatement (2,1)
                CallExpression (2,1) printLn
                  IntLiteral (2,9) 9

            """,
            Ast("#view 1: 42\nprintLn(9)"));
    }

    [Fact]
    public void AnAssertOwnsTheRestOfItsLine()
    {
        // The ')' ends the directive, so what follows it is thrown away whether or not a run
        // has annotated the line yet.
        Assert.Equal(
            """
            Module test.suru
              AssertDirective (1,1) #0
                IntLiteral (1,9) 1
                IntLiteral (1,12) 1

            """,
            Ast("#assert(1, 1) #view 1:"));
    }

    [Fact]
    public void AnAssertDiscardsTextThatIsNotEvenLexable()
    {
        Assert.Equal(
            """
            Module test.suru
              AssertDirective (1,1) #0
                IntLiteral (1,9) 1
                IntLiteral (1,12) 1

            """,
            Ast("#assert(1, 1) <- this is not Suru at all"));
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

    [Fact]
    public void AViewOwnsTheRestOfItsLine()
    {
        // A directive ends at its terminator, so the second '#' here is text the first one
        // discarded rather than a directive of its own.
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) a i64
                IntLiteral (1,12) 1
              ViewDirective (2,1) #0
                IdentifierExpression (2,7) a

            """,
            Ast("let a i64: 1\n#view a: #view b:"));
    }

    [Fact]
    public void RejectsAViewWithNoColon()
    {
        // The colon is where the run writes the value; without one the '#view' has nowhere
        // to report to. The ':' on the next line belongs to the assignment.
        Assert.Equal(
            $"{Source.Path}(3,1): expected Colon, got Identifier",
            ParseError("let x i64: 1\n#view x\nx: 2"));
    }

    [Fact]
    public void RejectsAMockFollowedByAnythingElseOnTheLine()
    {
        // After a mock's colon Suru expects an expression and nothing else — a mock reports
        // nothing, so it has no annotation for the rest of the line to be part of.
        Assert.Equal(
            $"{Source.Path}(2,12): a directive ends at the end of its line",
            ParseError("let x i64: 1\n#mock x: 1 #view b:"));
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
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) x i64
                IntLiteral (1,12) 1 : i64
              ViewDirective (2,1) #0
                BinaryExpression (2,7) + : i64
                  IdentifierExpression (2,7) x : i64
                  IntLiteral (2,11) 1 : i64
              AssertDirective (3,1) #1
                IdentifierExpression (3,9) x : i64
                IntLiteral (3,12) 1 : i64

            """,
            AstPrinter.Print(
                Source.Analyzed("let x i64: 1\n#view x + 1:\n#assert(x, 1)", BuildMode.Test), withTypes: true));
    }

    // Dumps.

    [Fact]
    public void DumpsNoHashTokenInAProductionBuild()
    {
        Assert.DoesNotContain("Hash", TokenPrinter.Print("#view 1:", Source.Path));
        Assert.Contains("Hash", TokenPrinter.Print("#view 1:", Source.Path, BuildMode.Test));
    }

    [Fact]
    public void AMockInsideAnArmSeesTheArmsScope()
    {
        Assert.Empty(Source.Analyze("let seed i64: 7\nif true {\n#mock seed: 1\n}", BuildMode.Test));
    }

    [Fact]
    public void ADirectiveInAnArmObeysTheSameRules()
    {
        // Nesting changes nothing about what a directive accepts.
        Assert.Equal(
            "test.suru(2,7): '#view' cannot show a value of type 'void'; expected 'bool', 'i64', 'f64'",
            Assert.Single(Source.Analyze("if true {\n#view printLn(1):\n}", BuildMode.Test)));
    }

    /// <summary>The parse tree of a test-mode build, as <see cref="AstPrinter"/> renders it.</summary>
    private static string Ast(string text) => AstPrinter.Print(Source.Parse(text, BuildMode.Test));

    private static T ParseSingle<T>(string text) where T : Statement =>
        Assert.IsType<T>(Source.Parse(text, BuildMode.Test).Statements[^1]);

    private static string ParseError(string text) =>
        Assert.Throws<ParseException>(() => Source.Parse(text, BuildMode.Test)).Message;
}
