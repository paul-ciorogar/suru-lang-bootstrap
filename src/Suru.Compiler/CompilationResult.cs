namespace Suru.Compiler;

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
}
