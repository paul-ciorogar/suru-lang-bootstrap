namespace Suru.Compiler.Semantic;

/// <summary>
/// What a name means. Variables and functions share one namespace, so they share one
/// <see cref="ScopeStack{TEntry, TScope}"/> — and the two shapes differ only in what they are and
/// in whether they survive a function boundary, which is the whole of what the barrier reads.
/// </summary>
public abstract record Binding : IScopeEntry
{
    public abstract bool SurvivesFunctionBoundary { get; }
}

/// <summary>A <c>let</c> or a parameter: invisible from inside a nested function's body.</summary>
public sealed record VariableBinding(SuruType Type) : Binding
{
    public override bool SurvivesFunctionBoundary => false;
}

/// <summary>
/// An <c>fn</c>. Visible through a function boundary, because a function's name is declared in
/// the scope around its body and a call to itself has to find it from within.
/// </summary>
public sealed record FunctionBinding(FunctionSignature Signature) : Binding
{
    public override bool SurvivesFunctionBoundary => true;
}
