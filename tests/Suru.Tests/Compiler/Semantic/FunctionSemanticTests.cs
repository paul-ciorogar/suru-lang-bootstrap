using Suru.Compiler;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests.Compiler.Semantic;

/// <summary>
/// User-defined functions: where a <c>fn</c> may be written, what a body can see, what a
/// <c>return</c> must carry, and what a call checks. The rules a function shares with everything
/// else — conditions, operators, ordinary bindings — stay in <see cref="SemanticTests"/>, which
/// this file must leave untouched.
/// </summary>
public class FunctionSemanticTests
{
    // ---- accepted -------------------------------------------------------------------------

    [Fact]
    public void AcceptsAFunctionAndACallToIt()
    {
        Assert.Empty(Source.Analyze("fn double(n i64) i64 {\n  return n + n\n}\nprintLn(double(21))"));
    }

    [Fact]
    public void AcceptsAZeroParameterFunction()
    {
        Assert.Empty(Source.Analyze("fn one() i64 {\n  return 1\n}\nprintLn(one())"));
    }

    /// <summary>
    /// The signature pre-pass, seen from the outside: every signature in a list exists before any
    /// statement in it is analyzed, so a call need not come after the declaration it names.
    /// </summary>
    [Fact]
    public void AcceptsACallToAFunctionDeclaredLater()
    {
        Assert.Empty(Source.Analyze("printLn(one())\nfn one() i64 {\n  return 1\n}"));
    }

    [Fact]
    public void AcceptsRecursion()
    {
        Assert.Empty(Source.Analyze(
            """
            fn factorial(n i64) i64 {
              if n = 0 {
                return 1
              }
              return n * factorial(n - 1)
            }
            """));
    }

    /// <summary>The same pre-pass again: neither name could see the other without it.</summary>
    [Fact]
    public void AcceptsMutualRecursion()
    {
        Assert.Empty(Source.Analyze(
            """
            fn isEven(n i64) bool {
              if n = 0 {
                return true
              }
              return isOdd(n - 1)
            }
            fn isOdd(n i64) bool {
              if n = 0 {
                return false
              }
              return isEven(n - 1)
            }
            """));
    }

    /// <summary>
    /// The barrier let through what it must: the enclosing function's name, a sibling's, and the
    /// nested function's own. None of them is a variable, so none of them is hidden.
    /// </summary>
    [Fact]
    public void ANestedFunctionSeesItsParentItsSiblingAndItself()
    {
        Assert.Empty(Source.Analyze(
            """
            fn outer(n i64) i64 {
              fn first(a i64) i64 {
                if a = 0 {
                  return second(a)
                }
                return first(a - 1) + outer(a - 1)
              }
              fn second(a i64) i64 {
                return a
              }
              return first(n)
            }
            """));
    }

    [Fact]
    public void AcceptsAVoidFunctionThatFallsOffItsEnd()
    {
        Assert.Empty(Source.Analyze("fn shout(n i64) void {\n  printLn(n)\n}\nshout(1)"));
    }

    [Fact]
    public void AcceptsABareReturnInAVoidFunction()
    {
        Assert.Empty(Source.Analyze("fn stop() void {\n  return\n}\nstop()"));
    }

    [Fact]
    public void AcceptsAReturnInsideAnArmAndInsideALoop()
    {
        Assert.Empty(Source.Analyze(
            """
            fn f(n i64) i64 {
              while true {
                if n = 0 {
                  return 1
                }
                n: n - 1
              }
              return 0
            }
            """));
    }

    /// <summary>A parameter is an ordinary binding, so it can be assigned and mocked.</summary>
    [Fact]
    public void AcceptsAnAssignmentToAParameter()
    {
        Assert.Empty(Source.Analyze("fn f(n i64) i64 {\n  n: n + 1\n  return n\n}\nprintLn(f(1))"));
    }

    [Fact]
    public void AcceptsABreakAndAContinueInsideALoopInsideABody()
    {
        Assert.Empty(Source.Analyze(
            """
            fn f() void {
              while true {
                if true {
                  break
                }
                continue
              }
            }
            """));
    }

    /// <summary>
    /// Discarding a non-void result is allowed for now; the analyzer carries a TODO to require a
    /// <c>_</c> binding once the language has one.
    /// </summary>
    [Fact]
    public void AcceptsANonVoidCallAsAStatement()
    {
        Assert.Empty(Source.Analyze("fn one() i64 {\n  return 1\n}\none()"));
    }

