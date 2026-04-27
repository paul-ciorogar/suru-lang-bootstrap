namespace Suru.Tests;

// Verifies the IR backend's printError built-in: stderr output without disturbing stdout.
//
// printError(msg String) — EmitStmt extracts the null-terminated data ptr from the
// String Seq header (EmitExtractStringData), then emits:
//
//   call i32 @fprintf(ptr @stderr, ptr @.fmt_s, ptr <data>)
//
// @stderr is declared as `@stderr = external global ptr` in Externals via AddStderr().
// It is loaded once at the call site and passed as the FILE* argument. The format
// string @.fmt_s ("%s\n") is the same global used by printLn for String values, so
// no extra global is needed.
//
// RunGetStreams in IntegrationTestBase captures both stdout and stderr via
// Process.StandardOutput / Process.StandardError and returns them as a tuple,
// letting tests assert each stream independently.
[Collection("IntegrationIR")]
public class IRPrintErrorTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("print-error");
    private bool _testPassed;

    [Fact]
    public void PrintError_WritesToStderr()
    {
        var (_, stderr) = RunGetStreams(_exe);
        Assert.Equal("error: something went wrong\n", stderr);
        _testPassed = true;
    }

    [Fact]
    public void PrintError_StdoutUnaffected()
    {
        var (stdout, _) = RunGetStreams(_exe);
        Assert.Equal("stdout line\n", stdout);
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("print-error"); }
}
