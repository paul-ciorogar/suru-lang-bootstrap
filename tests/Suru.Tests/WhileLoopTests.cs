namespace Suru.Tests;

[Collection("Integration")]
public class WhileLoopTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("while-loop");

    [Fact]
    public void WhileLoop_PrintsExpectedOutput()
        => Assert.Equal("1\n2\n3\n4\n5\n55\nxxx\n", Run(_exe));
}
