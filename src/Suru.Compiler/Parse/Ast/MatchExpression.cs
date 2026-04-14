namespace Suru.Compiler.Parse.Ast;

// Pattern = null represents the wildcard arm (_)
public sealed class MatchArm(Expression? pattern, Expression body)
{
    public Expression? Pattern { get; } = pattern;
    public Expression Body { get; } = body;
}

public sealed class MatchExpression(Expression condition, IReadOnlyList<MatchArm> arms) : Expression
{
    public Expression Condition { get; } = condition;
    public IReadOnlyList<MatchArm> Arms { get; } = arms;
}
