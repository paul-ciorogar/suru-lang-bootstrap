using Suru.CLI;
using Suru.Compiler.Debug;
using Suru.Compiler.Testing;

namespace Suru.Tests.CLI;

/// <summary>
/// <see cref="Spec.Parse"/> is the whole of the CLI's logic: everything downstream of it
/// either runs the compiler or prints. The tests read a command line and assert on the spec
/// it produced, or on the one sentence it failed with.
/// <para>
/// SURU_DUMP is process-wide, so every test in this class runs with it cleared and the ones
/// that care set it themselves. They live in one class for that reason — xUnit runs the
/// tests within a class one at a time, so no two of them can be holding the variable at once.
/// </para>
/// </summary>
public class SpecTests : IDisposable
{
    public SpecTests() => ClearDumpVariable();

    public void Dispose()
    {
        ClearDumpVariable();
        GC.SuppressFinalize(this);
    }

    private static void ClearDumpVariable() =>
        Environment.SetEnvironmentVariable(Spec.DumpEnvironmentVariable, null);

    private static void SetDumpVariable(string value) =>
        Environment.SetEnvironmentVariable(Spec.DumpEnvironmentVariable, value);

    // The two unwraps every test goes through, so a test that expected the other side says
    // what it got rather than failing on a null.
    private static Spec Parsed(params string[] args) =>
        Spec.Parse(args).Match(
            spec => spec,
            error => throw new Xunit.Sdk.XunitException($"expected a spec, got error: {error.Message}"));

    private static string Error(params string[] args) =>
        Spec.Parse(args).Match(
            spec => throw new Xunit.Sdk.XunitException($"expected an error, got a spec for '{spec.Source}'"),
            error => error.Message);

    [Fact]
    public void BuildIsACommandWithASource()
    {
        var spec = Parsed("build", "main.suru");

        Assert.Equal(CommandType.Build, spec.Command);
        Assert.Equal("main.suru", spec.Source);
        Assert.Equal(Dump.None, spec.Dump);
        Assert.False(spec.Help);
        Assert.Equal(TestOptions.Default, spec.Timeouts);
    }

    [Fact]
    public void TestIsACommandWithASource()
    {
        var spec = Parsed("test", "main.suru");

        Assert.Equal(CommandType.Test, spec.Command);
        Assert.Equal("main.suru", spec.Source);
    }

    [Fact]
    public void NoArgumentsIsNoCommand()
    {
        var spec = Parsed();

        Assert.Equal(CommandType.None, spec.Command);
        Assert.Equal("", spec.Source);
        Assert.False(spec.Help);
    }

    // Not an error: 'suru frobnicate' is answered with the usage text, which lists what
    // there is instead. Only a flag nobody recognises is worth a sentence of its own.
    [Fact]
    public void AnUnknownCommandIsNoCommand()
    {
        Assert.Equal(CommandType.None, Parsed("frobnicate", "main.suru").Command);
    }

    // The command is read from the first argument only, so a file named 'build' is a file.
    [Fact]
    public void OnlyTheFirstArgumentIsACommand()
    {
        var spec = Parsed("build", "test");

        Assert.Equal(CommandType.Build, spec.Command);
        Assert.Equal("test", spec.Source);
    }

    [Fact]
    public void TheLastSourceGivenWins()
    {
        Assert.Equal("second.suru", Parsed("build", "first.suru", "second.suru").Source);
    }

    [Theory]
    [InlineData(Spec.HelpFlag)]
    [InlineData(Spec.HelpSmallFlag)]
    public void HelpIsAskedForByEitherSpelling(string flag)
    {
        Assert.True(Parsed("build", flag).Help);
    }

    // --help ends the parse where it stands: nothing after it can change what the run does,
    // so a mistyped flag behind it is not worth refusing to print the usage over.
    [Fact]
    public void HelpStopsTheParse()
    {
        var spec = Parsed("build", "-h", "--bogus", "main.suru");

        Assert.True(spec.Help);
        Assert.Equal("", spec.Source);
    }

    [Fact]
    public void AnUnknownOptionIsAnError()
    {
        Assert.Equal("unknown option '--bogus'", Error("build", "--bogus", "main.suru"));
    }

    // One sentence, not a list: the arguments after a bad one were read in the light of it,
    // so what they would have meant is not worth guessing at.
    [Fact]
    public void TheFirstErrorEndsTheParse()
    {
        Assert.Equal("unknown option '--bogus'", Error("build", "--bogus", "--dump=nope"));
    }

    [Fact]
    public void DumpWithNoStagesIsEveryStage()
    {
        Assert.Equal(Dump.All, Parsed("build", "--dump", "main.suru").Dump);
    }

