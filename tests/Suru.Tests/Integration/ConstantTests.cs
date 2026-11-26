namespace Suru.Tests.Integration;

/// <summary>
/// File-level constants end to end: each type and sign prints as written, and a function
/// declared above the constants and one nested in <c>main</c> both read them.
/// </summary>
[Collection("Integration")]
public class ConstantTests
{
    private readonly string _exe;

    public ConstantTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("constants");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            12
            -3
            2.5
            -0.25
            true
            21
            4.75

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
