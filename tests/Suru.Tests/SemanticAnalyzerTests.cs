using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Semantic;

namespace Suru.Tests;

// Unit tests for the SemanticAnalyzer — covers the checks introduced in Stage 12.5g.
//
// Every test calls AnalyzeSource() directly (no file I/O, no IR codegen) and asserts
// on the returned error list.  These tests are intentionally independent of the
// compilation pipeline so that regressions can be caught at the semantic layer.
public class SemanticAnalyzerTests
{
    private static IReadOnlyList<string> Analyze(string source)
    {
        var module = Parser.Parse(new Tokens(new Lexer(source), "<test>")).Require();
        return SemanticAnalyzer.Analyze(module);
    }

    // ─── Variable / constant errors ───────────────────────────────────────────

    [Fact]
    public void UndefinedVariable_ReportsError()
    {
        var errors = Analyze("""
            fn main(args Array<String>) {
                let x Int64: y
            }
            """);
        Assert.Contains(errors, e => e.Contains("undefined variable 'y'"));
    }

    [Fact]
    public void DefinedVariable_NoError()
    {
        var errors = Analyze("""
            fn main(args Array<String>) {
                let y Int64: 1
                let x Int64: y
            }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void ConstantReassignment_ReportsError()
    {
        var errors = Analyze("""
            let x Int64: 1
            fn main(args Array<String>) {
                x: 2
            }
            """);
        Assert.Contains(errors, e => e.Contains("cannot reassign constant 'x'"));
    }

    [Fact]
    public void UnknownTypeAnnotation_ReportsError()
    {
        var errors = Analyze("""
            fn main(args Array<String>) {
                let x FooBar: 42
            }
            """);
        Assert.Contains(errors, e => e.Contains("unknown type") && e.Contains("FooBar"));
    }

    // ─── Type declaration errors ──────────────────────────────────────────────

    [Fact]
    public void DuplicateTypeDeclaration_ReportsError()
    {
        var errors = Analyze("""
            type Point: { x Int64, y Int64 }
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("type 'Point' is already declared"));
    }

    [Fact]
    public void SingleTypeDeclaration_NoError()
    {
        var errors = Analyze("""
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    // ─── Function declaration errors ──────────────────────────────────────────

    [Fact]
    public void DuplicateFunctionDeclaration_ReportsError()
    {
        var errors = Analyze("""
            fn foo() void { }
            fn foo() void { }
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("function 'foo' is already declared"));
    }

    [Fact]
    public void NonVoidFunctionWithNoReturn_ReportsError()
    {
        var errors = Analyze("""
            fn add(a Int64, b Int64) Int64 {
                let x Int64: 1
            }
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("non-void function 'add' has no return statement"));
    }

    [Fact]
    public void NonVoidFunctionWithReturn_NoError()
    {
        var errors = Analyze("""
            fn add(a Int64, b Int64) Int64 {
                return a.add(b)
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void VoidFunction_NoReturnRequired()
    {
        var errors = Analyze("""
            fn greet() void {
                let x Int64: 1
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    // ─── Block-level scoping ──────────────────────────────────────────────────

    [Fact]
    public void SameName_InSequentialWhileLoops_NoError()
    {
        // `let i` in two separate (non-nested) while loops must not trigger a duplicate error.
        var errors = Analyze("""
            fn run() void {
                let done Bool: false
                while done.invert() {
                    let i Int64: 0
                    done: true
                }
                let done2 Bool: false
                while done2.invert() {
                    let i Int64: 1
                    done2: true
                }
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void SameName_InSameScope_ReportsError()
    {
        var errors = Analyze("""
            fn run() void {
                let x Int64: 1
                let x Int64: 2
            }
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("variable 'x' is already declared"));
    }

    // ─── Return reachability ──────────────────────────────────────────────────

    [Fact]
    public void ReturnInsideWhile_SatisfiesReturnRequirement()
    {
        // A return statement inside a while loop body counts as satisfying
        // the non-void return requirement (conservative: assume loop runs).
        var errors = Analyze("""
            fn firstToken(tokens Array<String>) String {
                let i Int64: 0
                while i.lt(10) {
                    return tokens.at(0)
                }
                return ""
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void ExitInsideWhile_SatisfiesReturnRequirement()
    {
        var errors = Analyze("""
            fn run(ok Bool) Int64 {
                while ok {
                    exit(0)
                }
                return 0
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    // ─── Module-level constants visible inside functions ──────────────────────

    [Fact]
    public void ModuleLevelConstant_VisibleInsideFunction()
    {
        var errors = Analyze("""
            let VERSION Int64: 1
            fn getVersion() Int64 {
                return VERSION
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }
}
