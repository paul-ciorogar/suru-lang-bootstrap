namespace Suru.Compiler.Parse.Ast;

public abstract class Statement(SourcePosition position)
{
    public SourcePosition Position { get; } = position;
}

public sealed class ExpressionStatement(Expression expression) : Statement(expression.Position)
{
    public Expression Expression { get; } = expression;
}
