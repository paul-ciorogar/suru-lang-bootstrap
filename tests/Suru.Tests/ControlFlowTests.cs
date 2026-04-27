namespace Suru.Tests;

[Collection("Integration")]
public class ControlFlowTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("control-flow");

    [Fact]
    public void ControlFlow_PrintsExpectedOutput()
        => Assert.Equal("1\n1\ntrue\ntrue\nfalse\n1\n1\n-1\nFriday\nFriday\n", Run(_exe));
}
