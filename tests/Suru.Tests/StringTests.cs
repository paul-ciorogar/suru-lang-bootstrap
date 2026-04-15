namespace Suru.Tests;

[Collection("Integration")]
public class StringTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("strings");

    [Fact]
    public void String_LenEqualsAppendSliceAtFromToString()
        => Assert.Equal("5\ntrue\nfalse\nhello world\nel\n42\n42\nh\n", Run(_exe));
}
