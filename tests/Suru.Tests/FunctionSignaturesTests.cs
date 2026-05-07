using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Semantic;
using Suru.Compiler.Types;

namespace Suru.Tests;

// Unit tests for the FunctionSig record and the scoped function-signature /
// constant infrastructure added to Scope and Scopes.
//
// Part A — Scope / Scopes unit tests: exercise the data structures directly.
// Part B — Semantic integration tests: verify that SemanticAnalyzer behaviour
//          is unchanged now that _functions and _constants live in the scope stack.
public class FunctionSignaturesTests
{
    // ─── Part A: Scope unit tests ─────────────────────────────────────────────

    [Fact]
    public void FunctionSig_StoresParamTypesAndReturnType()
    {
        IReadOnlyList<SuruType> parms = [SuruType.Int64, SuruType.Bool];
        var sig = new FunctionSig(parms, SuruType.String);
        Assert.Same(parms, sig.ParamTypes);
        Assert.Equal(SuruType.String, sig.ReturnType);
    }

    [Fact]
    public void FunctionSig_NullReturnType_VoidFunction()
    {
        var sig = new FunctionSig([], null);
        Assert.Null(sig.ReturnType);
        Assert.Empty(sig.ParamTypes);
    }

    [Fact]
    public void FunctionSig_SameReference_IsEqual()
    {
        // Records compare IReadOnlyList by reference; same list → equal records.
        IReadOnlyList<SuruType> parms = [SuruType.Int64];
        var a = new FunctionSig(parms, null);
        var b = new FunctionSig(parms, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Scope_DeclareFunction_ThenTryGet_ReturnsSig()
    {
        var scope = new Scope();
        var sig   = new FunctionSig([SuruType.Int64], SuruType.Bool);
        scope.DeclareFunction("foo", sig);
        Assert.Equal(sig, scope.TryGetFunction("foo"));
    }

    [Fact]
    public void Scope_TryGetFunction_MissingName_ReturnsNull()
    {
        var scope = new Scope();
        Assert.Null(scope.TryGetFunction("missing"));
    }

    [Fact]
    public void Scope_ContainsFunction_True_AfterDeclare()
    {
        var scope = new Scope();
        scope.DeclareFunction("bar", new FunctionSig([], null));
        Assert.True(scope.ContainsFunction("bar"));
    }

    [Fact]
    public void Scope_ContainsFunction_False_WhenAbsent()
    {
        var scope = new Scope();
        Assert.False(scope.ContainsFunction("bar"));
    }

    [Fact]
    public void Scope_MarkConstant_IsConstant_True()
    {
        var scope = new Scope();
        scope.MarkConstant("VERSION");
        Assert.True(scope.IsConstant("VERSION"));
    }

    [Fact]
    public void Scope_IsConstant_False_WhenNotMarked()
    {
        var scope = new Scope();
        Assert.False(scope.IsConstant("VERSION"));
    }

    // ─── Part A: Scopes unit tests ────────────────────────────────────────────

    [Fact]
    public void Scopes_RegisterFunction_LookupFunction_SameScope()
    {
        var scopes = new Scopes();
        scopes.Enter();
        var sig = new FunctionSig([SuruType.String], SuruType.Int64);
        scopes.RegisterFunction("len", sig);
        Assert.Equal(sig, scopes.LookupFunction("len"));
        scopes.Exit();
    }

    [Fact]
    public void Scopes_LookupFunction_FindsOuterScope()
    {
        var scopes = new Scopes();
        scopes.Enter();
        var sig = new FunctionSig([], SuruType.Bool);
        scopes.RegisterFunction("check", sig);

        scopes.Enter(); // inner scope — should still see outer function
        Assert.Equal(sig, scopes.LookupFunction("check"));
        scopes.Exit();
        scopes.Exit();
    }

    [Fact]
    public void Scopes_LookupFunction_ReturnsNull_AfterScopeExit()
    {
        var scopes = new Scopes();
        scopes.Enter();
        scopes.Enter();
        scopes.RegisterFunction("inner", new FunctionSig([], null));
        scopes.Exit(); // inner scope pops — function gone
        Assert.Null(scopes.LookupFunction("inner"));
        scopes.Exit();
    }

    [Fact]
    public void Scopes_FunctionExistsInCurrent_OnlyChecksTopFrame()
    {
        var scopes = new Scopes();
        scopes.Enter();
        scopes.RegisterFunction("outer", new FunctionSig([], null));

        scopes.Enter(); // inner scope
        Assert.False(scopes.FunctionExistsInCurrent("outer")); // outer not in current frame
        scopes.RegisterFunction("outer", new FunctionSig([], null));
        Assert.True(scopes.FunctionExistsInCurrent("outer"));  // now it is
        scopes.Exit();
        scopes.Exit();
    }

    [Fact]
    public void Scopes_MarkConstant_IsConstant_SameScope()
    {
        var scopes = new Scopes();
        scopes.Enter();
        scopes.MarkConstant("PI");
        Assert.True(scopes.IsConstant("PI"));
        scopes.Exit();
    }

    [Fact]
    public void Scopes_IsConstant_FindsOuterScope()
    {
        var scopes = new Scopes();
        scopes.Enter();
        scopes.MarkConstant("MAX");

        scopes.Enter(); // inner scope should still see constant from outer
        Assert.True(scopes.IsConstant("MAX"));
        scopes.Exit();
        scopes.Exit();
    }

    [Fact]
    public void Scopes_IsConstant_False_AfterScopeExit()
    {
        var scopes = new Scopes();
        scopes.Enter();
        scopes.Enter();
        scopes.MarkConstant("TMP");
        scopes.Exit(); // constant scope popped
        Assert.False(scopes.IsConstant("TMP"));
        scopes.Exit();
    }

    // ─── Part B: Semantic integration tests ──────────────────────────────────

    private static IReadOnlyList<string> Analyze(string source)
    {
        var module = Parser.Parse(new Tokens(new Lexer(source), "<test>")).Require();
        return SemanticAnalyzer.Analyze(module);
    }

    [Fact]
    public void FunctionSignature_CorrectArgCount_NoError()
    {
        var errors = Analyze("""
            fn add(a Int64, b Int64) Int64 {
                return a.add(b)
            }
            fn main(args Array<String>) {
                let x Int64: add(1, 2)
            }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void FunctionSignature_WrongArgCount_ReportsError()
    {
        var errors = Analyze("""
            fn add(a Int64, b Int64) Int64 {
                return a.add(b)
            }
            fn main(args Array<String>) {
                let x Int64: add(1)
            }
            """);
        Assert.Contains(errors, e => e.Contains("function 'add'") && e.Contains("argument"));
    }

    [Fact]
    public void DuplicateFunction_ReportsError()
    {
        var errors = Analyze("""
            fn greet() void { }
            fn greet() void { }
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("function 'greet' is already declared"));
    }

    [Fact]
    public void ModuleLevelConstant_Reassignment_ReportsError()
    {
        var errors = Analyze("""
            let LIMIT Int64: 100
            fn main(args Array<String>) {
                LIMIT: 200
            }
            """);
        Assert.Contains(errors, e => e.Contains("cannot reassign constant 'LIMIT'"));
    }

    [Fact]
    public void ModuleLevelConstant_ReadableInsideFunction_NoError()
    {
        var errors = Analyze("""
            let LIMIT Int64: 100
            fn check() Bool {
                return LIMIT.gt(0)
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void LocalVariable_InFunctionScope_NotConstant_CanReassign()
    {
        var errors = Analyze("""
            fn run() void {
                let x Int64: 1
                x: 2
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void FunctionReturnType_Inferred_NoError()
    {
        var errors = Analyze("""
            fn double(n Int64) Int64 {
                return n.multiply(2)
            }
            fn main(args Array<String>) {
                let r Int64: double(5)
            }
            """);
        Assert.Empty(errors);
    }
}
