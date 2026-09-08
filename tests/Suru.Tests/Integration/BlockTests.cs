namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for blocks: eight nested scopes each shadow 'x', so the output
/// ramps up on the way in and back down on the way out only if every level keeps its own
/// stack slot.
/// </summary>
[Collection("Integration")]
public class BlockTests
{
    private readonly string _exe;

    public BlockTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("blocks");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            1
            2
            3
            4
            5
            6
            7
            8
            108
            7
            6
            5
            4
            3
            2
            1

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
