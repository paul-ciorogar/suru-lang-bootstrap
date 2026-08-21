using Suru.Compiler.Debug;
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
    public void AnIdentifierWithoutParensIsAVariableAndNotACall()
    {
        // A call is an identifier followed by '('; without one this is two
        // statements, both rejected later by semantic analysis.
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                IdentifierExpression (1,1) printLn
              ExpressionStatement (1,9)
                IntLiteral (1,9) 1

            """,
            AstPrinter.Print(Source.Parse("printLn 1")));
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
        var exception = Assert.Throws<ParseException>(() => Source.Parse(", 1"));

        Assert.Equal("test.suru(1,1): unexpected token Comma", exception.Message);
    }

    [Fact]
    public void FoldsBinaryOperatorsLeftToRightWithNoPrecedence()
    {
        // '1 + 2 * 3' is '(1 + 2) * 3': Suru has no precedence.
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                BinaryExpression (1,1) *
                  BinaryExpression (1,1) +
                    IntLiteral (1,1) 1
                    IntLiteral (1,5) 2
                  IntLiteral (1,9) 3

            """,
            AstPrinter.Print(Source.Parse("1 + 2 * 3")));
    }

    [Fact]
    public void ParenthesesRegroupAnExpression()
    {
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                BinaryExpression (1,1) +
                  IntLiteral (1,1) 1
                  BinaryExpression (1,6) *
                    IntLiteral (1,6) 2
                    IntLiteral (1,10) 3

            """,
            AstPrinter.Print(Source.Parse("1 + (2 * 3)")));
    }

    [Fact]
    public void ParsesAParenthesizedExpressionAsAStatement()
    {
        Assert.Equal(1L, Assert.IsType<IntLiteral>(Source.SingleExpression(Source.Parse("(1)"))).Value);
    }

    [Fact]
    public void ABinaryExpressionTakesThePositionOfItsLeftOperand()
    {
        Assert.Equal(new SourcePosition(1, 1), ParseSingleBinary("1 + 2").Position);
    }

    [Theory]
    [InlineData("-count", UnaryOperator.Negate)]
    [InlineData("not true", UnaryOperator.Not)]
    public void ParsesPrefixOperators(string text, UnaryOperator op)
    {
        var unary = Assert.IsType<UnaryExpression>(Source.SingleExpression(Source.Parse(text)));

        Assert.Equal(op, unary.Operator);
        Assert.Equal(new SourcePosition(1, 1), unary.Position);
    }

    [Fact]
    public void PrefixOperatorsBindTighterThanBinaryOnes()
    {
        // '-count + 2' negates only the count.
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                BinaryExpression (1,1) +
                  UnaryExpression (1,1) -
                    IdentifierExpression (1,2) count
                  IntLiteral (1,10) 2

            """,
            AstPrinter.Print(Source.Parse("-count + 2")));
    }

    [Theory]
    [InlineData("-1", -1L)]
    [InlineData("- 1", -1L)]                               // the fold is over tokens, not characters
    [InlineData("-0xff", -255L)]
    [InlineData("-9223372036854775808", long.MinValue)]    // only writable as a signed literal
    public void ASignInFrontOfANumberIsPartOfTheLiteral(string text, long value)
    {
        var literal = Assert.IsType<IntLiteral>(Source.SingleExpression(Source.Parse(text)));

        Assert.Equal(value, literal.Value);
        // The literal starts at the sign.
        Assert.Equal(new SourcePosition(1, 1), literal.Position);
    }

    [Fact]
    public void ASignInFrontOfAFloatIsPartOfTheLiteralToo()
    {
        Assert.Equal(-1.5, Assert.IsType<FloatLiteral>(Source.SingleExpression(Source.Parse("-1.5"))).Value);
    }

    [Fact]
    public void AMinusBetweenOperandsStillSubtracts()
    {
        // Only a prefix '-' folds — the one in '1 - 2' is consumed as a binary operator
        // before the right operand is parsed, and so is the one in '1 -2'.
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                BinaryExpression (1,1) -
                  IntLiteral (1,1) 1
                  IntLiteral (1,4) 2

            """,
            AstPrinter.Print(Source.Parse("1 -2")));
    }

    [Fact]
    public void RejectsTheNegatedMinimumWithoutItsSign()
    {
        // The lexer decodes an unsigned magnitude, so this one is in range only when the
        // '-' folds in; the parser is the stage that knows it did not.
        var exception = Assert.Throws<ParseException>(() => Source.Parse("printLn(9223372036854775808)"));

        Assert.Equal($"{Source.Path}(1,9): integer literal is out of range for 'i64'", exception.Message);
    }

    [Fact]
    public void ParsesALetBinding()
    {
        // The type's own position is not in the dump; it is covered by the
        // 'unknown type' diagnostic in SemanticTests.
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) count i64
                IntLiteral (1,16) 1

            """,
            AstPrinter.Print(Source.Parse("let count i64: 1")));
    }

    [Fact]
    public void ParsesAnAssignment()
    {
        Assert.Equal(
            """
            Module test.suru
              AssignmentStatement (1,1) count
                IntLiteral (1,8) 2

            """,
            AstPrinter.Print(Source.Parse("count: 2")));
    }

    [Fact]
    public void ReportsAMissingColonInALetBinding()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("let count i64 1"));

        Assert.Equal("test.suru(1,15): expected Colon, got IntLiteral", exception.Message);
    }

    [Fact]
    public void AnOperatorOnTheNextLineContinuesTheExpression()
    {
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) sum i64
                BinaryExpression (1,14) +
                  IntLiteral (1,14) 1
                  IntLiteral (2,5) 2

            """,
            AstPrinter.Print(Source.Parse("let sum i64: 1\n  + 2")));
    }

    [Fact]
    public void AnythingElseOnTheNextLineStartsANewStatement()
    {
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) sum i64
                IntLiteral (1,14) 1
              ExpressionStatement (2,1)
                CallExpression (2,1) printLn
                  IdentifierExpression (2,9) sum

            """,
            AstPrinter.Print(Source.Parse("let sum i64: 1\nprintLn(sum)")));
    }

    [Fact]
    public void ParsesAnEmptyBlock()
    {
        var block = Assert.IsType<BlockStatement>(Assert.Single(Source.Parse("{}").Statements));

        Assert.Empty(block.Statements);
        Assert.Equal(new SourcePosition(1, 1), block.Position);
    }

    [Fact]
    public void ParsesTheStatementsInsideABlock()
    {
        var block = Assert.IsType<BlockStatement>(Assert.Single(Source.Parse("{ printLn(1) printLn(2) }").Statements));

        Assert.Collection(block.Statements,
            statement => AssertCall(statement, "printLn", 1),
            statement => AssertCall(statement, "printLn", 2));
    }

    [Fact]
    public void ParsesABlockAmongTopLevelStatements()
    {
        Assert.Equal(
            """
            Module test.suru
              ExpressionStatement (1,1)
                CallExpression (1,1) printLn
                  IntLiteral (1,9) 1
              BlockStatement (2,1)
                ExpressionStatement (2,3)
                  CallExpression (2,3) printLn
                    IntLiteral (2,11) 2
              ExpressionStatement (3,1)
                CallExpression (3,1) printLn
                  IntLiteral (3,9) 3

            """,
            AstPrinter.Print(Source.Parse("printLn(1)\n{ printLn(2) }\nprintLn(3)")));
    }

    [Fact]
    public void ParsesNestedBlocks()
    {
        Assert.Equal(
            """
            Module test.suru
              BlockStatement (1,1)
                BlockStatement (1,3)
                  BlockStatement (1,5)
                    ExpressionStatement (1,7)
                      CallExpression (1,7) printLn
                        IntLiteral (1,15) 1

            """,
            AstPrinter.Print(Source.Parse("{ { { printLn(1) } } }")));
    }

    [Fact]
    public void ReportsAnUnterminatedBlock()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("{ printLn(1)"));

        Assert.Equal("test.suru(1,13): expected RightBrace, got Eof", exception.Message);
    }

    [Fact]
    public void ReportsAClosingBraceWithNoBlock()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("printLn(1) }"));

        Assert.Equal("test.suru(1,12): unexpected token RightBrace", exception.Message);
    }

    [Fact]
    public void ABlockOnTheNextLineDoesNotContinueAnExpression()
    {
        // Only a binary operator continues an expression, and '{' is not one.
        Assert.Equal(
            """
            Module test.suru
              LetStatement (1,1) sum i64
                IntLiteral (1,14) 1
              BlockStatement (2,1)

            """,
            AstPrinter.Print(Source.Parse("let sum i64: 1\n{}")));
    }

    [Fact]
    public void ParsesAnIfWithoutAnElse()
    {
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                BoolLiteral (1,4) true
                BlockStatement (1,9)
                  ExpressionStatement (1,11)
                    CallExpression (1,11) printLn
                      IntLiteral (1,19) 1

            """,
            AstPrinter.Print(Source.Parse("if true { printLn(1) }")));
    }

    [Fact]
    public void ParsesAnIfWithAnElse()
    {
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                BoolLiteral (1,4) true
                BlockStatement (1,9)
                  ExpressionStatement (1,11)
                    CallExpression (1,11) printLn
                      IntLiteral (1,19) 1
                BlockStatement (1,29)
                  ExpressionStatement (1,31)
                    CallExpression (1,31) printLn
                      IntLiteral (1,39) 2

            """,
            AstPrinter.Print(Source.Parse("if true { printLn(1) } else { printLn(2) }")));
    }

    [Fact]
    public void ElseIfIsAnElseWhoseBodyIsAnIf()
    {
        // The nesting is the whole of what 'else if' is, and it is what makes the trailing
        // 'else' belong to the inner 'if' rather than the outer one.
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                IdentifierExpression (1,4) a
                BlockStatement (1,6)
                IfStatement (1,14)
                  IdentifierExpression (1,17) b
                  BlockStatement (1,19)
                  BlockStatement (1,27)

            """,
            AstPrinter.Print(Source.Parse("if a {} else if b {} else {}")));
    }

    [Fact]
    public void ParsesEmptyArms()
    {
        var branch = Assert.IsType<IfStatement>(Assert.Single(Source.Parse("if true {} else {}").Statements));

        Assert.Empty(branch.Then.Statements);
        Assert.Empty(Assert.IsType<BlockStatement>(branch.Else).Statements);
    }

    [Fact]
    public void AnIfBodyMustBeABlock()
    {
        // There is no single-statement form, which is why there is no dangling 'else' to
        // have a rule about.
        var exception = Assert.Throws<ParseException>(() => Source.Parse("if true printLn(1)"));

        Assert.Equal("test.suru(1,9): expected LeftBrace, got Identifier", exception.Message);
    }

    [Fact]
    public void AnElseBodyMustBeABlockOrAnIf()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("if true {} else printLn(1)"));

        Assert.Equal("test.suru(1,17): expected LeftBrace, got Identifier", exception.Message);
    }

    [Fact]
    public void ReportsAMissingCondition()
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse("if { }"));

        Assert.Equal("test.suru(1,4): unexpected token LeftBrace", exception.Message);
    }

    [Fact]
    public void AConditionContinuesOntoTheNextLineOnAnOperator()
    {
        // The condition is an ordinary expression, so the ordinary continuation rule applies.
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                BinaryExpression (1,4) >
                  IdentifierExpression (1,4) x
                  IntLiteral (2,3) 1
                BlockStatement (2,5)

            """,
            AstPrinter.Print(Source.Parse("if x\n> 1 {\n}")));
    }

    [Fact]
    public void ABraceOnTheNextLineStillOpensTheBody()
    {
        // The counterpart of ABlockOnTheNextLineDoesNotContinueAnExpression: after a 'let' a
        // lone '{}' is a separate statement, but after a condition it is the body, because an
        // 'if' requires one.
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                IdentifierExpression (1,4) x
                BlockStatement (2,1)

            """,
            AstPrinter.Print(Source.Parse("if x\n{\n}")));
    }

    [Fact]
    public void AnElseNeedNotShareALineWithItsBrace()
    {
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                IdentifierExpression (1,4) a
                BlockStatement (1,6)
                BlockStatement (3,6)

            """,
            AstPrinter.Print(Source.Parse("if a {\n}\nelse {\n}")));
    }

    private static BinaryExpression ParseSingleBinary(string text) =>
        Assert.IsType<BinaryExpression>(Source.SingleExpression(Source.Parse(text)));

    private static CallExpression ParseSingleCall(string text) =>
        Assert.IsType<CallExpression>(Source.SingleExpression(Source.Parse(text)));

    private static void AssertCall(Statement statement, string name, long argument)
    {
        var call = Assert.IsType<CallExpression>(Assert.IsType<ExpressionStatement>(statement).Expression);
        Assert.Equal(name, call.Name);
        Assert.Equal(argument, Assert.IsType<IntLiteral>(Assert.Single(call.Args)).Value);
    }
}
