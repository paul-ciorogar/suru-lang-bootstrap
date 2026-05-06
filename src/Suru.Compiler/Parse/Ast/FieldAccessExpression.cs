namespace Suru.Compiler.Parse.Ast;

public sealed class FieldAccessExpression(Expression receiver, string fieldName) : Expression
{
    public Expression Receiver { get; } = receiver;
    public string FieldName { get; } = fieldName;
}
