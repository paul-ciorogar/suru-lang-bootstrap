using Suru.Compiler;
using Suru.Compiler.Debug;

const string DumpFlag = "--dump";
const string DumpEnvironmentVariable = "SURU_DUMP";

var usage = $"""
    Usage: suru build [options] <file.suru>

    Options:
      --dump=<stages>   write the named compiler stages to stderr
      --dump-<stage>    equivalent shorthand for a single stage
      --dump            all stages

    Stages: {DumpSpec.Names}

    {DumpEnvironmentVariable} holds the same stage list and applies to every run.
    """;

if (args.Length < 1 || args[0] != "build")
{
    Console.Error.WriteLine(usage);
    return 1;
}

var dump = Dump.None;

// Dumps are off unless asked for, by flag or by environment variable — the same
// switch has to be reachable from a release build with no rebuild.
if (Environment.GetEnvironmentVariable(DumpEnvironmentVariable) is { Length: > 0 } environmentSpec)
{
    if (!DumpSpec.TryParse(environmentSpec, out var environmentStages, out var environmentError))
    {
        Console.Error.WriteLine($"error: {DumpEnvironmentVariable}: {environmentError}");
        return 1;
    }
    dump |= environmentStages;
}

string? sourceArgument = null;

foreach (var arg in args.Skip(1))
{
    if (arg is "-h" or "--help")
    {
        Console.WriteLine(usage);
        return 0;
    }

    if (arg.StartsWith(DumpFlag, StringComparison.Ordinal))
    {
        // Accepts --dump, --dump=tokens,ast and --dump-tokens alike.
        var spec = arg[DumpFlag.Length..].TrimStart('=', '-');
        if (spec.Length == 0)
            spec = "all";

        if (!DumpSpec.TryParse(spec, out var stages, out var error))
        {
            Console.Error.WriteLine($"error: {error}");
            return 1;
        }
        dump |= stages;
        continue;
    }

    if (arg.StartsWith('-'))
    {
        Console.Error.WriteLine($"error: unknown option '{arg}'");
        Console.Error.WriteLine(usage);
        return 1;
    }

    if (sourceArgument is not null)
    {
        Console.Error.WriteLine("error: expected a single source file");
        return 1;
    }
    sourceArgument = arg;
}

if (sourceArgument is null)
{
    Console.Error.WriteLine(usage);
    return 1;
}

var sourcePath = Path.GetFullPath(sourceArgument);
var buildDir = Path.Combine(Path.GetDirectoryName(sourcePath)!, "build");

// Dumps go to stderr so stdout stays usable for the build result.
var compiler = new Compiler(sourcePath, new DumpOptions(dump, Console.Error));
var result = compiler.Compile(buildDir);

if (!result.Success)
{
    foreach (var error in result.Errors)
        Console.Error.WriteLine($"error: {error}");
    return 1;
}

Console.WriteLine($"Built: {result.OutputPath}");
return 0;
