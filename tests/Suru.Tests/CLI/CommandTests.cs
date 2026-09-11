using Suru.CLI;
using Suru.Lib;

namespace Suru.Tests.CLI;

/// <summary>
/// <see cref="Command.From"/> only chooses: what each command then does is the compiler,
/// which the integration tests drive end to end. So these assert the choice, and the output
/// of the one command that is nothing but output.
/// </summary>
public class CommandTests
{
    private static ICommand From(Spec spec) => Command.From(Result.Ok<Spec, ArgsError>(spec));

    private static ICommand From(string error) =>
        Command.From(Result.Error<Spec, ArgsError>(new ArgsError(error)));

    [Fact]
    public void BuildBecomesABuildCommand()
    {
        Assert.IsType<BuildCommand>(From(new Spec { Command = CommandType.Build, Source = "main.suru" }));
    }

    [Fact]
    public void TestBecomesATestCommand()
    {
        Assert.IsType<TestCommand>(From(new Spec { Command = CommandType.Test, Source = "main.suru" }));
    }

    // No command is not a failure to parse — it is a run that has not said what it wants,
    // and the usage text is the answer to that.
    [Fact]
    public void NoCommandBecomesTheUsageText()
    {
        Assert.IsType<UssageCommand>(From(new Spec { Command = CommandType.None }));
    }

    // Help outranks the command: 'suru build --help' asks what build does, it does not build.
    [Fact]
    public void HelpOutranksTheCommand()
    {
        Assert.IsType<UssageCommand>(
            From(new Spec { Command = CommandType.Build, Source = "main.suru", Help = true }));
    }

    [Fact]
    public void AnArgsErrorBecomesTheUsageCommand()
    {
        Assert.IsType<UssageCommand>(From("unknown option '--bogus'"));
    }

    // Asked for: stdout, and the run succeeded.
    [Fact]
    public void HelpIsPrintedToStandardOutput()
    {
        var (exitCode, output, errors) = Run(UssageCommand.Help());

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage: suru <command> [options] <file.suru>", output);
        Assert.Equal("", errors);
    }

    // Fallen into: stderr, and without the usage text buried underneath it.
    [Fact]
    public void AnErrorIsPrintedToStandardErrorAlone()
    {
        var (exitCode, output, errors) = Run(UssageCommand.Error("unknown option '--bogus'"));

        Assert.Equal(1, exitCode);
        Assert.Equal("", output);
        Assert.Contains("error: unknown option '--bogus'", errors);
        Assert.DoesNotContain("Usage:", errors);
    }

    // Every flag the usage text names has to be one the parser answers to, or the help is
    // a lie. They are the same constants, which is what makes that true rather than checked.
    [Fact]
    public void TheUsageTextNamesTheFlagsTheParserReads()
    {
        var usage = UssageCommand.Help().Message;

        Assert.Contains(Spec.TimeoutFlag, usage);
        Assert.Contains(Spec.ConnectTimeoutFlag, usage);
        Assert.Contains(Spec.DumpEnvironmentVariable, usage);
    }

    private static (int ExitCode, string Output, string Errors) Run(ICommand command)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var previousOutput = Console.Out;
        var previousErrors = Console.Error;

        try
        {
            Console.SetOut(output);
            Console.SetError(errors);
            return (command.Execute(), output.ToString(), errors.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousErrors);
        }
    }
}
