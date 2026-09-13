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
    // TODO(scope-kinds): add a 'Kind' here (Plain | Loop, taken as a constructor argument), so
    // the scope a block enters can say what kind of scope it is. See the design note on
    // 'ScopeStack', and have 'AstPrinter' render it so the parser tests assert on it for free.
    public IReadOnlyList<Statement> Statements { get; } = statements;
}

/// <summary>
/// <c>if &lt;condition&gt; { ... }</c>, with an optional <c>else</c>. Both arms are blocks, so
/// both are scopes and neither needs a rule of its own.
/// <para>
/// <see cref="Else"/> is a <see cref="BlockStatement"/>, or another <see cref="IfStatement"/>
/// when the source wrote <c>else if</c> — that nesting is the whole of what <c>else if</c> is,
/// which is why no stage past the parser has a case for it.
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
/// <c>while &lt;condition&gt; { ... }</c>. The condition is re-evaluated before every iteration,
/// and the body is a <see cref="BlockStatement"/>, so it is a scope for the same reason an
/// <c>if</c> arm is one and needs no rule of its own.
/// </summary>
public sealed class WhileStatement(
    SourcePosition position, Expression condition, BlockStatement body) : Statement(position)
{
    public Expression Condition { get; } = condition;
    public BlockStatement Body { get; } = body;
}

/// <summary>
/// <c>break</c> — leaves the innermost enclosing loop. It carries nothing but its position,
/// which is where the diagnostic for one written outside a loop points. There are no labels,
/// so which loop it leaves is decided by nesting alone.
/// </summary>
public sealed class BreakStatement(SourcePosition position) : Statement(position);

/// <summary>
/// <c>continue</c> — abandons this iteration and re-tests the innermost enclosing loop's
/// condition. Carries only its position, and picks its loop by nesting, exactly as
/// <see cref="BreakStatement"/> does.
/// </summary>
public sealed class ContinueStatement(SourcePosition position) : Statement(position);

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
