namespace Suru.Compiler.Parse.Ast;

public sealed class LetStatement(string name, Expression value) : Statement
{
    public string Name { get; } = name;
    public Expression Value { get; } = value;
}
