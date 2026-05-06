namespace Suru.Compiler.Parse.Ast;

public abstract class Statement { }

public sealed class ExpressionStatement(Expression expression) : Statement
{
    public Expression Expression { get; } = expression;
}

// SemanticAnalyzer pushes a fresh scope for Body so that `let` names declared
// inside the loop don't collide with identical names in subsequent while loops.
public sealed class WhileStatement(Expression condition, IReadOnlyList<Statement> body) : Statement
{
    public Expression Condition { get; } = condition;
    public IReadOnlyList<Statement> Body { get; } = body;
}
