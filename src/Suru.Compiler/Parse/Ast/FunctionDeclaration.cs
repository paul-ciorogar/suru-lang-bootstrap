namespace Suru.Compiler.Parse.Ast;

/// <summary>
/// <c>fn &lt;name&gt;(&lt;parameters&gt;) &lt;return type&gt; { ... }</c>. The return type is
/// always written; <c>void</c> is an ordinary identifier here, and where it may appear is the
/// analyzer's call.
/// <para>
/// A <see cref="Statement"/> because where a declaration may appear is a semantic rule: a
/// misplaced <c>fn</c> is then collected alongside every other error rather than thrown at the
/// first.
/// </para>
/// </summary>
public sealed class FunctionDeclaration(
    SourcePosition position,
    string name,
    IReadOnlyList<Parameter> parameters,
    string returnTypeName,
    SourcePosition returnTypePosition,
    BlockStatement body) : Statement(position)
{
    public string Name { get; } = name;
    public IReadOnlyList<Parameter> Parameters { get; } = parameters;
    public string ReturnTypeName { get; } = returnTypeName;
    public SourcePosition ReturnTypePosition { get; } = returnTypePosition;

    /// <summary>A block of kind <see cref="ScopeKind.Function"/>.</summary>
    public BlockStatement Body { get; } = body;

    /// <summary>Filled in by the semantic analyzer; null until then, or if the name did not resolve.</summary>
    public SuruType? ReturnType { get; internal set; }
}

/// <summary>
/// <c>&lt;name&gt; &lt;type&gt;</c> in a function's parameter list: a
/// <see cref="LetStatement"/> without the value. <see cref="TypePosition"/> is where an unknown
/// type is reported.
/// </summary>
public sealed class Parameter(
    SourcePosition position, string name, string typeName, SourcePosition typePosition)
{
    /// <summary>Where the name is written — what a duplicate parameter points at.</summary>
    public SourcePosition Position { get; } = position;

    public string Name { get; } = name;
    public string TypeName { get; } = typeName;
    public SourcePosition TypePosition { get; } = typePosition;

    /// <summary>
    /// Filled in by the semantic analyzer. Needed because there is no initialiser to carry a
    /// type, and codegen needs one for the parameter's slot.
    /// </summary>
    public SuruType? Type { get; internal set; }
}
