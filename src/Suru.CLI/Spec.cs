using Suru.Compiler.Debug;
using Suru.Compiler.Testing;
using Suru.Lib;

namespace Suru.CLI;

/// <summary>Why a command line could not be read, in the words the user is shown.</summary>
public sealed record ArgsError(string Message);

/// <summary>
/// What a command line asked for, once every argument has been read. The flag names live
/// here rather than beside the usage text: the parser is what gives them meaning, and a
/// usage line naming a flag nothing parses is worse than none.
/// </summary>
public class Spec
{
    public CommandType Command = CommandType.None;
    public Dump Dump = Dump.None;
    public TestOptions Timeouts = TestOptions.Default;
    public bool Help = false;
    public string Source = "";

    public const string DumpFlag = "--dump";
    public const string DumpEnvironmentVariable = "SURU_DUMP";
    public const string TimeoutFlag = "--timeout";
    public const string ConnectTimeoutFlag = "--connect-timeout";
    public const string HelpFlag = "--help";
    public const string HelpSmallFlag = "-h";

    // Each step either hands the spec on or ends the parse: AndThen carries a failure
    // through untouched, so no step past the failing one has to ask whether one happened.
    public static Result<Spec, ArgsError> Parse(string[] args) =>
        ParseCommand(args, new Spec())
            .AndThen(ParseEnvironmentVariables)
            .AndThen(spec => ParseArgs(args, spec));

    // Every argument is another step in the same chain, so a flag that fails ends the parse
    // the way a failing stage does — the fold stops asking, rather than each branch checking.
    private static Result<Spec, ArgsError> ParseArgs(string[] args, Spec spec)
    {
        var result = Ok(spec);

        foreach (var (flag, value) in args.Skip(1).Select(SplitFlag))
        {
            // The two reasons to stop reading arguments, in the one place both are visible:
            // the parse failed, or --help already decided what this run does.
            if (result.Match(parsed => parsed.Help, _ => true)) break;

            result = result.AndThen(parsed => ParseArg(flag, value, parsed));
        }

        return result;
    }

    private static Result<Spec, ArgsError> ParseArg(string flag, string value, Spec spec)
    {
        if (flag is HelpFlag or HelpSmallFlag)
        {
            spec.Help = true;
            return Ok(spec);
        }

        if (flag is TimeoutFlag or ConnectTimeoutFlag)
        {
            return ParseTimeout(flag, value, spec);
        }

        if (flag.StartsWith(DumpFlag, StringComparison.Ordinal))
        {
            return ParseDump(flag, value, spec);
        }

        if (flag.StartsWith('-'))
        {
            return Fail($"unknown option '{flag}'");
        }

        spec.Source = flag;
        return Ok(spec);
    }

    private static Result<Spec, ArgsError> ParseDump(string flag, string value, Spec spec)
    {
        // Accepts --dump, --dump=tokens,ast and --dump-tokens alike.
        if (value.Length == 0)
        {
            value = flag[DumpFlag.Length..].TrimStart('=', '-');
        }

        if (value.Length == 0)
        {
            value = "all";
        }

        return ParseDumpStages(DumpFlag, value, spec);
    }

    private static Result<Spec, ArgsError> ParseTimeout(string flag, string value, Spec spec)
    {
        // Rejected on 'build' rather than accepted and ignored: a flag that silently
        // stops working is worse than one that was never there.
        if (spec.Command != CommandType.Test)
        {
            return Fail($"{flag} applies to 'suru test'");
        }

        if (!int.TryParse(value, out var milliseconds) || milliseconds <= 0)
        {
            return Fail($"{flag} expects a positive number of milliseconds");
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        spec.Timeouts = flag == TimeoutFlag
            ? spec.Timeouts with { Run = span }
            : spec.Timeouts with { Connect = span };

        return Ok(spec);
    }

    // Split on '=' before matching, so '--timeout' cannot be read as a prefix of
    // '--connect-timeout' or the other way about, whichever is tested first.
    private static (string Flag, string Value) SplitFlag(string arg)
    {
        var separatorIndex = arg.IndexOf('=');
        var flag = separatorIndex < 0 ? arg : arg[..separatorIndex];
        var value = separatorIndex < 0 ? "" : arg[(separatorIndex + 1)..];
        return (flag, value);
    }

    // Dumps are off unless asked for, by flag or by environment variable — the same
    // switch has to be reachable from a release build with no rebuild.
    private static Result<Spec, ArgsError> ParseEnvironmentVariables(Spec spec)
    {
        var environmentSpec = Environment.GetEnvironmentVariable(DumpEnvironmentVariable);
        if (string.IsNullOrEmpty(environmentSpec)) return Ok(spec);

        return ParseDumpStages(DumpEnvironmentVariable, environmentSpec, spec);
    }

    // Reading the stage list is DumpSpec's job; naming where the value came from is this
    // layer's, since the flag and the environment variable fail alike and are fixed in
    // different places. The two sources accumulate: neither replaces the other.
    private static Result<Spec, ArgsError> ParseDumpStages(string source, string value, Spec spec) =>
        DumpSpec.Parse(value)
            .MapError(error => new ArgsError($"{source}: {error.Message}"))
            .Map(dump =>
            {
                spec.Dump |= dump;
                return spec;
            });

    private static Result<Spec, ArgsError> ParseCommand(string[] args, Spec spec)
    {
        var commandText = args.Length > 0 ? args[0] : "";
        spec.Command = commandText switch
        {
            "build" => CommandType.Build,
            "test" => CommandType.Test,
            _ => CommandType.None
        };
        return Ok(spec);
    }

    // Named so a step reads as what it decided, not as which constructor it called.
    private static Result<Spec, ArgsError> Ok(Spec spec) => Result.Ok<Spec, ArgsError>(spec);

    private static Result<Spec, ArgsError> Fail(string message) =>
        Result.Error<Spec, ArgsError>(new ArgsError(message));
}

public enum CommandType
{
    None, Build, Test
}
