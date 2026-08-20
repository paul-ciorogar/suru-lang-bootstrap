namespace Suru.Tests;

[Collection("Integration")]
public class PrintTests
{
    private readonly string _exe;

    public PrintTests(CompiledFixtures fixtures)
        => _exe = fixtures.GetExecutable("print");

    [Fact]
    public void PrintsExpectedOutput()
    {
        var stdout = Executable.Run(_exe);
        Assert.Equal("true\nfalse\n1\n1.2\n", stdout);
    }
}
