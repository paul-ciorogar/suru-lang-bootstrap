namespace Suru.Tests;

// Verifies clone(s) and drop(s) for the String type.
//
// clone(s) — EmitCloneStringDispatch / EmitCloneString (IRStringCodeGenerator.cs):
//   Allocates a new 16-byte %suru.Seq header and a new heap char buffer of `len+1`
//   bytes; memcpy's all bytes (including the null terminator) from the source buffer.
//   The new Seq is fully heap-owned and independent of the original.
//
// drop(s) — EmitDropStringDispatch / EmitDropString (IRStringCodeGenerator.cs):
//   Frees the char buffer first (free(data)), then the Seq header (free(seqPtr)).
//   Safe for every String because EmitStringLiteralValue always mallocs a fresh
//   char buffer — there is no "literal" Seq that points into read-only memory.
//
// These operations are dispatched from EmitValue ahead of the Array and Struct
// clone/drop arms, guarded by PeekType returning SuruType.String.
[Collection("IntegrationIR")]
public class IRStringCloneDropTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("string-clone-drop");
    private bool _testPassed;

    [Fact]
    public void StringCloneDrop_PrintsExpectedOutput()
    {
        // clone survives after original is dropped; chained clones are independent.
        Assert.Equal("hello\nfoo bar\nworld\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("string-clone-drop"); }
}
