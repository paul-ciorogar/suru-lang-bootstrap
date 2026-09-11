using Suru.Compiler.Debug;
using Suru.Compiler.Testing;
using Suru.Lib;

namespace Suru.CLI;

// A marker: what a command *is* comes from parsing, what it *does* is the method the
// entry point's switch picks out by type.
public interface ICommand
{
    int Execute();
}

public static class Command
{
    // Dumps go to stderr so stdout stays usable for the build result.
    internal static Suru.Compiler.Compiler CompilerFor(string sourcePath, Dump dump) =>
        new(sourcePath, new DumpOptions(dump, Console.Error));

    internal static string BuildDirectoryFor(string sourcePath) =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, "build");

    // The one place the two outcomes of parsing meet: a spec becomes the command it names,
    // an error becomes the command that reports it.
    public static ICommand From(Result<Spec, ArgsError> parsed) =>
        parsed.Match<ICommand>(From, error => UssageCommand.Error(error.Message));

    private static ICommand From(Spec spec)
    {
        if (spec.Help) return UssageCommand.Help();
        return spec.Command switch
        {
            CommandType.Build => new BuildCommand(spec.Source, spec.Dump),
            CommandType.Test => new TestCommand(spec.Source, spec.Dump, spec.Timeouts),
            _ => UssageCommand.Help()
        };
    }
}

public sealed class BuildCommand(string sourcePath, Dump dump) : ICommand
{
    public int Execute()
    {
        var compiler = Command.CompilerFor(sourcePath, dump);
        var result = compiler.Compile(Command.BuildDirectoryFor(sourcePath));

        if (!result.Success)
        {
            foreach (var error in result.Errors)
                Console.Error.WriteLine($"error: {error}");
            return 1;
        }

        Console.WriteLine($"Built: {result.OutputPath}");
        return 0;
    }
}

public sealed class TestCommand(string sourcePath, Dump dump, TestOptions timeouts) : ICommand
{
    public int Execute()
    {
        var compiler = Command.CompilerFor(sourcePath, dump);
        var run = compiler.Test(Command.BuildDirectoryFor(sourcePath), timeouts);

        if (run.Errors.Count > 0)
        {
            foreach (var error in run.Errors)
                Console.Error.WriteLine($"error: {error}");
            return 1;
        }

        // The program's own output first, verbatim, so a test run reads like an ordinary run.
        Console.Write(run.Output);

        foreach (var failure in run.Failures)
            Console.Error.WriteLine($"error: {failure}");

        // Views are reported by count: their values went into the source file, which is where
        // they are meant to be read.
        var views = run.Views == 1 ? "1 view" : $"{run.Views} views";
        Console.Error.WriteLine(
            $"{run.Passed} passed, {run.Failed} failed, {run.Undefined} undefined, " +
            $"{views} written to {sourcePath}");

        // A run that never said it finished is not a run with results missing — it is a run
        // whose remaining directives are unknown, and their lines were blanked to say so.
        if (run.Crashed)
            Console.Error.WriteLine(
                $"error: the run did not finish: the program stopped before {run.Unreported} " +
                $"{(run.Unreported == 1 ? "directive" : "directives")} reported");

        if (run.ExitCode != 0)
            Console.Error.WriteLine($"error: the program exited with {run.ExitCode}");

        return run.Success ? 0 : 1;
    }
}

public sealed class UssageCommand : ICommand
{
    private readonly string? _error = null;
    private readonly bool _withUsage = false;
    private readonly int _exitCode = 0;

    private UssageCommand()
    {
        _withUsage = true;
    }

    private UssageCommand(string error, int exitCode)
    {
        _error = error;
        _exitCode = exitCode;
    }

    // Asked for: it goes to stdout and the run succeeded.
    public static UssageCommand Help() => new();

    // Fallen into: it goes to stderr, and without the usage text — a message specific
    // enough to act on stands on its own, and burying it under thirty lines hides it.
    public static UssageCommand Error(string error)
    {
        return new(error, exitCode: 1);
    }

    public int Execute()
    {
        var writer = _exitCode == 0 ? Console.Out : Console.Error;

        if (_error is not null)
            writer.WriteLine($"error: {_error}");
        if (_withUsage)
            writer.WriteLine(Message);

        return _exitCode;
    }

    public string Message => $"""
    Usage: suru <command> [options] <file.suru>

    Commands:
      build             compile to a native executable; '#' directives are ignored
      test              compile with the '#' directives live, run the result, and
                        write each '#view' and '#assert' result into the source

    Options:
      --dump=<stages>   write the named compiler stages to stderr
      --dump-<stage>    equivalent shorthand for a single stage
      --dump            all stages

    Options for 'test':
      {Spec.TimeoutFlag}=<ms>    how long the program may run once it has reached the
                        test channel, before it is killed (default {TestOptions.Default.Run.TotalMilliseconds:0})
      {Spec.ConnectTimeoutFlag}=<ms>
                        how long the program has to reach the test channel at all
                        (default {TestOptions.Default.Connect.TotalMilliseconds:0})

    Two deadlines rather than one: a binary that never connected and a program that
    never ended are different failures, and one number could only hedge between them.

    Stages: {DumpSpec.Names}

    {Spec.DumpEnvironmentVariable} holds the same stage list and applies to every run.
    """;
}
