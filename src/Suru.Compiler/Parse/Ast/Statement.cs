namespace Suru.Compiler.Parse.Ast;

public abstract class Statement(SourcePosition position)
{
    public SourcePosition Position { get; } = position;
}

public sealed class ExpressionStatement(Expression expression) : Statement(expression.Position)
{
    public Expression Expression { get; } = expression;
}

/// <summary>
/// <c>let &lt;name&gt; &lt;type&gt;: &lt;value&gt;</c>. The type is written out, so
/// <paramref name="typePosition"/> lets an unknown type name point at the type
/// rather than at the <c>let</c>.
/// </summary>
public sealed class LetStatement(
    SourcePosition position,
    string name,
    string typeName,
    SourcePosition typePosition,
    Expression value) : Statement(position)
{
    public string Name { get; } = name;
    public string TypeName { get; } = typeName;
    public SourcePosition TypePosition { get; } = typePosition;
    public Expression Value { get; } = value;
}

/// <summary>
/// <c>{ ... }</c>. A scope: bindings made inside it are visible only until the <c>}</c>,
/// and one may shadow an outer binding of the same name.
/// </summary>
public sealed class BlockStatement(SourcePosition position, IReadOnlyList<Statement> statements)
    : Statement(position)
{
    public IReadOnlyList<Statement> Statements { get; } = statements;
}

/// <summary><c>&lt;name&gt;: &lt;value&gt;</c>, storing into an existing binding.</summary>
public sealed class AssignmentStatement(SourcePosition position, string name, Expression value)
    : Statement(position)
{
    public string Name { get; } = name;
    public Expression Value { get; } = value;
}
