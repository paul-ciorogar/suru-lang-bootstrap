namespace Suru.Compiler.Parse.Ast;

// Body is an empty list for empty arms ({} or bare colon + newline).
// Pattern = null represents the wildcard arm (_).
public sealed class MatchStatementArm(Expression? pattern, IReadOnlyList<Statement> body)
{
    public Expression? Pattern { get; } = pattern;
    public IReadOnlyList<Statement> Body { get; } = body;
}

// Match used at statement level: each arm carries a block of statements rather
// than a single expression, enabling early returns, let bindings, and nested logic.
public sealed class MatchStatement(Expression condition, IReadOnlyList<MatchStatementArm> arms) : Statement
{
    public Expression Condition { get; } = condition;
    public IReadOnlyList<MatchStatementArm> Arms { get; } = arms;
}
