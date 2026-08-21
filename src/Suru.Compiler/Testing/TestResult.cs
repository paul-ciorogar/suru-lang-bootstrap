namespace Suru.Compiler.Testing;

/// <summary>
/// What a <c>suru test</c> run found. Like <see cref="CompilationResult"/> it only carries
/// the outcome — the CLI is the only thing that prints.
/// </summary>
public sealed class TestResult
{
    /// <summary>Compiled, ran, and every assertion held.</summary>
    public bool Success => Errors.Count == 0 && Failed == 0 && ExitCode == 0;

    /// <summary>Compile errors, or a reason the program could not be run.</summary>
    public IReadOnlyList<string> Errors { get; private init; } = [];

    /// <summary>One <c>file(line,col): message</c> per failed assertion.</summary>
    public IReadOnlyList<string> Failures { get; private init; } = [];

    public int Passed { get; private init; }
    public int Failed { get; private init; }

    /// <summary>How many <c>#view</c> values were written back into the source.</summary>
    public int Views { get; private init; }

    /// <summary>
    /// Directives — <c>#view</c> and <c>#assert</c> alike — the run never reached, each written
    /// back as <c>undefined</c>. Deliberately neither a pass nor a failure and not part of
    /// <see cref="Success"/>: an assertion inside a branch the run does not take is honest, not
    /// broken. What would be dishonest is hiding it, which this count prevents.
    /// </summary>
    public int Undefined { get; private init; }

    /// <summary>What the program itself printed, with the test records taken out.</summary>
    public string Output { get; private init; } = "";

    public int ExitCode { get; private init; }

    public static TestResult Fail(IReadOnlyList<string> errors) => new() { Errors = errors };

    public static TestResult Fail(string error) => Fail([error]);

    internal static TestResult Ran(
        string output, int exitCode, IReadOnlyList<string> failures,
        int passed, int views, int undefined) =>
        new()
        {
            Output = output,
            ExitCode = exitCode,
            Failures = failures,
            Passed = passed,
            Failed = failures.Count,
            Views = views,
            Undefined = undefined,
        };
}