    [Fact]
    public void AcceptsShadowingAnOuterFunctionsNameInsideABody()
    {
        // The parameter is declared in the body's own scope, so it shadows rather than collides.
        Assert.Empty(Source.Analyze("let n i64: 1\nfn f(n i64) i64 {\n  return n\n}\nprintLn(f(n))"));
    }

    // ---- a body sees only its parameters -----------------------------------------------------

    // TODO: this will change as the language progresses variables declared at the file leve will be constants
    // and therefor can be seen in a function 
    [Fact]
    public void ABodyCannotSeeAFileLevelBinding()
    {
        var error = Assert.Single(Source.Analyze("let x i64: 1\nfn f() i64 {\n  return x\n}"));

        Assert.Equal("test.suru(3,10): unknown variable 'x'", error);
    }

    [Fact]
    public void ABodyCannotSeeTheEnclosingFunctionsLocal()
    {
        var errors = Source.Analyze(
            """
            fn outer(n i64) i64 {
              let local i64: 1
              fn inner() i64 {
                return local + n
              }
              return inner()
            }
            """);

        Assert.Equal(
            [
                "test.suru(4,12): unknown variable 'local'",
                "test.suru(4,20): unknown variable 'n'",
            ],
            errors);
    }

    /// <summary>
    /// The loop barrier, now by construction rather than by a counter: a <c>break</c> in a body
    /// cannot reach the loop its call sits in, because the search stops at the function scope.
    /// </summary>
    [Fact]
    public void ABreakInABodyCannotSeeTheCallersLoop()
    {
        var error = Assert.Single(Source.Analyze(
            """
            fn f() void {
              break
            }
            while true {
              f()
            }
            """));

        Assert.Equal("test.suru(2,3): 'break' can only appear inside a loop", error);
    }

    // ---- where a 'fn' may be written ----------------------------------------------------------

    [Theory]
    [InlineData("if true {\n  fn f() void { }\n}")]
    [InlineData("if false { } else {\n  fn f() void { }\n}")]
    [InlineData("while true {\n  fn f() void { }\n}")]
    public void ReportsAFunctionDeclaredInsideControlFlow(string text)
    {
        var error = Assert.Single(Source.Analyze(text));

        Assert.Equal(
            "test.suru(2,3): a function cannot be declared inside an 'if' or a 'while'", error);
    }

    /// <summary>
    /// A bare block always runs when it is reached, so a declaration in one is a fact and is
    /// allowed — and it is a scope like any other, so the name does not escape it.
    /// </summary>
    [Fact]
    public void AcceptsAFunctionInsideABareBlockAndKeepsItThere()
    {
        Assert.Empty(Source.Analyze("{\n  fn f() void { }\n  f()\n}"));

        var error = Assert.Single(Source.Analyze("{\n  fn f() void { }\n}\nf()"));
        Assert.Equal("test.suru(4,1): unknown function 'f'", error);
    }

    [Fact]
    public void AcceptsAFunctionDeclaredAfterAnotherStatement()
    {
        Assert.Empty(Source.Analyze("let x i64: 1\nprintLn(x)\nfn f() void { }\nf()"));
    }

    // ---- names ------------------------------------------------------------------------------

    [Fact]
    public void ReportsARedeclaredFunction()
    {
        var error = Assert.Single(Source.Analyze("fn f() void { }\nfn f() void { }"));

        Assert.Equal("test.suru(2,1): 'f' is already declared", error);
    }

    /// <summary>
    /// One namespace, so a <c>let</c> and an <c>fn</c> of the same name collide — and the
    /// <c>let</c> is what reports it whichever order they are written in, because the pre-pass
    /// declared the function before any statement in the list was analyzed. That is the same
    /// reason a call may precede its declaration.
    /// </summary>
    [Theory]
    [InlineData("let f i64: 1\nfn f() void { }", "test.suru(1,1)")]
    [InlineData("fn f() void { }\nlet f i64: 1", "test.suru(2,1)")]
    public void ReportsABindingCollidingWithAFunction(string text, string position)
    {
        var error = Assert.Single(Source.Analyze(text));

        Assert.Equal($"{position}: 'f' is already declared", error);
    }

    [Fact]
    public void ReportsARedeclaredParameter()
    {
        var error = Assert.Single(Source.Analyze("fn f(a i64, a i64) void { }"));

        Assert.Equal("test.suru(1,13): 'a' is already declared", error);
    }

    [Fact]
    public void ReportsAParameterCollidingWithABindingInTheBody()
    {
        // Parameters share the body's scope, so this is a redeclaration and not a shadow.
        var error = Assert.Single(Source.Analyze("fn f(a i64) void {\n  let a i64: 1\n}"));

        Assert.Equal("test.suru(2,3): 'a' is already declared", error);
    }

