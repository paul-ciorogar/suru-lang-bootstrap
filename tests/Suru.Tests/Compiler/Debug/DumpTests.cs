using Suru.Compiler.Debug;

namespace Suru.Tests.Compiler.Debug;

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
        var dump = AstPrinter.Print(
            Source.Analyzed(
                """
                fn main() void {
                  printLn(true)
                }
                """),
            withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) main void : void
                BlockStatement (1,16) function
                  ExpressionStatement (2,3)
                    CallExpression (2,3) printLn : void
                      BoolLiteral (2,11) true : bool

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
    public void UnresolvedFunctionAndParameterTypesAreMarked()
    {
        // A declaration and its parameters carry a resolved type the way an expression does.
        var dump = AstPrinter.Print(Source.Parse("fn f(a i64) i64 { return a }"), withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f i64 : ?
                Parameter (1,6) a i64 : ?
                BlockStatement (1,17) function
                  ReturnStatement (1,19)
                    IdentifierExpression (1,26) a : ?

            """,
            dump);
    }

    /// <summary>
    /// The twin of <see cref="UnresolvedFunctionAndParameterTypesAreMarked"/>: the same dump once
    /// the analyzer has run, which is where a return type, a parameter's type and a call's type
    /// all become visible. The two differ only in the annotations, since analysis annotates the
    /// tree in place.
    /// </summary>
    [Fact]
    public void DumpsAFunctionWithItsResolvedTypes()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed(
                """
                fn f(a i64, b f64) i64 {
                  return a
                }
                fn main() void {
                  printLn(f(1, 2.5))
                }
                """),
            withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) f i64 : i64
                Parameter (1,6) a i64 : i64
                Parameter (1,13) b f64 : f64
                BlockStatement (1,24) function
                  ReturnStatement (2,3)
                    IdentifierExpression (2,10) a : i64
              FunctionDeclaration (4,1) main void : void
                BlockStatement (4,16) function
                  ExpressionStatement (5,3)
                    CallExpression (5,3) printLn : void
                      CallExpression (5,11) f : i64
                        IntLiteral (5,13) 1 : i64
                        FloatLiteral (5,16) 2.5 : f64

            """,
            dump);
    }

    [Fact]
    public void DumpsBindingsOperatorsAndVariableUses()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed(
                """
                fn main() void {
                  let total i64: 1 + 2
                  total: -total
                  printLn(not false)
                }
                """),
            withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) main void : void
                BlockStatement (1,16) function
                  LetStatement (2,3) total i64
                    BinaryExpression (2,18) + : i64
                      IntLiteral (2,18) 1 : i64
                      IntLiteral (2,22) 2 : i64
                  AssignmentStatement (3,3) total
                    UnaryExpression (3,10) - : i64
                      IdentifierExpression (3,11) total : i64
                  ExpressionStatement (4,3)
                    CallExpression (4,3) printLn : void
                      UnaryExpression (4,11) not : bool
                        BoolLiteral (4,15) false : bool

            """,
            dump);
    }

    [Fact]
    public void DumpsABlockWithItsStatementsIndentedOneLevel()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed(
                """
                fn main() void {
                  let x i64: 1
                  {
                    let x f64: 1.5
                    printLn(x)
                  }
                  printLn(x)
                }
                """),
            withTypes: true);

        // The two 'x' uses differ only in type: the inner one resolves to the shadow.
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) main void : void
                BlockStatement (1,16) function
                  LetStatement (2,3) x i64
                    IntLiteral (2,14) 1 : i64
                  BlockStatement (3,3)
                    LetStatement (4,5) x f64
                      FloatLiteral (4,16) 1.5 : f64
                    ExpressionStatement (5,5)
                      CallExpression (5,5) printLn : void
                        IdentifierExpression (5,13) x : f64
                  ExpressionStatement (7,3)
                    CallExpression (7,3) printLn : void
                      IdentifierExpression (7,11) x : i64

            """,
            dump);
    }

    [Fact]
    public void DumpsAnIfWithItsConditionAndArms()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed(
                """
                fn main() void {
                  let x i64: 1
                  if x > 0 {
                    printLn(x)
                  } else {
                    printLn(0)
                  }
                }
                """),
            withTypes: true);

        // The condition types as 'bool' although both its operands are 'i64' — that is what
        // makes it a condition at all.
        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) main void : void
                BlockStatement (1,16) function
                  LetStatement (2,3) x i64
                    IntLiteral (2,14) 1 : i64
                  IfStatement (3,3)
                    BinaryExpression (3,6) > : bool
                      IdentifierExpression (3,6) x : i64
                      IntLiteral (3,10) 0 : i64
                    BlockStatement (3,12) branch
                      ExpressionStatement (4,5)
                        CallExpression (4,5) printLn : void
                          IdentifierExpression (4,13) x : i64
                    BlockStatement (5,10) branch
                      ExpressionStatement (6,5)
                        CallExpression (6,5) printLn : void
                          IntLiteral (6,13) 0 : i64

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
