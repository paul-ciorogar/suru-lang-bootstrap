namespace Suru.Tests;

// Verifies the IR backend's user-defined function support, including recursive calls.
//
// The fibonacci fixture defines two functions:
//   fn fibonacci(n Int64) Int64 — recursive, uses match-as-expression as its return value
//   fn main(args Array)         — calls fibonacci four times and prints the results
//
// What this exercises in IRCodeGenerator:
//
//   Typed function parameters — the pre-pass registers fibonacci's signature
//   (Int64 → Int64) in _userFunctions before any body is emitted. EmitFunction
//   then builds the LLVM parameter list from FunctionDeclaration.Parameters,
//   emitting alloca+store for each parameter in the entry block so the body can
//   read it via the standard EmitLoad path (same mechanism as let-bound locals).
//
//   User-defined call sites in expression position — the wildcard arm of
//   fibonacci's match expression contains:
//       fibonacci(n.take(1)).add(fibonacci(n.take(2)))
//   The outer .add() is a MethodCallExpression whose receiver is a CallExpression.
//   EmitValue now dispatches CallExpression to EmitUserFunctionCall, which reads
//   the registered signature to emit the correct LLVM types and returns the Suru
//   return type so the enclosing .add() selects the right integer opcode.
//
//   Recursive self-calls — fibonacci calls itself in its own body. LLVM IR
//   definitions are visible module-wide regardless of textual order, so no
//   forward declaration is needed; the pre-pass registration is sufficient.
//
//   match-as-expression as a return value — `return match n.lt(2) { true: n, _: … }`
//   chains EmitMatchAsExpression into ReturnStatement handling. The match result
//   alloca is loaded after the merge label, and the loaded value goes directly
//   into the `ret i64` instruction.
[Collection("IntegrationIR")]
public class IRFibonacciTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("fibonacci");
    private bool _testPassed;

    [Fact]
    public void Fibonacci_PrintsExpectedOutput()
    {
        Assert.Equal("0\n1\n5\n55\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("fibonacci"); }
}
