using Suru.Compiler.Debug;
using Suru.Compiler.Parse;

namespace Suru.Tests.Compiler.Parse;

/// <summary>
/// <c>fn</c> declarations and <c>return</c> through the parser. Placement is a semantic rule,
/// so every shape here parses wherever it is written; what the analyzer makes of it is not
/// this class's concern.
/// </summary>
public class FunctionTests
{
    [Fact]
    public void ParsesAVoidFunctionWithNoParameters()
    {
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f void
                BlockStatement (1,13) function

            """,
            AstPrinter.Print(Source.Parse("fn f() void {}")));
    }

    [Fact]
    public void ParsesParametersAndAReturnedValue()
    {
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) add i64
                Parameter (1,8) a i64
                Parameter (1,15) b i64
                BlockStatement (1,26) function
                  ReturnStatement (2,3)
                    BinaryExpression (2,10) +
                      IdentifierExpression (2,10) a
                      IdentifierExpression (2,14) b

            """,
            AstPrinter.Print(Source.Parse("""
                fn add(a i64, b i64) i64 {
                  return a + b
                }
                """)));
    }

    [Fact]
    public void ParsesABareReturnBeforeAClosingBrace()
    {
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f void
                BlockStatement (1,13) function
                  ReturnStatement (1,15)

            """,
            AstPrinter.Print(Source.Parse("fn f() void { return }")));
    }

    [Fact]
    public void AValueOnTheNextLineIsNotReturned()
    {
        // The same-line rule: without it 'printLn(1)' would silently become the returned value.
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f void
                BlockStatement (1,13) function
                  ReturnStatement (2,3)
                  ExpressionStatement (3,3)
                    CallExpression (3,3) printLn
                      IntLiteral (3,11) 1

            """,
            AstPrinter.Print(Source.Parse("""
                fn f() void {
                  return
                  printLn(1)
                }
                """)));
    }

    [Fact]
    public void AReturnedValueContinuesOntoALineStartingWithAnOperator()
    {
        // The value began on the 'return' line, so the ordinary continuation rule applies.
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f i64
                Parameter (1,6) a i64
                Parameter (1,13) b i64
                BlockStatement (1,24) function
                  ReturnStatement (2,3)
                    BinaryExpression (2,10) +
                      IdentifierExpression (2,10) a
                      IdentifierExpression (3,7) b

            """,
            AstPrinter.Print(Source.Parse("""
                fn f(a i64, b i64) i64 {
                  return a
                    + b
                }
                """)));
    }

    [Fact]
    public void ParsesControlFlowInABody()
    {
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f i64
                Parameter (1,6) n i64
                BlockStatement (1,17) function
                  BlockStatement (2,3)
                  IfStatement (3,3)
                    IdentifierExpression (3,6) ready
                    BlockStatement (3,12) branch
                      ReturnStatement (3,14)
                        IntLiteral (3,21) 1
                    BlockStatement (3,30) branch
                      ReturnStatement (3,32)
                        IntLiteral (3,39) 2
                  WhileStatement (4,3)
                    BoolLiteral (4,9) true
                    BlockStatement (4,14) loop
                      BreakStatement (4,16)
                  ReturnStatement (5,3)
                    IdentifierExpression (5,10) n

            """,
            AstPrinter.Print(Source.Parse("""
                fn f(n i64) i64 {
                  {}
                  if ready { return 1 } else { return 2 }
                  while true { break }
                  return n
                }
                """)));
    }

    [Fact]
    public void ParsesANestedFunction()
    {
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) outer i64
                BlockStatement (1,16) function
                  FunctionDeclaration (2,3) inner i64
                    BlockStatement (2,18) function
                      ReturnStatement (2,20)
                        IntLiteral (2,27) 1
                  ReturnStatement (3,3)
                    CallExpression (3,10) inner

            """,
            AstPrinter.Print(Source.Parse("""
                fn outer() i64 {
                  fn inner() i64 { return 1 }
                  return inner()
                }
                """)));
    }

    [Fact]
    public void ParsesAFunctionAndAReturnWhereTheAnalyzerWillRejectThem()
    {
        // Where 'fn' and 'return' may appear is semantic, so both parse inside an 'if' arm.
        Assert.Equal(
            """
            Module test.suru
              IfStatement (1,1)
                BoolLiteral (1,4) true
                BlockStatement (1,9) branch
                  FunctionDeclaration (1,11) f void
                    BlockStatement (1,23) function
                  ReturnStatement (1,26)

            """,
            AstPrinter.Print(Source.Parse("if true { fn f() void {} return }")));
    }

    [Theory]
    [InlineData("fn f() { }", "test.suru(1,8): expected Identifier, got LeftBrace")]
    [InlineData("fn f a i64 { }", "test.suru(1,6): expected LeftParen, got Identifier")]
    [InlineData("fn f(a) i64 { }", "test.suru(1,7): expected Identifier, got RightParen")]
    [InlineData("fn f(a i64,) i64 { }", "test.suru(1,12): expected Identifier, got RightParen")]
    [InlineData("fn (a i64) i64 { }", "test.suru(1,4): expected Identifier, got LeftParen")]
    [InlineData("fn f() i64 return 1", "test.suru(1,12): expected LeftBrace, got Return")]
    public void ReportsMalformedDeclarations(string source, string expected)
    {
        var exception = Assert.Throws<ParseException>(() => Source.Parse(source));

        Assert.Equal(expected, exception.Message);
    }
}
