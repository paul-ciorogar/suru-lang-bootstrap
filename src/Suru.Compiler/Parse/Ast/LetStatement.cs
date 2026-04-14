namespace Suru.Compiler.Parse.Ast;

public sealed class LetStatement(string name, string? typeAnnotation, Expression value) : Statement
{
    public string Name { get; } = name;
    public string? TypeAnnotation { get; } = typeAnnotation;
    public Expression Value { get; } = value;
}
