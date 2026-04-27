namespace Suru.Tests;

// Verifies the IR backend's while loop, variable reassignment, and string append.
//
// WhileStatement — emitted as three consecutive basic blocks:
//
//   br label %while_cond_N           ← closes the preceding block (no fall-through)
//   while_cond_N:
//     <evaluate boolean condition>
//     br i1 <cond>, label %while_body_N, label %while_after_N
//   while_body_N:
//     <body statements>
//     br label %while_cond_N         ← loop-back edge (omitted if body terminates)
//   while_after_N:                   ← execution continues here
//
// _whileCounter provides unique N values so nested or sequential loops get distinct labels.
//
// AssignmentStatement — stores a new value into the alloca that was created by the preceding
// LetStatement. The declared type from _vars is used as the store width so the alloca and
// store sizes always agree. For example, `i: i.add(1)` emits:
//   %t_new = add i64 %t_old, 1
//   store i64 %t_new, ptr %i.addr
//
// String append and the Seq representation — strings are %suru.Seq = { i64 len, ptr data }
// heap structs. `let s: ""` creates a Seq header with len=0 and data pointing to a global
// [1 x i8] constant. Each `s: s.append("x")` in the loop body:
//   1. Extracts len and data from both Seqs.
//   2. Adds the lengths; mallocs a new data buffer of (len1+len2+1) bytes.
//   3. memcpys the first string then the second; stores a null terminator.
//   4. Mallocs a new 16-byte Seq header, stores the new len and data ptr.
//   5. Overwrites %s.addr (an `alloca ptr`) with the new Seq pointer.
// The old Seq pointers are leaked — memory management is out of scope for this migration.
[Collection("IntegrationIR")]
public class IRWhileLoopTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("while-loop");
    private bool _testPassed;

    [Fact]
    public void WhileLoop_PrintsExpectedOutput()
    {
        Assert.Equal("1\n2\n3\n4\n5\n55\nxxx\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("while-loop"); }
}
