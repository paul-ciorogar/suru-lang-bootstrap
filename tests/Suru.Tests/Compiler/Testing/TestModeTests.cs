namespace Suru.Tests.Compiler.Testing;

/// <summary>
/// The <c>#</c> directives end to end: the same fixture compiled both ways, run, and its
/// source read back to see what the run wrote into it.
/// </summary>
[Collection("Integration")]
public class TestModeTests
{
    private readonly CompiledFixtures _fixtures;

    public TestModeTests(CompiledFixtures fixtures) => _fixtures = fixtures;

    [Fact]
    public void ProductionBuildRunsAsIfTheDirectivesWereComments()
    {
        // width is 3, not the mocked 10, so the area is 3 * 4.
        Assert.Equal("12\n", Executable.Run(_fixtures.GetExecutable("directives")));
    }

    [Fact]
    public void TestBuildAppliesTheMocks()
    {
        // width is 10, so the area is 10 * 4 — and printLn still prints exactly once.
        Assert.Equal("40\n", _fixtures.GetTestRun("directives").Result.Output);
    }

    [Fact]
    public void ReportsEveryAssertion()
    {
        var result = _fixtures.GetTestRun("directives").Result;

        Assert.Equal(5, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.False(result.Success);
    }

    [Fact]
    public void AnnotatesADirectiveInsideATakenBranch()
    {
        // Without walking into the arm these two records would have nothing to annotate and
        // would be dropped, taking a failing assertion with them.
        var source = _fixtures.GetTestRun("directives").Source;

        Assert.Contains("  #view area: 40\n", source);
        Assert.Contains("  #assert(area, 40): pass\n", source);
    }

    [Fact]
    public void WritesUndefinedForADirectiveInAnUntakenBranch()
    {
        var run = _fixtures.GetTestRun("directives");

        Assert.Contains("  #view area: undefined\n", run.Source);
        Assert.Contains("  #assert(area, 0): undefined\n", run.Source);
        Assert.Equal(2, run.Result.Undefined);
    }

    [Fact]
    public void AnUnreachedAssertIsNotAFailure()
    {
        // An assertion in a branch the run does not take is honest, not broken: it is counted
        // apart from the passes and failures rather than being either.
        var result = _fixtures.GetTestRun("directives").Result;

        Assert.Single(result.Failures);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void PositionsAFailureAtItsDirective()
    {
        var run = _fixtures.GetTestRun("directives");

        var failure = Assert.Single(run.Result.Failures);
        Assert.EndsWith("main.suru(16,1): assert failed: expected 12, got 40", failure);
    }

    [Fact]
    public void WritesEveryResultBackIntoTheSource()
    {
        var source = _fixtures.GetTestRun("directives").Source;

        Assert.Contains("#view area: 40\n", source);
        Assert.Contains("#view area + 1: 41\n", source);
        Assert.Contains("#view ratio: 1.5\n", source);
        Assert.Contains("#view ready: true\n", source);
        Assert.Contains("#assert(area, 40): pass\n", source);
        Assert.Contains("#assert(area, 12): fail, got 40\n", source);
        Assert.Equal(6, _fixtures.GetTestRun("directives").Result.Views);
    }

    [Fact]
    public void AnnotatingIsIdempotent()
    {
        // The second run had to re-parse lines the first one annotated, including
        // 'fail, got 40', which is not an expression and cannot even be lexed.
        var run = _fixtures.GetTestRun("directives");

        Assert.Equal(run.Source, run.SourceAfterRerun);
    }

    [Fact]
    public void LeavesTheCodeAroundTheDirectivesAlone()
    {
        var run = _fixtures.GetTestRun("directives");

        Assert.Contains("let area i64: width * height\n", run.Source);
        Assert.Contains("printLn(area)\n", run.Source);
    }
}
