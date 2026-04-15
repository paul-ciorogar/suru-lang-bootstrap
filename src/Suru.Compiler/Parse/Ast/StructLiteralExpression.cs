namespace Suru.Compiler.Parse.Ast;

public sealed class StructLiteralExpression(
    IReadOnlyList<(string Name, Expression Value)> fields) : Expression
{
    public IReadOnlyList<(string Name, Expression Value)> Fields { get; } = fields;
}
