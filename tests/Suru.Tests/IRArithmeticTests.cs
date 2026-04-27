namespace Suru.Tests;

// Verifies the IR backend's arithmetic method calls:
//   add / take / multiply / split (binary), invert (unary)
// and explicit Int32 type annotation (`let p Int32: 7`).
// The fixture prints results via printLn which is already covered by IRPrintTests.
[Collection("IntegrationIR")]
public class IRArithmeticTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("arithmetic");
    private bool _testPassed;

    [Fact]
    public void PrintsExpectedOutput()
    {
        Assert.Equal("5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("arithmetic"); }
}
