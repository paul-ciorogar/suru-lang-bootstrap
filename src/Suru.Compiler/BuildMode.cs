namespace Suru.Compiler;

/// <summary>
/// What a compilation is for, which decides the fate of the <c>#</c> test directives.
/// <para>
/// Decided in the lexer: in <see cref="Production"/> a <c>#</c> line is skipped like a
/// <c>//</c> comment, so a directive this compiler does not understand cannot break a release
/// build — at the cost that a mistyped directive is only ever reported by a test build.
/// </para>
/// </summary>
public enum BuildMode
{
    Production,
    Test,
}