    [Fact]
    public void ReportsARedeclaredBuiltin()
    {
        var error = Assert.Single(Source.Analyze("fn printLn(n i64) void { }"));

        Assert.Equal("test.suru(1,1): 'printLn' is a builtin and cannot be redeclared", error);
    }

    [Fact]
    public void ReportsAFunctionNameUsedAsAValue()
    {
        var error = Assert.Single(Source.Analyze("fn f() i64 {\n  return 1\n}\nprintLn(f)"));

        Assert.Equal("test.suru(4,9): 'f' is a function and cannot be used as a value", error);
    }

    [Fact]
    public void ReportsAnAssignmentToAFunctionName()
    {
        var error = Assert.Single(Source.Analyze("fn f() void { }\nf: 1"));

        Assert.Equal("test.suru(2,1): 'f' is a function and cannot be used as a value", error);
    }

    [Fact]
    public void ReportsACallToAVariable()
    {
        var error = Assert.Single(Source.Analyze("let f i64: 1\nf()"));

        Assert.Equal("test.suru(2,1): 'f' is not a function", error);
    }

    // ---- types ------------------------------------------------------------------------------

    [Fact]
    public void ReportsAnUnknownParameterTypeAtTheTypeName()
    {
        var error = Assert.Single(Source.Analyze("fn f(a inter) void { }"));

        Assert.Equal("test.suru(1,8): unknown type 'inter'", error);
    }

    [Fact]
    public void ReportsAnUnknownReturnTypeAtTheTypeName()
    {
        var error = Assert.Single(Source.Analyze("fn f() inter {\n  return 1\n}"));

        Assert.Equal("test.suru(1,8): unknown type 'inter'", error);
    }

    /// <summary>
    /// <c>void</c> is writable as a return type and nowhere else, and a parameter written
    /// <c>void</c> shares the message a <c>let</c> gets, because both go through one lookup.
    /// </summary>
    [Fact]
    public void ReportsAVoidParameter()
    {
        var error = Assert.Single(Source.Analyze("fn f(a void) i64 {\n  return 1\n}"));

        Assert.Equal("test.suru(1,8): nothing can be bound to 'void'", error);
    }

    /// <summary>
    /// The signature is registered even though its return type failed, so the call reports the
    /// problem it has rather than an 'unknown function' it does not.
    /// </summary>
    [Fact]
    public void AFunctionWithABadReturnTypeIsStillDeclared()
    {
        var errors = Source.Analyze("fn f(n i64) int {\n  return n\n}\nf(1, 2)");

        Assert.Equal(
            [
                "test.suru(1,13): unknown type 'int'",
                "test.suru(4,1): 'f' expects 1 argument, got 2",
            ],
            errors);
    }

    // ---- calls ------------------------------------------------------------------------------

    [Fact]
    public void ReportsTooFewArguments()
    {
        var error = Assert.Single(Source.Analyze("fn f(a i64, b i64) void { }\nf(1)"));

        Assert.Equal("test.suru(2,1): 'f' expects 2 arguments, got 1", error);
    }

    [Fact]
    public void ReportsTooManyArguments()
    {
        var error = Assert.Single(Source.Analyze("fn f() void { }\nf(1)"));

        Assert.Equal("test.suru(2,1): 'f' expects 0 arguments, got 1", error);
    }

    [Fact]
    public void ReportsAnArgumentOfTheWrongTypeAtItsOwnPosition()
    {
        var error = Assert.Single(Source.Analyze("fn f(a i64, b i64) void { }\nf(1, 2.5)"));

        Assert.Equal("test.suru(2,6): argument 2 of 'f' is of type 'f64'; expected 'i64'", error);
    }

    [Fact]
    public void AnnotatesACallWithTheFunctionsReturnType()
    {
        var module = Source.Analyzed("fn f() f64 {\n  return 1.5\n}\nprintLn(f())");
        var call = Assert.IsType<CallExpression>(
            Assert.IsType<ExpressionStatement>(module.Statements[1]).Expression);

        Assert.Equal(SuruType.F64, Assert.Single(call.Args).Type);
    }

