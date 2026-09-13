namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for loops. The fixture prints a sequence that only comes out in this
/// order if every edge lands where it should: a missing number is an iteration that was
/// skipped, an extra one is a <c>break</c> or <c>continue</c> that went to the wrong block,
/// and a hang is a condition emitted once instead of on every pass.
/// </summary>
[Collection("Integration")]
public class WhileTests
{
    private readonly string _exe;

    public WhileTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("while");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            1
            2
            4
            5
            6
            1
            1
            1
            2
            2
            1
            2
            3
            3
            0
            4
            0
            2
            4
            4

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
