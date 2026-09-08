namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for bindings and operators: the fixture is compiled,
/// linked and run, and its output asserted in full.
/// </summary>
[Collection("Integration")]
public class ExpressionTests
{
    private readonly string _exe;

    public ExpressionTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("expressions");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            9
            4
            1
            -4
            7
            1.5
            3
            true
            false
            true
            true
            6

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
