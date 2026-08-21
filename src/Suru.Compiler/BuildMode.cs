namespace Suru.Compiler;

/// <summary>
/// What a compilation is for, which decides the fate of the <c>#</c> test directives.
/// <para>
/// The distinction is made in the lexer rather than any later stage: in
/// <see cref="Production"/> a <c>#</c> line is skipped exactly like a <c>//</c> comment and
/// no token for it ever reaches the parser. A directive written in syntax a given compiler
/// build does not understand therefore cannot break a release build — the cost being that a
/// mistyped directive is only ever reported by a test build.
/// </para>
/// </summary>
public enum BuildMode
{
    Production,
    Test,
}
