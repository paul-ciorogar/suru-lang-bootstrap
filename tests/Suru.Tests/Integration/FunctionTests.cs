namespace Suru.Tests.Integration;

/// <summary>
/// The end-to-end check for user-defined functions. The fixture prints a sequence that only
/// comes out in this order if every call lands in the right body with its arguments in the right
/// slots and every <c>return</c> leaves where it should: a value missing or repeated is a return
/// that fell through, and a compile failure is IR the verifier refused — most likely the end of a
/// body whose last block was left open.
/// </summary>
[Collection("Integration")]
public class FunctionTests
{
    private readonly string _exe;

    public FunctionTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("functions");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal(
            """
            1
            1
            20
            7
            120
            true
            true
            false
            42
            -1
            0
            1
            8
            25
            3
            2
            1
            3
            25
            2.5
            true
            5

            """.ReplaceLineEndings("\n"),
            stdout);
    }
}
