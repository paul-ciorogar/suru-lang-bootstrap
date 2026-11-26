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
/// rather than at the <c>let</c>, and <paramref name="namePosition"/> lets a reserved name point
/// at the name.
/// </summary>
public sealed class LetStatement(
    SourcePosition position,
    string name,
    SourcePosition namePosition,
    string typeName,
    SourcePosition typePosition,
    Expression value) : Statement(position)
{
    public string Name { get; } = name;
    public SourcePosition NamePosition { get; } = namePosition;
    public string TypeName { get; } = typeName;
    public SourcePosition TypePosition { get; } = typePosition;
    public Expression Value { get; } = value;
}

/// <summary>
/// <c>{ ... }</c>. A scope: bindings made inside it are visible only until the <c>}</c>,
/// and one may shadow an outer binding of the same name.
/// </summary>
public sealed class BlockStatement(
    SourcePosition position,
    IReadOnlyList<Statement> statements,
    ScopeKind kind = ScopeKind.Plain) : Statement(position)
{
    /// <summary>
    /// Set by the parser, the only stage that knows <i>why</i> it built the block: a loop body
    /// and an <c>if</c> arm are the same syntax. Downstream stages read it rather than re-derive
    /// it, so they cannot drift apart.
    /// </summary>
    public ScopeKind Kind { get; } = kind;

    public IReadOnlyList<Statement> Statements { get; } = statements;
}

/// <summary>
/// <c>if &lt;condition&gt; { ... }</c>, with an optional <c>else</c>. Both arms are blocks, so
/// both are scopes with no rule of their own.
/// <para>
/// <see cref="Else"/> is a <see cref="BlockStatement"/>, or another <see cref="IfStatement"/>
/// for <c>else if</c> — which is why no stage past the parser has a case for <c>else if</c>.
/// </para>
/// </summary>
public sealed class IfStatement(
    SourcePosition position, Expression condition, BlockStatement then, Statement? otherwise)
    : Statement(position)
{
    public Expression Condition { get; } = condition;
    public BlockStatement Then { get; } = then;
    public Statement? Else { get; } = otherwise;
}

/// <summary>
/// <c>while &lt;condition&gt; { ... }</c>. The condition is re-evaluated before every iteration;
/// the body is a block, so a scope with no rule of its own.
/// </summary>
public sealed class WhileStatement(
    SourcePosition position, Expression condition, BlockStatement body) : Statement(position)
{
    public Expression Condition { get; } = condition;
    public BlockStatement Body { get; } = body;
}

/// <summary>
/// <c>break</c>: leaves the innermost enclosing loop. There are no labels, so nesting alone
/// picks the loop.
/// </summary>
public sealed class BreakStatement(SourcePosition position) : Statement(position);

/// <summary>
/// <c>continue</c>: re-tests the innermost enclosing loop's condition. Picks its loop by
/// nesting, as <see cref="BreakStatement"/> does.
/// </summary>
public sealed class ContinueStatement(SourcePosition position) : Statement(position);

/// <summary>
/// <c>return</c>, with or without a value. A value belongs to it only if it begins on the
/// <c>return</c>'s own line (see <c>Parser.ParseReturn</c>), so a statement on the next line is
/// never mistaken for one.
/// </summary>
public sealed class ReturnStatement(SourcePosition position, Expression? value) : Statement(position)
{
    public Expression? Value { get; } = value;
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
/// a production build discards the line in the lexer. Its <see cref="Statement.Position"/> is
/// its <c>#</c>.
/// </summary>
public abstract class Directive(SourcePosition position) : Statement(position);

/// <summary>
/// <c>#mock &lt;name&gt;: &lt;value&gt;</c>: an assignment that exists only in a test build.
/// It takes effect where it is written rather than replacing the binding's initialiser, so
/// it obeys the ordinary assignment rules and needs no case of its own in scoping or codegen;
/// <paramref name="namePosition"/> is where those diagnostics point.
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
/// <c>#view &lt;subject&gt;:</c>: reports the subject's value into its own source line, after
/// the colon.
/// </summary>
public sealed class ViewDirective(SourcePosition position, int id, Expression subject)
    : Directive(position)
{
    /// <summary>
    /// How a value reported at runtime finds its way back to this source line. Numbered in
    /// source order, so it is stable across a run and readable in a dump.
    /// </summary>
    public int Id { get; } = id;

    public Expression Subject { get; } = subject;
}

/// <summary>
/// <c>#assert(&lt;actual&gt;, &lt;expected&gt;):</c>: records the outcome in its own source
/// line and, on failure, as a diagnostic.
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
