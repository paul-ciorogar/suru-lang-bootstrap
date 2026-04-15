namespace Suru.Tests;

[Collection("Integration")]
public class StructTests(CompiledFixtures fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("structs");

    [Fact]
    public void Struct_FieldReadWriteCloneDrop()
        => Assert.Equal("true\n2283\nfalse\nfalse\n", Run(_exe));
}
