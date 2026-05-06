namespace Suru.Compiler.Parse.Ast;

// Represents `recv.field: expr` mutation syntax.
// Distinct from AssignmentStatement (variable rebind) because the target is a struct field,
// not a local variable — the codegen must walk the field linked-list and update the val slot.
public sealed class FieldAssignmentStatement(
    Expression receiver, string fieldName, Expression value) : Statement
{
    public Expression Receiver { get; } = receiver;
    public string FieldName { get; } = fieldName;
    public Expression Value { get; } = value;
}
