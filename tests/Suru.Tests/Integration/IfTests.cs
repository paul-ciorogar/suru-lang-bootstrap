namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for branches: the fixture prints 1 to 10 in order, and only in order
/// if every edge lands in the block it should. A missing number is an arm that was skipped
/// when it should have run; an extra one is an arm that ran when it should not have.
/// </summary>
[Collection("Integration")]
public class IfTests
{
    private readonly string _exe;

    public IfTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("if");

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
            true
            false
            9
            10

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
