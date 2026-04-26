namespace Suru.Tests;

// Verifies the IR backend's printLn built-in across all four scalar types.
//
// printLn dispatch — EmitStmt routes `printLn(expr)` to EmitPrintLn(val, type),
// which selects a format string based on the value's SuruType:
//
//   Bool    → @.str_true / @.str_false selected at compile time (constant condition)
//             or a conditional branch to pick the right global at runtime; printed
//             via @printf with format @.fmt_s ("%s\n")
//   Int64   → @printf with format @.fmt_int ("%lld\n")
//   Float64 → @printf with format @.fmt_float ("%.15g\n")
//   String  → EmitExtractStringData extracts the null-terminated data ptr from the
//             Seq header; @printf with format @.fmt_s ("%s\n")
//
// All format-string globals are declared lazily in BoolStringGlobals and only
// emitted in the final assembly pass if at least one printLn of that type was
// encountered during pass 1.
[Collection("IntegrationIR")]
public class IRPrintTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("print");

    [Fact]
    public void PrintsExpectedOutput()
        => Assert.Equal("true\nfalse\n1\n1.2\n", Run(_exe));
}
