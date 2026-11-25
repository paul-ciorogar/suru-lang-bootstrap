namespace Suru.Tests.Compiler.Testing;

/// <summary>
/// Directives inside <c>fn</c> bodies: a body runs once per call, so a directive there can report
/// many times or never — the cases a loop raised first, answered by the same id-keyed rules.
/// </summary>
[Collection("Integration")]
public class DirectivesInFunctionTests
{
    private readonly CompiledFixtures _fixtures;

    public DirectivesInFunctionTests(CompiledFixtures fixtures) => _fixtures = fixtures;

    [Fact]
    public void RunsTheProgramWithItsMocks()
    {
        // A frame from a body that no directive claimed used to end the run with an error and
        // leave the source unwritten; getting any output back at all is the regression check.
        Assert.Equal("14\n7\n41\n1\n", _fixtures.GetTestRun("directives-in-function").Result.Output);
    }

    [Fact]
    public void AViewInAFunctionCalledThreeTimesShowsTheLastCall()
    {
        var source = _fixtures.GetTestRun("directives-in-function").Source;

        Assert.Contains("  #view result: 9\n", source);
    }

    [Fact]
    public void AnAssertInAFunctionIsStickyAndKeepsTheValueThatFailed()
    {
        // Failed on the first call, held on the second, failed on the third.
        var source = _fixtures.GetTestRun("directives-in-function").Source;

        Assert.Contains("  #assert(result, 4): fail, got 1\n", source);
    }

    [Fact]
    public void ADirectiveInAFunctionIsCountedOnceHoweverOftenItIsCalled()
    {
        var result = _fixtures.GetTestRun("directives-in-function").Result;

        Assert.Equal(0, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(3, result.Views);
        Assert.Equal(3, result.Undefined);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void WritesUndefinedInAFunctionThatIsNeverCalled()
    {
        var source = _fixtures.GetTestRun("directives-in-function").Source;

        Assert.Contains("  #view n: undefined\n", source);
        Assert.Contains("  #assert(n, 0): undefined\n", source);
    }

    [Fact]
    public void WritesUndefinedPastAReturn()
    {
        var source = _fixtures.GetTestRun("directives-in-function").Source;

        Assert.Contains("  #view 2: undefined\n", source);
    }

    [Fact]
    public void AMockOverridesTheArgumentAParameterWasPassed()
    {
        var source = _fixtures.GetTestRun("directives-in-function").Source;

        Assert.Contains("  #view n: 7\n", source);
    }

    [Fact]
    public void FindsADirectiveInANestedFunction()
    {
        var source = _fixtures.GetTestRun("directives-in-function").Source;

        Assert.Contains("    #view m: 40\n", source);
    }

    [Fact]
    public void AnnotatingAFunctionsDirectivesIsIdempotent()
    {
        var run = _fixtures.GetTestRun("directives-in-function");

        Assert.Equal(run.Source, run.SourceAfterRerun);
    }
}
