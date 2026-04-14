namespace Suru.Compiler.Parse.Ast;

public sealed class AssignmentStatement(string name, Expression value) : Statement
{
    public string Name { get; } = name;
    public Expression Value { get; } = value;
}
