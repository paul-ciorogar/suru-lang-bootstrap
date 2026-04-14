namespace Suru.Compiler.Parse.Ast;

public sealed class VariableReferenceExpression(string name) : Expression
{
    public string Name { get; } = name;
}
