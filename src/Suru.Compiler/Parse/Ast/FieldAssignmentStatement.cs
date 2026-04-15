namespace Suru.Compiler.Parse.Ast;

public sealed class FieldAssignmentStatement(
    Expression receiver, string fieldName, Expression value) : Statement
{
    public Expression Receiver { get; } = receiver;
    public string FieldName { get; } = fieldName;
    public Expression Value { get; } = value;
}
