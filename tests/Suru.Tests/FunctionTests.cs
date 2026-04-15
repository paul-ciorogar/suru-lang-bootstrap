namespace Suru.Tests;

[Collection("Integration")]
public class FunctionTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("fibonacci");

    [Fact]
    public void Fibonacci_PrintsExpectedOutput()
        => Assert.Equal("0\n1\n5\n55\n", Run(_exe));
}
