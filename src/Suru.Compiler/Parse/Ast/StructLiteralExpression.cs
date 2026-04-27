namespace Suru.Compiler.Parse.Ast;

public sealed class StructLiteralExpression(
    IReadOnlyList<(string Name, TypeAnnotation TypeAnnotation, Expression Value)> fields) : Expression
{
    public IReadOnlyList<(string Name, TypeAnnotation TypeAnnotation, Expression Value)> Fields { get; } = fields;
}
