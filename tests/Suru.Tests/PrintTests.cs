namespace Suru.Tests;

[Collection("Integration")]
public class PrintTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("print");

    [Fact]
    public void PrintsExpectedOutput()
        => Assert.Equal("true\nfalse\n1\n1.2\n", Run(_exe));
}
