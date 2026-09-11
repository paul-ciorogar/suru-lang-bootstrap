using Suru.Compiler.Debug;
using Suru.Lib;

namespace Suru.CLI;

/// <summary>
/// Parses the comma separated stage list accepted by <c>--dump</c> and <c>SURU_DUMP</c>.
/// <para>
/// The <see cref="Dump"/> flags are the compiler's, because the stages are; the names a
/// command line spells them with are not. Which is why this sits here and not beside the
/// enum: the compiler is handed a <see cref="Dump"/> and never learns there was a text form.
/// </para>
/// </summary>
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

    /// <summary>
    /// Parses a list such as <c>tokens,ast</c>. Unknown names fail rather than being ignored:
    /// a stage list is asked for by someone who wants to see the stage, and silently dropping
    /// the one they misspelled would look exactly like the dump not working.
    /// </summary>
    public static Result<Dump, ArgsError> Parse(string spec)
    {
        var dump = Dump.None;

        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var name = part.ToLowerInvariant();
            var match = Array.Find(Stages, stage => stage.Name == name);
            if (match.Stage == Dump.None)
            {
                return Result.Error<Dump, ArgsError>(
                    new ArgsError($"unknown dump stage '{part}'; expected one of: {Names}"));
            }
            dump |= match.Stage;
        }

        return Result.Ok<Dump, ArgsError>(dump);
    }
}
