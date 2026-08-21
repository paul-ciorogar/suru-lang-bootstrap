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

/// <summary>
/// A <c>#</c> test directive. Only a <see cref="BuildMode.Test"/> compilation ever sees one:
/// a production build discards the line in the lexer. Every directive's
/// <see cref="Statement.Position"/> is that of its <c>#</c>.
/// </summary>
public abstract class Directive(SourcePosition position) : Statement(position);

/// <summary>
/// <c>#mock &lt;name&gt;: &lt;value&gt;</c> — an assignment that exists only in a test build.
/// It takes effect where it is written rather than replacing the binding's initialiser, so
/// it needs no handling of its own in scoping or codegen and obeys the ordinary assignment
/// rules; <paramref name="namePosition"/> is where those diagnostics point.
/// </summary>
public sealed class MockDirective(
    SourcePosition position,
    string name,
    SourcePosition namePosition,
    Expression value) : Directive(position)
{
    public string Name { get; } = name;
    public SourcePosition NamePosition { get; } = namePosition;
    public Expression Value { get; } = value;
}

/// <summary>
/// <c>#view &lt;subject&gt;:</c> — prints the subject's value back into its own source line,
/// after the colon.
/// </summary>
public sealed class ViewDirective(SourcePosition position, int id, Expression subject)
    : Directive(position)
{
    /// <summary>
    /// Identifies this directive in the record the compiled program prints when it reaches
    /// it, which is how a runtime value finds its way back to a source line. Assigned by the
    /// parser in source order, so it is stable across a run and readable in a dump.
    /// </summary>
    public int Id { get; } = id;

    public Expression Subject { get; } = subject;
}

/// <summary>
/// <c>#assert(&lt;actual&gt;, &lt;expected&gt;):</c> — compares the two and records the
/// outcome both in its own source line and, on failure, as a diagnostic.
/// </summary>
public sealed class AssertDirective(
    SourcePosition position,
    int id,
    Expression actual,
    Expression expected) : Directive(position)
{
    /// <inheritdoc cref="ViewDirective.Id"/>
    public int Id { get; } = id;

    public Expression Actual { get; } = actual;
    public Expression Expected { get; } = expected;
}
