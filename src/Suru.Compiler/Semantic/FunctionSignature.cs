namespace Suru.Compiler.Semantic;

/// <summary>
/// What a call needs to know about a function: how many arguments, of what types, and what it
/// yields. Collected before any statement in a list is analyzed, which is what lets a call reach
/// a function declared further down.
/// <para>
/// Deliberately <b>not</b> a <see cref="SuruType"/>. Nothing in the language can hold a function
/// as a value and there is no syntax for writing a function type down, so a type node for one
/// would be structure no program could reach. It becomes a type the day a function is a value.
/// </para>
/// </summary>
/// <param name="Parameters">
/// One entry per declared parameter, so arity is always right; an entry is null when that
/// parameter's type name did not resolve, and a null is skipped rather than reported twice.
/// </param>
/// <param name="ReturnType">
/// Null when the return type name did not resolve. The signature is registered anyway, so a call
/// still reports its own arity and argument problems instead of <c>unknown function</c>.
/// </param>
public sealed record FunctionSignature(
    string Name,
    IReadOnlyList<SuruType?> Parameters,
    SuruType? ReturnType);
