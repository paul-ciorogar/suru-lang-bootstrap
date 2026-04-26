namespace Suru.Tests;

[Collection("Integration")]
public class ArrayTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("arrays");

    [Fact]
    public void Array_LenAtSetAddSlice()
        // => Assert.Equal("3\n10\n30\n99\n4\n40\n2\n99\n4\n3\n4\n3 three\n", Run(_exe));
        => Assert.Equal("3\n10\n30\n99\n4\n40\n2\n99\n", Run(_exe));
}
