namespace Suru.Compiler;

/// <summary>
/// Why a scope exists. A block is the only thing that opens one, so the kind is decided by the
/// parser — the one stage that knows <i>why</i> it is building a block — and rides in on the node
/// from there, which is what keeps semantic analysis and codegen from drifting on it.
/// <para>
/// Only kinds some lookup actually distinguishes should exist. <c>if</c> arms were
/// <see cref="Plain"/> until something asked them apart, and what asked is where a <c>fn</c> may
/// be declared: a bare block always runs, an arm may not, so the two can no longer share a kind.
/// </para>
/// </summary>
public enum ScopeKind
{
    /// <summary>A block that is nothing but a scope.</summary>
    Plain,

    /// <summary>A loop body: what a <c>break</c> or <c>continue</c> searches outward for.</summary>
    Loop,

    /// <summary>
    /// An <c>if</c> or <c>else</c> arm — a block whose execution is conditional. This and
    /// <see cref="Loop"/> are the control-flow kinds: a declaration inside one may or may not be
    /// reached, which is why a <c>fn</c> cannot be written there, and telling an arm from a bare
    /// block is what asked the two apart.
    /// </summary>
    Branch,

    /// <summary>
    /// A function body, and the barrier the outward searches stop at.
    /// </summary>
    Function,
}

/// <summary>
/// What a binding is, as far as the barrier is concerned: whether it stays visible to a lookup
/// that has crossed a <see cref="ScopeKind.Function"/> scope.
/// <para>
/// The rule lives here and in <see cref="ScopeStack{TEntry, TScope}"/> rather than at each call
/// site, so semantic analysis and codegen only say which of their two binding shapes is which and
/// cannot drift on what the barrier does.
/// </para>
/// </summary>
public interface IScopeEntry
{
    /// <summary>
    /// False for a variable — a body must not see its caller's locals. True for a function name,
    /// which is declared in the scope <i>outside</i> the body it names, so recursion depends on
    /// it passing through.
    /// </summary>
    bool SurvivesFunctionBoundary { get; }
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
/// for <typeparamref name="TEntry"/>, nothing and a loop's two basic blocks for
/// <typeparamref name="TScope"/> — so it is one generic type rather than the same list of
/// dictionaries written twice. The per-scope payload is a second type parameter rather than a
/// shared enum because only codegen has anything to put there.
/// </para>
/// <para>
/// <b>The barrier is per-query, not per-scope.</b> Three searches cross a
/// <see cref="ScopeKind.Function"/> scope differently, and that difference is the whole of what
/// makes one namespace and lexical function names work:
/// </para>
/// <list type="bullet">
/// <item><see cref="TryLookupVariable"/> — the barrier <b>hides</b> a binding that does not
/// survive it, so a body cannot see its caller's locals.</item>
/// <item><see cref="TryLookupFunction"/> — <b>no</b> barrier, because a function's own name is
/// declared in the scope outside its body and recursion could not work otherwise.</item>
/// <item><see cref="TryFindEnclosing"/> — the walk <b>stops</b>, because a <c>break</c> written in
/// a function called from inside a loop does not belong to that loop.</item>
/// </list>
/// <para>
/// The first two walk every scope regardless; what the barrier changes is which entries are
/// admitted, and an entry answers that itself through <see cref="IScopeEntry"/>. Neither stage
/// writes the rule down.
/// </para>
/// </summary>
public sealed class ScopeStack<TEntry, TScope> where TEntry : IScopeEntry
{
    private sealed class Scope(ScopeKind kind, TScope data)
    {
        public ScopeKind Kind { get; } = kind;
        public TScope Data { get; } = data;
        public Dictionary<string, TEntry> Names { get; } = [];
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
    public void Declare(string name, TEntry value) => _scopes[^1].Names[name] = value;

    /// <summary>
    /// Innermost first, then outward, as a value is resolved. Once the walk has crossed a
    /// <see cref="ScopeKind.Function"/> scope only an entry that survives the boundary is
    /// admitted, so a function body sees the names of functions declared around it and none of
    /// the variables. False when the name is bound nowhere the walk can see it.
    /// </summary>
    public bool TryLookupVariable(string name, out TEntry value)
    {
        var crossed = false;
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].Names.TryGetValue(name, out value!)
                && (!crossed || value.SurvivesFunctionBoundary))
                return true;

            // Checked after the scope's own names: a body's parameters live in the function
            // scope itself and are on this side of the barrier.
            crossed |= _scopes[i].Kind == ScopeKind.Function;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Innermost first, then outward, as a call is resolved — with no barrier at all. A
    /// function's name is declared in the scope containing its body, so this walk passing
    /// through a <see cref="ScopeKind.Function"/> scope is exactly what lets a function call
    /// itself, its siblings and the functions around it.
    /// </summary>
    public bool TryLookupFunction(string name, out TEntry value)
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
