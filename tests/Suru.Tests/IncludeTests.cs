namespace Suru.Tests;

[Collection("Integration")]
public class IncludeTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("include-test");

    [Fact]
    public void Include_CallsNamespacedFunctions()
        => Assert.Equal("42\nHello, Suru!\n", Run(_exe));
}
