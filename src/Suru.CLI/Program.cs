using Suru.Compiler;
using Suru.Compiler.Debug;

const string DumpFlag = "--dump";
const string DumpEnvironmentVariable = "SURU_DUMP";

var usage = $"""
    Usage: suru <command> [options] <file.suru>

    Commands:
      build             compile to a native executable; '#' directives are ignored
      test              compile with the '#' directives live, run the result, and
                        write each '#view' and '#assert' result into the source

    Options:
      --dump=<stages>   write the named compiler stages to stderr
      --dump-<stage>    equivalent shorthand for a single stage
      --dump            all stages

    Stages: {DumpSpec.Names}

    {DumpEnvironmentVariable} holds the same stage list and applies to every run.
    """;

var command = args.Length > 0 ? args[0] : "";
if (command is not ("build" or "test"))
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

if (command == "test")
    return Test();

var result = compiler.Compile(buildDir);

if (!result.Success)
{
    foreach (var error in result.Errors)
        Console.Error.WriteLine($"error: {error}");
    return 1;
}

Console.WriteLine($"Built: {result.OutputPath}");
return 0;

int Test()
{
    var run = compiler.Test(buildDir);

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
        $"{run.Passed} passed, {run.Failed} failed, {views} written to {sourcePath}");

    if (run.ExitCode != 0)
        Console.Error.WriteLine($"error: the program exited with {run.ExitCode}");

    return run.Success ? 0 : 1;
}
