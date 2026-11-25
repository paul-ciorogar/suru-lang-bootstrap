namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for <c>and</c> and <c>or</c>. The fixture calls a function that prints
/// on both sides of them, so an operand that ran when it should have been skipped is an extra
/// number in the output, and one that was skipped when it should have run is a missing one.
/// </summary>
[Collection("Integration")]
public class ShortCircuitTests
{
    private readonly string _exe;

    public ShortCircuitTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("short-circuit");

    [Fact]
    public void RunsTheRightSideOnlyWhenTheLeftDoesNotDecide()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            false
            true
            3
            false
            4
            true
            false
            7
            9
            false
            10
            11
            2
            13

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
