namespace Suru.Tests;

[Collection("Integration")]
public class NegativeLiteralTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("negative-literals");

    [Fact]
    public void PrintsExpectedOutput()
        => Assert.Equal("-5\n-2.5\n-8\n", Run(_exe));
}
