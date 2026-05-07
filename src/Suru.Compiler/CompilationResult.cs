namespace Suru.Compiler;

/// <summary>
/// Result of the full build pipeline (lex → link). Carries the path of the
/// produced executable on success, or a list of error messages on failure.
/// </summary>
public sealed class CompilationResult
{
    public bool Success { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];
    public string? OutputPath { get; private init; }

    public static CompilationResult Ok(string outputPath) =>
        new() { Success = true, OutputPath = outputPath };

    public static CompilationResult Fail(IReadOnlyList<string> errors) =>
        new() { Success = false, Errors = errors };

    public static CompilationResult Fail(string error) =>
        Fail([error]);

    // Unwraps OutputPath after a .Success check; throws descriptively on misuse.
    public string Require() =>
        OutputPath ?? throw new InvalidOperationException(
            $"CompilationResult failed: {string.Join("; ", Errors)}");
}

/// <summary>
/// Result of a partial pipeline stage that produces a typed value (e.g. a token list,
/// a parsed <see cref="Parse.Ast.Module"/>, or an IR string) rather than a file path.
/// <typeparamref name="T"/> is non-null when <see cref="Success"/> is <c>true</c>.
/// </summary>
public sealed class CompilationResult<T>
{
    public bool Success { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];
    public T? Value { get; private init; }

    public static CompilationResult<T> Ok(T value) =>
        new() { Success = true, Value = value };

    public static CompilationResult<T> Fail(IReadOnlyList<string> errors) =>
        new() { Success = false, Errors = errors };

    public static CompilationResult<T> Fail(string error) =>
        Fail([error]);

    // Unwraps Value after a .Success check; throws descriptively on misuse.
    public T Require() =>
        Value ?? throw new InvalidOperationException(
            $"CompilationResult failed: {string.Join("; ", Errors)}");
}
