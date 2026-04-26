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
[Collection("IntegrationIR")]
public class IRControlFlowTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("control-flow");

    [Fact]
    public void PrintsExpectedOutput()
        => Assert.Equal("1\n1\ntrue\ntrue\nfalse\n1\n1\n-1\n", Run(_exe));
}
