namespace Suru.Tests;

// Verifies the IR backend's exit() built-in: process exit code propagation.
//
// exit(code Int64) is a terminal statement. EmitStmt handles it by:
//   1. Truncating the i64 code to i32  (C's exit() takes int, not long).
//   2. Calling @exit(i32) declared via _externals.AddExit().
//   3. Emitting `unreachable` to close the current basic block (LLVM requires
//      every block to end with a terminator; exit never returns).
//   4. Opening a fresh, unlabelled dead block (_blockOpen = true) to absorb
//      any statements the parser placed after the exit call — these are
//      unreachable at runtime but must not prevent IR from being well-formed.
//
// RunGetExitCode in IntegrationTestBase captures the process exit status and
// returns it as an int, letting the test assert the exact code without parsing
// stdout/stderr.
[Collection("IntegrationIR")]
public class IRExitTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("exit_test");

    [Fact]
    public void Exit_ReturnsCode()
        => Assert.Equal(42, RunGetExitCode(_exe));
}
