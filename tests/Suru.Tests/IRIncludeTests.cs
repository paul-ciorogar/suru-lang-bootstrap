namespace Suru.Tests;

// Verifies that the IR backend handles included namespaced functions transparently.
//
// Include resolution — `Compiler.CompileIR` calls `ResolveIncludes` before constructing
// an IRCodeGenerator. ResolveIncludes reads lib.suru, renames every FunctionDeclaration
// it finds by prepending the alias (e.g., `double` → `lib.double`, `greet` → `lib.greet`),
// and merges them into the main module's statement list. By the time IRCodeGenerator.Generate
// is called, the module is a single flat list of FunctionDeclarations — include directives
// have already been resolved and removed.
//
// Pre-pass registration — the _userFunctions pre-pass iterates all FunctionDeclarations
// before emitting any body. It registers `lib.double` and `lib.greet` just like any
// other user-defined function. No special namespace handling is needed in the pre-pass.
//
// Call sites — `lib.double(21)` is parsed as CallExpression { Name: "lib.double", Args: [21] }.
// EmitValue dispatches it to EmitUserFunctionCall because `_userFunctions.ContainsKey("lib.double")`
// is true. EmitUserFunctionCall emits `call i64 @lib.double(...)`. LLVM IR accepts dots in
// function identifiers, so `@lib.double` is a valid LLVM symbol — no mangling is needed.
//
// What this fixture covers:
//   lib.double(21) — Int64 parameter, Int64 return; result passed to printLn(Int64)
//   lib.greet("Suru") — String parameter, String return; result passed to printLn(String)
//   lib.greet uses .append() internally — String IR already works; this confirms it works
//   through a cross-module call boundary (the callee lives in lib.suru, not main.suru)
//
// Return-type correctness — lib.greet returns String (ptr), not Int64. This requires that:
//   - EmitFunction emits `define internal ptr @lib.greet(...)` (not hardcoded i64)
//   - ReturnStatement emits `ret ptr` matched to _currentFnReturnLlvmType
//   - EmitUserFunctionCall emits `call ptr @lib.greet(...)` using LlvmType(returnType)
// Without these, clang rejects the IR with a type mismatch error.
[Collection("IntegrationIR")]
public class IRIncludeTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("include-test");

    [Fact]
    public void Include_CallsNamespacedFunctions()
        => Assert.Equal("42\nHello, Suru!\n", Run(_exe));
}
