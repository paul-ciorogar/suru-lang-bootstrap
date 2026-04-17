namespace Suru.Compiler.Parse.Ast;

public abstract class Statement { }

public sealed class ExpressionStatement(Expression expression) : Statement
{
    public Expression Expression { get; } = expression;
}

public sealed class WhileStatement(Expression condition, IReadOnlyList<Statement> body) : Statement
{
    public Expression Condition { get; } = condition;
    public IReadOnlyList<Statement> Body { get; } = body;
}
