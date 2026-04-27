namespace Suru.Tests;

// Verifies the IR backend's match expression support and the compare() method.
//
// Match as statement: each arm body is a side-effecting printLn call; arms are
// selected by a conditional branch chain and all converge on a merge label.
//
// Match as expression: each arm produces a value stored into an alloca'd result
// slot; after the merge label a single load yields the selected value. This
// avoids phi nodes, which are harder to emit correctly in text-form IR.
//
// String pattern matching uses @strcmp (returns i32; 0 means equal) rather than
// the Seq-header approach in CodeGenerator, because IRCodeGenerator strings are
// raw null-terminated pointers with no length header.
//
// compare() returns -1 / 0 / 1 via the zext(gt) - zext(lt) trick.
//
// Variable/constant patterns: match arms may name a module-level constant or a
// local variable as a pattern. The parser returns a VariableReferenceExpression
// for any identifier in pattern position; EmitMatchTestChain loads it via
// EmitValue before emitting the icmp/fcmp comparison — no codegen changes needed.
[Collection("IntegrationIR")]
public class IRControlFlowTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("control-flow");
    private bool _testPassed;

    [Fact]
    public void PrintsExpectedOutput()
    {
        Assert.Equal("1\n1\ntrue\ntrue\nfalse\n1\n1\n-1\nFriday\nFriday\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("control-flow"); }
}
