namespace Suru.Compiler.Parse.Ast;

public sealed class Module
{
    public string SourcePath { get; init; } = string.Empty;
    public IReadOnlyList<Statement> Statements { get; init; } = [];
}