    /// <summary>
    /// A void call in expression position needs no rule of its own — every consumer already
    /// refuses a value of type <c>void</c>, wherever it came from.
    /// </summary>
    [Theory]
    [InlineData("let x i64: v()", "test.suru(5,12): cannot bind a value of type 'void' to 'x' of type 'i64'")]
    [InlineData("printLn(v())", "test.suru(5,9): 'printLn' cannot print a value of type 'void'; expected 'bool', 'i64', 'f64'")]
    [InlineData("let x i64: v() + 1", "test.suru(5,12): operator '+' cannot be applied to 'void' and 'i64'")]
    [InlineData("if v() { }", "test.suru(5,4): 'if' cannot branch on a value of type 'void'; expected 'bool'")]
    public void ReportsAVoidCallUsedAsAValue(string text, string expected)
    {
        var error = Assert.Single(Source.Analyze($"fn v() void {{\n  printLn(1)\n}}\n\n{text}"));

        Assert.Equal(expected, error);
    }

    // ---- return -----------------------------------------------------------------------------

    [Fact]
    public void ReportsAReturnOutsideAFunction()
    {
        var error = Assert.Single(Source.Analyze("return 1"));

        Assert.Equal("test.suru(1,1): 'return' can only appear inside a function", error);
    }

    [Fact]
    public void ReportsAReturnInABlockOutsideAFunction()
    {
        var error = Assert.Single(Source.Analyze("while true {\n  return\n}"));

        Assert.Equal("test.suru(2,3): 'return' can only appear inside a function", error);
    }

    [Fact]
    public void ReportsAValueReturnedFromAVoidFunction()
    {
        var error = Assert.Single(Source.Analyze("fn f() void {\n  return 1\n}"));

        Assert.Equal("test.suru(2,3): 'f' returns 'void'; 'return' cannot carry a value", error);
    }

    [Fact]
    public void ReportsABareReturnFromANonVoidFunction()
    {
        var error = Assert.Single(Source.Analyze("fn f() i64 {\n  return\n}"));

        Assert.Equal("test.suru(2,3): 'f' must return a value of type 'i64'", error);
    }

    [Fact]
    public void ReportsAReturnedValueOfTheWrongType()
    {
        var error = Assert.Single(Source.Analyze("fn f() i64 {\n  return 1.5\n}"));

        Assert.Equal(
            "test.suru(2,10): cannot return a value of type 'f64' from 'f' of type 'i64'", error);
    }

    // ---- every path returns -------------------------------------------------------------------

    [Fact]
    public void ReportsANonVoidFunctionThatCanFallOffItsEnd()
    {
        var error = Assert.Single(Source.Analyze("fn f() i64 {\n  printLn(1)\n}"));

        Assert.Equal("test.suru(1,1): 'f' must return a value of type 'i64' on every path", error);
    }

    /// <summary>One arm is a path that falls through, so an <c>if</c> without an else never counts.</summary>
    [Fact]
    public void AnIfWithNoElseDoesNotCountAsReturning()
    {
        var error = Assert.Single(Source.Analyze(
            "fn f(n i64) i64 {\n  if n = 0 {\n    return 1\n  }\n}"));

        Assert.Equal("test.suru(1,1): 'f' must return a value of type 'i64' on every path", error);
    }

    [Fact]
    public void BothArmsReturningCountsAsReturning()
    {
        Assert.Empty(Source.Analyze(
            "fn f(n i64) i64 {\n  if n = 0 {\n    return 1\n  } else {\n    return 2\n  }\n}"));
    }

    /// <summary>An <c>else if</c> is a nested <c>if</c>, so the rule applies to it unchanged.</summary>
    [Fact]
    public void AnElseIfChainCountsWhenItsLastElseReturns()
    {
        Assert.Empty(Source.Analyze(
            """
            fn f(n i64) i64 {
              if n = 0 {
                return 1
              } else if n = 1 {
                return 2
              } else {
                return 3
              }
            }
            """));
    }

    [Fact]
    public void AReturnMidBlockCountsForTheWholeBlock()
    {
        // The statements after it are unreachable, which is the rule codegen applies too.
        Assert.Empty(Source.Analyze("fn f() i64 {\n  return 1\n  printLn(2)\n}"));
    }

    /// <summary>
    /// A loop body may never run and nothing folds a constant condition, so this is rejected. An
    /// accepted cost: the fix is to write the return after the loop.
    /// </summary>
    [Fact]
    public void ALoopNeverCountsAsReturning()
    {
        var error = Assert.Single(Source.Analyze("fn f() i64 {\n  while true {\n    return 1\n  }\n}"));

        Assert.Equal("test.suru(1,1): 'f' must return a value of type 'i64' on every path", error);
    }

    [Fact]
    public void ANestedFunctionIsCheckedToo()
    {
        var error = Assert.Single(Source.Analyze(
            "fn outer() void {\n  fn inner() i64 {\n    printLn(1)\n  }\n}"));

        Assert.Equal(
            "test.suru(2,3): 'inner' must return a value of type 'i64' on every path", error);
    }
}
