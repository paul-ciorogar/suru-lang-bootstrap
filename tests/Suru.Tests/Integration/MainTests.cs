namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for the entry point: every line comes from inside <c>main</c>, so any
/// output at all means the C entry point called it, and the second line means a call from it
/// reached a function declared below it.
/// </summary>
[Collection("Integration")]
public class MainTests
{
    private readonly string _exe;

    public MainTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("main");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            1
            21

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
