namespace Suru.Compiler.Parse.Ast;

public abstract class Statement { }

public sealed class ExpressionStatement(Expression expression) : Statement
{
    public Expression Expression { get; } = expression;
}
