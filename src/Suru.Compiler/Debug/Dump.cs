namespace Suru.Compiler.Debug;

/// <summary>
/// The compiler stages that can dump their output.
/// <para>
/// Compilers debug by dumping the whole intermediate form after each stage — you
/// diff what the program looked like before and after a stage — rather than by
/// narrating events the way an application log does. The dumps are always
/// compiled in and off by default, because the person who needs them is usually
/// holding a release build.
/// </para>
/// </summary>
[Flags]
public enum Dump
{
    None = 0,

    /// <summary>The token stream, in source order.</summary>
    Tokens = 1 << 0,

    /// <summary>The AST as the parser produced it, before any types exist.</summary>
    Ast = 1 << 1,

    /// <summary>The same AST after semantic analysis, with the resolved type on every expression.</summary>
    TypedAst = 1 << 2,

    /// <summary>The generated LLVM IR, in LLVM's textual form.</summary>
    Llvm = 1 << 3,

    All = Tokens | Ast | TypedAst | Llvm,
}

/// <summary>Parses the comma separated stage list accepted by <c>--dump</c> and <c>SURU_DUMP</c>.</summary>
public static class DumpSpec
{
    public static readonly (string Name, Dump Stage)[] Stages =
    [
        ("tokens", Dump.Tokens),
        ("ast", Dump.Ast),
        ("typed-ast", Dump.TypedAst),
        ("llvm", Dump.Llvm),
        ("all", Dump.All),
    ];

    /// <summary>Every accepted stage name, for help text and error messages.</summary>
    public static string Names => string.Join(", ", Stages.Select(stage => stage.Name));

    /// <summary>Parses a list such as <c>tokens,ast</c>. Unknown names fail rather than being ignored.</summary>
    public static bool TryParse(string spec, out Dump dump, out string? error)
    {
        dump = Dump.None;
        error = null;

        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var name = part.ToLowerInvariant();
            var match = Array.Find(Stages, stage => stage.Name == name);
            if (match.Stage == Dump.None)
            {
                error = $"unknown dump stage '{part}'; expected one of: {Names}";
                dump = Dump.None;
                return false;
            }
            dump |= match.Stage;
        }

        return true;
    }
}