    [Fact]
    public void DumpTakesAStageList()
    {
        Assert.Equal(Dump.Tokens | Dump.Ast, Parsed("build", "--dump=tokens,ast", "main.suru").Dump);
    }

    [Fact]
    public void DumpTakesTheHyphenatedShorthand()
    {
        Assert.Equal(Dump.Llvm, Parsed("build", "--dump-llvm", "main.suru").Dump);
    }

    [Fact]
    public void RepeatedDumpsAccumulate()
    {
        Assert.Equal(
            Dump.Tokens | Dump.Llvm,
            Parsed("build", "--dump-tokens", "--dump=llvm", "main.suru").Dump);
    }

    // The error names the flag, not the environment variable: the two fail the same way and
    // are fixed in different places.
    [Fact]
    public void AnUnknownDumpStageOnTheFlagIsAnError()
    {
        Assert.Equal(
            $"--dump: unknown dump stage 'nope'; expected one of: {DumpSpec.Names}",
            Error("build", "--dump=nope", "main.suru"));
    }

    [Fact]
    public void TheEnvironmentVariableSetsStagesToo()
    {
        SetDumpVariable("ast");

        Assert.Equal(Dump.Ast, Parsed("build", "main.suru").Dump);
    }

    [Fact]
    public void TheEnvironmentVariableAndTheFlagAccumulate()
    {
        SetDumpVariable("ast");

        Assert.Equal(Dump.Ast | Dump.Tokens, Parsed("build", "--dump=tokens", "main.suru").Dump);
    }

    [Fact]
    public void AnEmptyEnvironmentVariableIsNotAskingForAnything()
    {
        SetDumpVariable("");

        Assert.Equal(Dump.None, Parsed("build", "main.suru").Dump);
    }

    [Fact]
    public void AnUnknownDumpStageInTheEnvironmentIsAnError()
    {
        SetDumpVariable("nope");

        Assert.Equal(
            $"{Spec.DumpEnvironmentVariable}: unknown dump stage 'nope'; expected one of: {DumpSpec.Names}",
            Error("build", "main.suru"));
    }

    // The environment is read before the arguments, so a bad SURU_DUMP is reported even
    // when the command line that would have overridden nothing is itself fine.
    [Fact]
    public void TheEnvironmentIsReadBeforeTheArguments()
    {
        SetDumpVariable("nope");

        Assert.StartsWith(Spec.DumpEnvironmentVariable, Error("build", "--bogus"));
    }

    [Fact]
    public void TimeoutSetsHowLongTheProgramMayRun()
    {
        var spec = Parsed("test", "--timeout=5000", "main.suru");

        Assert.Equal(TimeSpan.FromMilliseconds(5000), spec.Timeouts.Run);
        Assert.Equal(TestOptions.Default.Connect, spec.Timeouts.Connect);
    }

    [Fact]
    public void ConnectTimeoutSetsHowLongItHasToConnect()
    {
        var spec = Parsed("test", "--connect-timeout=250", "main.suru");

        Assert.Equal(TimeSpan.FromMilliseconds(250), spec.Timeouts.Connect);
        Assert.Equal(TestOptions.Default.Run, spec.Timeouts.Run);
    }

    // Split on '=' before matching, so neither flag can be read as a prefix of the other.
    [Fact]
    public void TheTwoDeadlinesAreSetIndependently()
    {
        var spec = Parsed("test", "--timeout=5000", "--connect-timeout=250", "main.suru");

        Assert.Equal(TimeSpan.FromMilliseconds(5000), spec.Timeouts.Run);
        Assert.Equal(TimeSpan.FromMilliseconds(250), spec.Timeouts.Connect);
    }

    // Rejected on 'build' rather than accepted and ignored: a flag that silently stops
    // working is worse than one that was never there.
    [Theory]
    [InlineData(Spec.TimeoutFlag)]
    [InlineData(Spec.ConnectTimeoutFlag)]
    public void ADeadlineOutsideTestIsAnError(string flag)
    {
        Assert.Equal($"{flag} applies to 'suru test'", Error("build", $"{flag}=5000", "main.suru"));
    }

    [Theory]
    [InlineData("--timeout=zero")]
    [InlineData("--timeout=0")]
    [InlineData("--timeout=-1")]
    [InlineData("--timeout")]
    public void ADeadlineThatIsNotAPositiveNumberIsAnError(string argument)
    {
        Assert.Equal(
            "--timeout expects a positive number of milliseconds",
            Error("test", argument, "main.suru"));
    }
}
