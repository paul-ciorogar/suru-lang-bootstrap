namespace Suru.Tests;

// Verifies that the LLVM module model for include directives compiles and links correctly.
//
// ── LLVM module model ────────────────────────────────────────────────────────────────────
//
// Each .suru source file is a separate compilation unit that produces its own .ll and .o.
// Include resolution (ResolveIncludes) still merges function signatures into the main
// module's statement list so semantic analysis can type-check cross-module calls, but the
// codegen treats them differently:
//
//   Main module (main.suru → main.ll)
//     - Local functions: `define @fn(...)` with external linkage so they are linkable.
//     - Imported functions (from `include "lib.suru" as lib`):
//         `declare i64 @double(...)` — the ORIGINAL unqualified name, not @lib.double.
//     - Call sites: `call i64 @double(...)` — original name; linker resolves against lib.o.
//
//   Included module (lib.suru → lib.ll, compiled independently)
//     - Defines `@double` and `@greet` with external linkage.
//     - Has its own @main wrapper only if lib.suru contains `fn main` (it doesn't here).
//
// The linker combines main.o + lib.o and resolves all cross-module references.
//
// ── What this fixture covers ─────────────────────────────────────────────────────────────
//
//   lib.double(21)    — Int64 parameter, Int64 return; result passed to printLn(Int64)
//   lib.greet("Suru") — String parameter, String return; result passed to printLn(String)
//   lib.greet uses .append() internally — confirms cross-module String returns work.
//
// Return-type correctness — lib.greet returns String (ptr), not Int64. This requires:
//   - EmitFunction emits `define ptr @greet(...)` in lib.ll (no `internal`, external linkage)
//   - declare in main.ll: `declare ptr @greet(...)`
//   - Call site: `call ptr @greet(...)` — type must match the declare
// Without these, clang rejects the IR with a type mismatch error.
[Collection("IntegrationIR")]
public class IRIncludeTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("include-test");
    private bool _testPassed;

    [Fact]
    public void Include_CallsNamespacedFunctions()
    {
        Assert.Equal("42\nHello, Suru!\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("include-test"); }
}
