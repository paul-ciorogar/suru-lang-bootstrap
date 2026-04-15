namespace Suru.Compiler.Parse.Ast;

public sealed class ReturnStatement(Expression? value) : Statement
{
    public Expression? Value { get; } = value;
}
