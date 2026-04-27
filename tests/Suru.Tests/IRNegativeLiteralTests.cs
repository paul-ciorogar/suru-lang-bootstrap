namespace Suru.Tests;

// Verifies the IR backend's handling of negative integer and float literals.
//
// Parsing — the parser represents `-5` as a UnaryExpression(Minus, IntLiteral(5))
// and `-2.5` as UnaryExpression(Minus, FloatLiteral(2.5)). ParsePrimary detects
// the leading Minus token and wraps the literal rather than treating minus as a
// binary operator. This mirrors the ParseMatchPattern logic.
//
// Codegen — EmitValue dispatches UnaryExpression:
//   Int64   → emits `sub i64 0, <val>` (two's-complement negation)
//   Float64 → emits `fneg double <val>`; the literal itself is emitted in IEEE-754
//             hex form (e.g. 0x4004000000000000 for 2.5) to avoid decimal rounding.
//
// The fixture prints -5, -2.5, and -8 (result of -5 + -3 via `.add(-3)`), confirming
// that negative literals are usable in expression positions, not just let declarations.
[Collection("IntegrationIR")]
public class IRNegativeLiteralTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("negative-literals");
    private bool _testPassed;

    [Fact]
    public void PrintsExpectedOutput()
    {
        Assert.Equal("-5\n-2.5\n-8\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("negative-literals"); }
}
