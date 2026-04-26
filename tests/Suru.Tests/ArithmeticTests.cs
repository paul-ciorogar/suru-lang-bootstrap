namespace Suru.Tests;

[Collection("Integration")]
public class ArithmeticTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("arithmetic");

    [Fact]
    public void ArithmeticAndVariables_PrintsExpectedOutput()
        => Assert.Equal("5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n", Run(_exe));
}
