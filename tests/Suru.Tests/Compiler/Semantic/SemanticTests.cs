using Suru.Compiler;
using Suru.Compiler.Debug;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests.Compiler.Semantic;

public class SemanticTests
{
    [Theory]
    [InlineData("fn main() void { printLn(true) }")]
    [InlineData("fn main() void { printLn(false) }")]
    [InlineData("fn main() void { printLn(1) }")]
    [InlineData("fn main() void { printLn(1.2) }")]
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
    [InlineData("fn main() void { printLn(true) }", "bool")]
    [InlineData("fn main() void { printLn(1) }", "i64")]
    [InlineData("fn main() void { printLn(1.2) }", "f64")]
    public void AnnotatesTheArgumentWithItsType(string text, string typeName)
    {
        var call = Assert.IsType<CallExpression>(
            Source.SingleExpression(Source.MainBody(Source.Analyzed(text))));

        Assert.Equal(new SuruType(typeName), Assert.Single(call.Args).Type);
    }

    [Fact]
    public void AnnotatesAPrintLnCallAsVoid()
    {
        var call = Source.SingleExpression(
            Source.MainBody(Source.Analyzed("fn main() void { printLn(1) }")));

        Assert.Equal(SuruType.Void, call.Type);
    }

    [Fact]
    public void ReportsUnknownFunction()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              printLn(1)
              foo(2)
            }
            """));

        Assert.Equal("test.suru(3,3): unknown function 'foo'", error);
    }

    [Fact]
    public void ReportsWrongArity()
    {
        var errors = Source.Analyze(
            """
            fn main() void {
              printLn(1, 2)
              printLn()
            }
            """);

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(2,3): 'printLn' expects 1 argument, got 2", error),
            error => Assert.Equal("test.suru(3,3): 'printLn' expects 1 argument, got 0", error));
    }

    [Fact]
    public void ReportsUnprintableArgumentAtTheArgumentPosition()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              printLn(printLn(1))
            }
            """));

        Assert.Equal(
            "test.suru(2,11): 'printLn' cannot print a value of type 'void'; expected 'bool', 'i64', 'f64'",
            error);
    }

    [Fact]
    public void ReportsNonCallStatement()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              42
            }
            """));

        Assert.Equal("test.suru(2,3): only call expressions are allowed as statements", error);
    }

    [Fact]
    public void AnUnknownFunctionArgumentReportsOnlyOneError()
    {
        // The argument's type is null, so the printability check stays quiet
        // rather than piling a second error onto the same expression.
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              printLn(foo())
            }
            """));

        Assert.Equal("test.suru(2,11): unknown function 'foo'", error);
    }

    [Theory]
    [InlineData("fn main() void { printLn(1 + 2) }", "i64")]
    [InlineData("fn main() void { printLn(1.0 * 2.0) }", "f64")]
    [InlineData("fn main() void { printLn(1 < 2) }", "bool")]
    [InlineData("fn main() void { printLn(1.0 <> 2.0) }", "bool")]
    [InlineData("fn main() void { printLn(true and false) }", "bool")]
    [InlineData("fn main() void { printLn(true = false) }", "bool")]
    [InlineData("fn main() void { printLn(-1) }", "i64")]
    [InlineData("fn main() void { printLn(not true) }", "bool")]
    public void AnnotatesAnOperatorWithItsResultType(string text, string typeName)
    {
        var call = Assert.IsType<CallExpression>(
            Source.SingleExpression(Source.MainBody(Source.Analyzed(text))));

        Assert.Equal(new SuruType(typeName), Assert.Single(call.Args).Type);
    }

    [Fact]
    public void AnnotatesAVariableWithTheTypeItWasBoundAt()
    {
        var module = Source.Analyzed(
            """
            fn main() void {
              let count i64: 1
              printLn(count)
            }
            """);

        Assert.Equal(SuruType.I64, PrintedArgument(Source.MainBody(module)[1]).Type);
    }

    [Fact]
    public void AcceptsAnAssignmentOfTheBoundType()
    {
        Assert.Empty(Source.Analyze(
            """
            fn main() void {
              let count i64: 1
              count: count + 1
            }
            """));
    }

    [Theory]
    [InlineData("fn main() void { printLn(1 + 1.5) }", "test.suru(1,26): operator '+' cannot be applied to 'i64' and 'f64'")]
    [InlineData("fn main() void { printLn(true = 1) }", "test.suru(1,26): operator '=' cannot be applied to 'bool' and 'i64'")]
    [InlineData("fn main() void { printLn(true < false) }", "test.suru(1,26): operator '<' cannot be applied to 'bool' and 'bool'")]
    [InlineData("fn main() void { printLn(1 and 2) }", "test.suru(1,26): operator 'and' cannot be applied to 'i64' and 'i64'")]
    [InlineData("fn main() void { printLn(not 1) }", "test.suru(1,26): operator 'not' cannot be applied to 'i64'")]
    [InlineData("fn main() void { printLn(-true) }", "test.suru(1,26): operator '-' cannot be applied to 'bool'")]
    public void ReportsAnOperatorAppliedToTheWrongTypes(string text, string expected)
    {
        Assert.Equal(expected, Assert.Single(Source.Analyze(text)));
    }

    [Fact]
    public void AnOperandThatFailedReportsOnlyOneError()
    {
        // The left operand has no type, so the operator check stays quiet rather
        // than piling a second error onto the same expression.
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              printLn(missing + 1)
            }
            """));

        Assert.Equal("test.suru(2,11): unknown variable 'missing'", error);
    }

    [Fact]
    public void ReportsAnUnknownTypeAtTheTypeName()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let count int: 1
            }
            """));

        Assert.Equal("test.suru(2,13): unknown type 'int'", error);
    }

    [Fact]
    public void ReportsVoidAsUnbindable()
    {
        // Not 'unknown type': 'void' is the type of a printLn call and the compiler knows it
        // perfectly well. What is wrong is binding to it — which is why a function may write
        // 'void' as its return type and nothing else may write it at all.
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let nothing void: printLn(1)
            }
            """));

        Assert.Equal("test.suru(2,15): nothing can be bound to 'void'", error);
    }

    [Fact]
    public void ReportsARedeclaredBinding()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let count i64: 1
              let count i64: 2
            }
            """));

        Assert.Equal("test.suru(3,3): 'count' is already declared", error);
    }

    [Fact]
    public void ReportsABindingWhoseValueHasTheWrongType()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let count i64: 1.5
            }
            """));

        Assert.Equal("test.suru(2,18): cannot bind a value of type 'f64' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ReportsAnAssignmentOfTheWrongType()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let count i64: 1
              count: true
            }
            """));

        Assert.Equal("test.suru(3,10): cannot assign a value of type 'bool' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ReportsAnUnknownVariable()
    {
        var errors = Source.Analyze(
            """
            fn main() void {
              printLn(count)
              count: 1
            }
            """);

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(2,11): unknown variable 'count'", error),
            error => Assert.Equal("test.suru(3,3): unknown variable 'count'", error));
    }

    [Fact]
    public void ABindingWithABadValueIsStillDeclared()
    {
        // One error for the initialiser; the later use resolves rather than
        // reporting 'unknown variable' as well.
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let count i64: 1.5
              printLn(count)
            }
            """));

        Assert.Equal("test.suru(2,18): cannot bind a value of type 'f64' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ReportsAVariableUsedBeforeItIsBound()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              printLn(count)
              let count i64: 1
            }
            """));

        Assert.Equal("test.suru(2,11): unknown variable 'count'", error);
    }

    [Fact]
    public void ABindingInsideABlockShadowsAnOuterOne()
    {
        var module = Source.Analyzed(
            """
            fn main() void {
              let x i64: 1
              {
                let x f64: 1.5
                printLn(x)
              }
            }
            """);
        var block = Assert.IsType<BlockStatement>(Source.MainBody(module)[1]);

        Assert.Equal(SuruType.F64, PrintedArgument(block.Statements[1]).Type);
    }

    [Fact]
    public void TheOuterBindingComesBackAfterTheBlock()
    {
        var module = Source.Analyzed(
            """
            fn main() void {
              let x i64: 1
              {
                let x f64: 1.5
              }
              printLn(x)
            }
            """);

        Assert.Equal(SuruType.I64, PrintedArgument(Source.MainBody(module)[2]).Type);
    }

    [Fact]
    public void ABindingDoesNotEscapeItsBlock()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              {
                let x i64: 1
              }
              printLn(x)
            }
            """));

        Assert.Equal("test.suru(5,11): unknown variable 'x'", error);
    }

    [Fact]
    public void ReportsARedeclarationWithinTheSameBlock()
    {
        // Shadowing crosses nesting levels; it does not license rebinding in one scope.
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              {
                let x i64: 1
                let x i64: 2
              }
            }
            """));

        Assert.Equal("test.suru(4,5): 'x' is already declared", error);
    }

    [Fact]
    public void ABlockCanBindANameThatIsFreeOutsideIt()
    {
        Assert.Empty(Source.Analyze(
            """
            fn main() void {
              {
                let x i64: 1
                printLn(x)
              }
              {
                let x i64: 2
                printLn(x)
              }
            }
            """));
    }

    [Fact]
    public void ABlockCanReadAndAssignAnOuterBinding()
    {
        Assert.Empty(Source.Analyze(
            """
            fn main() void {
              let count i64: 1
              {
                count: count + 1
              }
              printLn(count)
            }
            """));
    }

    [Fact]
    public void AnAssignmentInsideABlockStillChecksTheOuterType()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn main() void {
              let count i64: 1
              {
                count: true
              }
            }
            """));

        Assert.Equal("test.suru(4,12): cannot assign a value of type 'bool' to 'count' of type 'i64'", error);
    }

    [Fact]
    public void ABlockReportsTheErrorsInsideIt()
    {
        var errors = Source.Analyze(
            """
            fn main() void {
              {
                printLn(missing)
                1
              }
            }
            """);

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(3,13): unknown variable 'missing'", error),
            error => Assert.Equal("test.suru(4,5): only call expressions are allowed as statements", error));
    }

    [Theory]
    [InlineData("fn main() void { if true { } }")]
    [InlineData("fn main() void { if not false { } }")]
    [InlineData("fn main() void {\n  let x i64: 1\n  if x = 1 { } else { }\n}")]
    [InlineData("fn main() void {\n  let ok bool: true\n  if ok { } else if not ok { } else { }\n}")]
    public void AcceptsABooleanCondition(string text)
    {
        Assert.Empty(Source.Analyze(text));
    }

    [Theory]
    [InlineData("fn main() void { if 1 { } }", "i64")]
    [InlineData("fn main() void { if 1.5 { } }", "f64")]
    [InlineData("fn main() void { if printLn(1) { } }", "void")]
    public void ReportsANonBooleanCondition(string text, string typeName)
    {
        // Nothing converts implicitly, so a number is not a truth value.
        Assert.Equal(
            $"test.suru(1,21): 'if' cannot branch on a value of type '{typeName}'; expected 'bool'",
            Assert.Single(Source.Analyze(text)));
    }

    [Fact]
    public void SaysNothingMoreWhenTheConditionAlreadyFailed()
    {
        Assert.Equal("test.suru(2,6): unknown variable 'missing'",
            Assert.Single(Source.Analyze(
                """
                fn main() void {
                  if missing { }
                }
                """)));
    }

    [Fact]
    public void ChecksTheConditionOfEveryArmInAChain()
    {
        Assert.Equal("test.suru(2,23): 'if' cannot branch on a value of type 'i64'; expected 'bool'",
            Assert.Single(Source.Analyze(
                """
                fn main() void {
                  if true { } else if 1 { }
                }
                """)));
    }

    [Fact]
    public void EachArmIsItsOwnScope()
    {
        // Both arms bind 'x', which is only legal if neither can see the other's.
        Assert.Equal("test.suru(7,11): unknown variable 'x'",
            Assert.Single(Source.Analyze(
                """
                fn main() void {
                  if true {
                    let x i64: 1
                  } else {
                    let x f64: 1.5
                  }
                  printLn(x)
                }
                """)));
    }

    [Fact]
    public void ReportsTheErrorsInBothArms()
    {
        var errors = Source.Analyze(
            """
            fn main() void {
              if true {
                foo()
              } else {
                bar()
              }
            }
            """);

        Assert.Collection(errors,
            error => Assert.Equal("test.suru(3,5): unknown function 'foo'", error),
            error => Assert.Equal("test.suru(5,5): unknown function 'bar'", error));
    }

    [Theory]
    [InlineData("fn main() void { while 1 { } }", "i64")]
    [InlineData("fn main() void { while 1.5 { } }", "f64")]
    [InlineData("fn main() void { while printLn(1) { } }", "void")]
    public void ReportsANonBooleanLoopCondition(string text, string typeName)
    {
        // The same rule an 'if' condition follows, down to sharing the check — only the verb
        // differs, because a loop does not branch.
        Assert.Equal(
            $"test.suru(1,24): 'while' cannot loop on a value of type '{typeName}'; expected 'bool'",
            Assert.Single(Source.Analyze(text)));
    }

    [Fact]
    public void SaysNothingMoreWhenTheLoopConditionAlreadyFailed()
    {
        Assert.Equal("test.suru(2,9): unknown variable 'missing'",
            Assert.Single(Source.Analyze(
                """
                fn main() void {
                  while missing { }
                }
                """)));
    }

    [Theory]
    [InlineData("fn main() void { break }", "test.suru(1,18): 'break' can only appear inside a loop")]
    [InlineData("fn main() void { continue }", "test.suru(1,18): 'continue' can only appear inside a loop")]
    [InlineData("fn main() void { { break } }", "test.suru(1,20): 'break' can only appear inside a loop")]
    [InlineData("fn main() void { if true { continue } }", "test.suru(1,28): 'continue' can only appear inside a loop")]
    public void ReportsBreakAndContinueOutsideALoop(string text, string expected)
    {
        // A block is not a loop and neither is an 'if', so neither gives one somewhere to go.
        Assert.Equal(expected, Assert.Single(Source.Analyze(text)));
    }

    [Theory]
    [InlineData("fn main() void { while true { break } }")]
    [InlineData("fn main() void { while true { continue } }")]
    [InlineData("fn main() void { while true { if true { break } } }")]
    [InlineData("fn main() void { while true { { continue } } }")]
    [InlineData("fn main() void { while true { while true { break } break } }")]
    public void AcceptsBreakAndContinueInsideALoop(string text)
    {
        // An 'if' or a block between the statement and its loop changes nothing: the loop is
        // picked by nesting, and nothing but a loop interrupts that.
        Assert.Empty(Source.Analyze(text));
    }

    [Fact]
    public void ALoopBodyIsItsOwnScope()
    {
        Assert.Equal("test.suru(5,11): unknown variable 'x'",
            Assert.Single(Source.Analyze(
                """
                fn main() void {
                  while true {
                    let x i64: 1
                  }
                  printLn(x)
                }
                """)));
    }

    [Fact]
    public void AnalyzesUnreachableStatementsAfterABreak()
    {
        // Codegen drops what follows a 'break', but analysis does not: a statement that would
        // never run is still a statement, and being unreachable is no reason to stop reporting
        // that it does not typecheck.
        Assert.Equal("test.suru(4,5): unknown function 'foo'",
            Assert.Single(Source.Analyze(
                """
                fn main() void {
                  while true {
                    break
                    foo()
                  }
                }
                """)));
    }

    [Fact]
    public void ANestedLoopRestoresTheOuterOneOnTheWayOut()
    {
        // The depth is a count, not a flag: the 'break' after the inner loop's '}' is still
        // inside the outer one.
        Assert.Empty(Source.Analyze("fn main() void { while true { while true { } break } }"));
    }

    [Fact]
    public void ErrorsAreReportedWithTheSourcePathAndPosition()
    {
        var error = Assert.Single(Source.Analyze("fn main() void { 42 }"));

        Assert.Matches(@"^test\.suru\(\d+,\d+\): ", error);
    }

    // ---- what a file may hold ---------------------------------------------------------------

    [Theory]
    [InlineData("printLn(1)")]
    [InlineData("x: 1")]
    [InlineData("{ }")]
    [InlineData("if true { }")]
    [InlineData("while true { }")]
    [InlineData("break")]
    [InlineData("continue")]
    [InlineData("return")]
    public void ReportsAnythingButLetAndFnAtFileLevel(string text)
    {
        var error = Assert.Single(Source.Analyze(text));

        Assert.Equal("test.suru(1,1): only 'let' and 'fn' may appear outside a function", error);
    }

    /// <summary>Reported once and not analyzed, so the body's own errors do not cascade.</summary>
    [Fact]
    public void DoesNotAnalyzeTheBodyOfAFileLevelStatement()
    {
        var error = Assert.Single(Source.Analyze("if 1 {\n  printLn(x)\n  42\n}"));

        Assert.Equal("test.suru(1,1): only 'let' and 'fn' may appear outside a function", error);
    }

    [Theory]
    [InlineData("#view 1:")]
    [InlineData("#assert(1, 1)")]
    [InlineData("#mock x: 1")]
    public void ReportsADirectiveAtFileLevel(string text)
    {
        var error = Assert.Single(Source.Analyze(text, BuildMode.Test));

        Assert.Equal("test.suru(1,1): a directive can only appear inside a function", error);
    }

    // ---- constants --------------------------------------------------------------------------

    /// <summary>
    /// Every type and both signs, read from a function written above the constants and from a
    /// function nested in another, so neither order nor depth hides one.
    /// </summary>
    [Fact]
    public void AConstantIsVisibleInEveryFunction()
    {
        var dump = AstPrinter.Print(
            Source.Analyzed(
                """
                fn scaled(n i64) i64 {
                  return n * rate + offset
                }
                let rate i64: 12
                let offset i64: -3
                let ratio f64: -1.5
                let verbose bool: false
                fn main() void {
                  fn inner() f64 {
                    return ratio
                  }
                  printLn(scaled(2))
                  printLn(inner())
                  printLn(verbose)
                }
                """),
            withTypes: true);

        Assert.Equal(
            """
            Module test.suru
              FunctionDeclaration (1,1) scaled i64 : i64
                Parameter (1,11) n i64 : i64
                BlockStatement (1,22) function
                  ReturnStatement (2,3)
                    BinaryExpression (2,10) + : i64
                      BinaryExpression (2,10) * : i64
                        IdentifierExpression (2,10) n : i64
                        IdentifierExpression (2,14) rate : i64
                      IdentifierExpression (2,21) offset : i64
              LetStatement (4,1) rate i64
                IntLiteral (4,15) 12 : i64
              LetStatement (5,1) offset i64
                IntLiteral (5,17) -3 : i64
              LetStatement (6,1) ratio f64
                FloatLiteral (6,16) -1.5 : f64
              LetStatement (7,1) verbose bool
                BoolLiteral (7,19) false : bool
              FunctionDeclaration (8,1) main void : void
                BlockStatement (8,16) function
                  FunctionDeclaration (9,3) inner f64 : f64
                    BlockStatement (9,18) function
                      ReturnStatement (10,5)
                        IdentifierExpression (10,12) ratio : f64
                  ExpressionStatement (12,3)
                    CallExpression (12,3) printLn : void
                      CallExpression (12,11) scaled : i64
                        IntLiteral (12,18) 2 : i64
                  ExpressionStatement (13,3)
                    CallExpression (13,3) printLn : void
                      CallExpression (13,11) inner : f64
                  ExpressionStatement (14,3)
                    CallExpression (14,3) printLn : void
                      IdentifierExpression (14,11) verbose : bool

            """,
            dump);
    }

    /// <summary>
    /// The shadows have another type than the constant, so a body that still read the constant
    /// would fail to return.
    /// </summary>
    [Theory]
    [InlineData("let x bool: true\nfn f(x i64) i64 {\n  return x\n}")]
    [InlineData("let x bool: true\nfn f() i64 {\n  let x i64: 1\n  return x\n}")]
    public void AFunctionMayShadowAConstant(string text)
    {
        Assert.Empty(Source.Analyze(text));
    }

    [Theory]
    [InlineData("let a i64: 3 * 4", "test.suru(1,12)")]
    [InlineData("let a i64: x + 1", "test.suru(1,12)")]
    [InlineData("let a i64: -x", "test.suru(1,12)")]
    [InlineData("let a i64: 1\nlet b i64: a", "test.suru(2,12)")]
    [InlineData("fn f() i64 {\n  return 1\n}\nlet c i64: f()", "test.suru(4,12)")]
    public void ReportsAConstantWhoseValueIsNotALiteral(string text, string position)
    {
        var error = Assert.Single(Source.Analyze(text));

        Assert.Equal($"{position}: a constant's value must be a literal", error);
    }

    /// <summary>The name is still declared, so its uses do not also read as unknown.</summary>
    [Fact]
    public void ARejectedConstantIsStillDeclared()
    {
        var error = Assert.Single(Source.Analyze("let a i64: 3 * 4\nfn main() void {\n  printLn(a)\n}"));

        Assert.Equal("test.suru(1,12): a constant's value must be a literal", error);
    }

    [Fact]
    public void ReportsAConstantOfTheWrongType()
    {
        var error = Assert.Single(Source.Analyze("let a i64: true"));

        Assert.Equal("test.suru(1,12): cannot bind a value of type 'bool' to 'a' of type 'i64'", error);
    }

    [Theory]
    [InlineData("let a i64: 1\nlet a i64: 2")]
    [InlineData("fn a() void { }\nlet a i64: 2")]
    public void ReportsAConstantNamedLikeAnotherDeclaration(string text)
    {
        var error = Assert.Single(Source.Analyze(text));

        Assert.Equal("test.suru(2,1): 'a' is already declared", error);
    }

    [Fact]
    public void ReportsAnAssignmentToAConstant()
    {
        var error = Assert.Single(Source.Analyze("let x i64: 1\nfn main() void {\n  x: 2\n}"));

        Assert.Equal("test.suru(3,3): 'x' is a constant and cannot be assigned", error);
    }

    [Fact]
    public void ReportsAMockOfAConstant()
    {
        var error = Assert.Single(
            Source.Analyze("let x i64: 1\nfn main() void {\n  #mock x: 2\n}", BuildMode.Test));

        Assert.Equal("test.suru(3,9): 'x' is a constant and cannot be assigned", error);
    }

    /// <summary>The lone argument of a <c>printLn</c> statement, to read its resolved type off.</summary>
    private static Expression PrintedArgument(Statement statement)
    {
        var call = Assert.IsType<CallExpression>(Assert.IsType<ExpressionStatement>(statement).Expression);
        return Assert.Single(call.Args);
    }
}
