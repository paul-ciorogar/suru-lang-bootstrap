using Suru.CLI;
using Suru.Compiler.Debug;
using Suru.Compiler.Testing;

var spec = Spec.Parse(args);
var command = Command.From(spec);

return command.Execute();

internal class Spec
{
    public CommandType Command = CommandType.None;
    public string? ErrorMsg = null;
    public Dump Dump = Dump.None;
    public TestOptions Timeouts = TestOptions.Default;
    public bool Help = false;
    public string Source = "";

    private const string _dumpEnvironmentVariable = "SURU_DUMP";
    private const string DumpFlag = "--dump";
    private const string DumpEnvironmentVariable = "SURU_DUMP";
    private const string TimeoutFlag = "--timeout";
    private const string ConnectTimeoutFlag = "--connect-timeout";
    private const string HelpFlag = "--help";
    private const string HelpSmallFlag = "-h";


    public Spec() { }

    internal static Spec Parse(string[] args)
    {
        var spec = new Spec();
        spec = ParseCommand(args, spec);
        spec = ParseEnvironmentVariables(spec);
        spec = ParseArgs(args, spec);
        
        return spec;
    }

    private static Spec ParseArgs(string[] args, Spec spec)
    {

        foreach (var (flag, value) in args.Skip(1).Select(SplitFlag))
        {
            if (spec.HasError()) return spec;

            if (flag is HelpFlag or HelpSmallFlag)
            {
                spec.Help = true;
                return spec;
            }

            if (flag is TimeoutFlag or ConnectTimeoutFlag)
            {
                spec = ParseTimeout(flag, value, spec);
                continue;
            }

            if (flag.StartsWith(DumpFlag, StringComparison.Ordinal))
            {
                spec = ParseDump(flag, value, spec);
                continue;
            }

            if (flag.StartsWith('-'))
            {
                spec.ErrorMsg = $"unknown option '{flag}'";
                return spec;
            }

            spec.Source = flag;

        }
        
        return spec;
    }

    private static Spec ParseDump(string flag, string value, Spec spec)
    {
        if (spec.HasError()) return spec;

        // Accepts --dump, --dump=tokens,ast and --dump-tokens alike.
        if (value.Length == 0)
        {
            value = flag[DumpFlag.Length..].TrimStart('=', '-');
        }

        if (value.Length == 0)
        {
            value = "all";
        }

        return ParseDumpStages(value, spec);
    }

    private static Spec ParseTimeout(string flag, string value, Spec spec)
    {
        // Rejected on 'build' rather than accepted and ignored: a flag that silently
        // stops working is worse than one that was never there.
        if (spec.Command != CommandType.Test)
        {
            spec.ErrorMsg = $"{flag} applies to 'suru test'";
            return spec;
        }

        if (!int.TryParse(value, out var milliseconds) || milliseconds <= 0)
        {
            spec.ErrorMsg = $"{flag} expects a positive number of milliseconds";
            return spec;
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        spec.Timeouts = flag == TimeoutFlag
            ? spec.Timeouts with { Run = span }
            : spec.Timeouts with { Connect = span };

        return spec;
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
    private static Spec ParseEnvironmentVariables(Spec spec)
    {
        if (spec.HasError()) return spec;

        var environmentSpec = Environment.GetEnvironmentVariable(_dumpEnvironmentVariable);
        if (environmentSpec == null || environmentSpec.Length == 0) return spec;

        return ParseDumpStages(environmentSpec, spec);
    }

    private static Spec ParseDumpStages(string value, Spec spec)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var name = part.ToLowerInvariant();
            var match = Array.Find(DumpSpec.Stages, stage => stage.Name == name);
            if (match.Stage == Dump.None)
            {
                spec.ErrorMsg = $"{_dumpEnvironmentVariable}: unknown dump stage '{part}'; expected one of: {DumpSpec.Names}";
                return spec;
            }
            spec.Dump |= match.Stage;
        }

        return spec;
    }

    private static Spec ParseCommand(string[] args, Spec spec)
    {
        var commandText = args.Length > 0 ? args[0] : "";
        spec.Command = commandText switch
        {
            "build" => CommandType.Build,
            "test" => CommandType.Test,
            _ => CommandType.None
        };
        return spec;
    }

    public bool HasError()
    {
        return ErrorMsg != null;
    }
}

internal enum CommandType
{
    None, Build, Test
}