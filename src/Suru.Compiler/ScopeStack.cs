namespace Suru.Compiler;

/// <summary>
/// Why a scope exists. A block is the only thing that opens one, so the kind is decided by the
/// parser — the one stage that knows <i>why</i> it is building a block — and rides in on the node
/// from there, which is what keeps semantic analysis and codegen from drifting on it.
/// <para>
/// Only kinds some lookup actually distinguishes should exist. <c>if</c> arms stay
/// <see cref="Plain"/> until something asks them apart.
/// </para>
/// </summary>
public enum ScopeKind
{
    /// <summary>A block that is nothing but a scope.</summary>
    Plain,

    /// <summary>A loop body: what a <c>break</c> or <c>continue</c> searches outward for.</summary>
    Loop,

    /// <summary>
    /// A function body, and the barrier the outward searches stop at. Nothing creates one yet —
    /// user-defined functions do, and the searches below are already written to respect it so
    /// that adding them is a change of what exists, not of how lookup works.
    /// </summary>
    Function,
}

/// <summary>Per-scope data for a stage that needs none.</summary>
public readonly record struct NoScopeData;

/// <summary>
/// The names in scope, innermost last, and what kind of scope each one is. A block enters a scope
/// on the way in and exits it on the way out; a lookup walks outward from the innermost, so an
/// inner binding shadows an outer one of the same name.
/// <para>
/// "What names am I inside" and "what control flow am I inside" are the same question — innermost
/// first, outward, stopping at a barrier — so they are one mechanism rather than two.
/// <see cref="TryFindEnclosing"/> is the second question over the same walk, which is why neither
/// stage keeps a structure of its own for it.
/// </para>
/// <para>
/// Semantic analysis and codegen both need this over their own payloads — a type and a stack slot
/// for <typeparamref name="TName"/>, nothing and a loop's two basic blocks for
/// <typeparamref name="TScope"/> — so it is one generic type rather than the same list of
/// dictionaries written twice. The per-scope payload is a second type parameter rather than a
/// shared enum because only codegen has anything to put there.
/// </para>
/// <para>
/// <b>The barrier is per-query, not per-scope.</b> A <see cref="ScopeKind.Function"/> scope stops
/// <see cref="TryFindEnclosing"/>, because a <c>break</c> written in a function called from inside
/// a loop does not belong to that loop. It will <i>not</i> stop every query once functions exist:
/// a function's own name is declared in the scope outside its body, so the search that resolves a
/// call has to pass through the barrier or recursion could not work, while the search that
/// resolves a variable has to stop at it or a body would see its caller's locals. Those are two
/// searches over this one walk, and the day they exist the difference belongs here rather than in
/// either stage.
/// </para>
/// </summary>
public sealed class ScopeStack<TName, TScope>
{
    private sealed class Scope(ScopeKind kind, TScope data)
    {
        public ScopeKind Kind { get; } = kind;
        public TScope Data { get; } = data;
        public Dictionary<string, TName> Names { get; } = [];
    }

    /// <summary>The outermost scope is the file itself and is never exited.</summary>
    private readonly List<Scope> _scopes = [new(ScopeKind.Plain, default!)];

    /// <summary>
    /// Enters a scope of the given kind. Whoever enters a scope supplies its data, so there is no
    /// half-initialised scope and no pending field for the exit to remember.
    /// </summary>
    public void EnterNew(ScopeKind kind = ScopeKind.Plain, TScope data = default!) =>
        _scopes.Add(new Scope(kind, data));

    public void Exit()
    {
        if (_scopes.Count == 1)
            throw new InvalidOperationException("the outermost scope cannot be exited");
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    /// <summary>
    /// Bound in the innermost scope. This is the redeclaration check: it looks no further
    /// out, so shadowing an outer binding does not trip it.
    /// </summary>
    public bool DeclaredHere(string name) => _scopes[^1].Names.ContainsKey(name);

    /// <summary>Binds in the innermost scope, shadowing any outer binding of the same name.</summary>
    public void Declare(string name, TName value) => _scopes[^1].Names[name] = value;

    /// <summary>Innermost first, then outward. False when the name is bound nowhere.</summary>
    public bool TryLookup(string name, out TName value)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].Names.TryGetValue(name, out value!))
                return true;

        value = default!;
        return false;
    }

    /// <summary>
    /// The nearest enclosing scope of the given kind and its data — the same walk
    /// <see cref="TryLookup"/> does, asking what encloses rather than what is bound.
    /// <para>
    /// The search stops at a <see cref="ScopeKind.Function"/> scope, so a <c>break</c> inside a
    /// function called from a loop cannot see that loop. Asking for
    /// <see cref="ScopeKind.Function"/> itself therefore finds the enclosing function and no
    /// further, which is the same rule read the other way round.
    /// </para>
    /// </summary>
    public bool TryFindEnclosing(ScopeKind kind, out TScope data)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].Kind == kind)
            {
                data = _scopes[i].Data;
                return true;
            }

            if (_scopes[i].Kind == ScopeKind.Function)
                break;
        }

        data = default!;
        return false;
    }
}
