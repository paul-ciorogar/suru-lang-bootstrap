namespace Suru.Tests;

[Collection("Integration")]
public class SemanticErrorTests(CompiledFixtures fixtures)
{
    [Fact]
    public void ReportsUnknownFunction()
    {
        var errors = fixtures.GetErrors("unknown-function");

        var error = Assert.Single(errors);
        Assert.EndsWith("(2,1): unknown function 'foo'", error);
        Assert.Contains("unknown-function", error);
    }

    [Fact]
    public void ReportsWrongArity()
    {
        var errors = fixtures.GetErrors("wrong-arity");

        Assert.Collection(errors,
            error => Assert.EndsWith("(1,1): 'printLn' expects 1 argument, got 2", error),
            error => Assert.EndsWith("(2,1): 'printLn' expects 1 argument, got 0", error));
    }

    [Fact]
    public void ReportsUnprintableArgument()
    {
        var errors = fixtures.GetErrors("unprintable-argument");

        var error = Assert.Single(errors);
        Assert.EndsWith(
            "(1,9): 'printLn' cannot print a value of type 'void'; expected 'bool', 'i64', 'f64'",
            error);
    }

    [Fact]
    public void ReportsNonCallStatement()
    {
        var errors = fixtures.GetErrors("non-call-statement");

        var error = Assert.Single(errors);
        Assert.EndsWith("(1,1): only call expressions are allowed as statements", error);
    }

    [Fact]
    public void ErrorsAreReportedWithTheSourcePathAndPosition()
    {
        var error = Assert.Single(fixtures.GetErrors("non-call-statement"));
        Assert.Matches(@"^.+main\.suru\(\d+,\d+\): ", error);
    }
}
