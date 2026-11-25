using Suru.Lib;

namespace Suru.Compiler;

/// <summary>
/// Why a scope exists. A block is the only thing that opens one, so the kind is decided by the
/// parser — the one stage that knows <i>why</i> it is building a block — and rides in on the node
/// from there, which is what keeps semantic analysis and codegen from drifting on it.
/// <para>
/// Only kinds some check actually tells apart should exist.
/// </para>
/// </summary>
public enum ScopeKind
{
    /// <summary>A block that is nothing but a scope.</summary>
    Plain,

    /// <summary>A loop body: what a <c>break</c> or <c>continue</c> searches outward for.</summary>
    Loop,

    /// <summary>
    /// An <c>if</c> or <c>else</c> arm. Apart from <see cref="Plain"/> because, as in a
    /// <see cref="Loop"/>, a declaration inside may never be reached, so a <c>fn</c> cannot be
    /// written there.
    /// </summary>
    Branch,

    /// <summary>A function body, and the barrier for the outward searches.</summary>
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
/// A lookup's failure: the name is bound nowhere the walk can see. It carries nothing because the
/// caller already holds the name, and there is only the one way to not find it.
/// </summary>
public readonly record struct Unbound;

/// <summary>
/// The names in scope, innermost last, and what kind of scope each one is. An inner binding
/// shadows an outer one of the same name.
/// <para>
/// "What names am I inside" and "what control flow am I inside" are the same question — innermost
/// first, outward, stopping at a barrier — so they are one mechanism, and neither stage keeps a
/// structure of its own for the second.
/// </para>
/// <para>
/// Generic because semantic analysis and codegen need it over their own payloads — a type or a
/// stack slot per name, nothing or a loop's two basic blocks per scope — rather than the same
/// list of dictionaries written twice.
/// </para>
/// <para>
/// <b>The barrier is per-query, not per-scope.</b> Three searches cross a
/// <see cref="ScopeKind.Function"/> scope differently, and that difference is what makes one
/// namespace and lexical function names work:
/// </para>
/// <list type="bullet">
/// <item><see cref="LookupVariable"/> — the barrier <b>hides</b> a binding that does not
/// survive it, so a body cannot see its caller's locals.</item>
/// <item><see cref="LookupFunction"/> — <b>no</b> barrier, because a function's own name is
/// declared in the scope outside its body and recursion could not work otherwise.</item>
/// <item><see cref="TryFindEnclosing"/> — the walk <b>stops</b>, because a <c>break</c> written in
/// a function called from inside a loop does not belong to that loop.</item>
/// </list>
/// <para>
/// For the first two the barrier changes only which entries are admitted, and an entry answers
/// that itself through <see cref="IScopeEntry"/>, so neither stage writes the rule down.
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
    /// Whoever enters a scope supplies its data, so there is no half-initialised scope and no
    /// pending field for the exit to remember.
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
    /// The redeclaration check. It looks no further than the innermost scope, so shadowing an
    /// outer binding does not trip it.
    /// </summary>
    public bool DeclaredHere(string name) => _scopes[^1].Names.ContainsKey(name);

    public void Declare(string name, TEntry value) => _scopes[^1].Names[name] = value;

    /// <summary>
    /// A name read as a value. Past a <see cref="ScopeKind.Function"/> scope only an entry that
    /// survives the boundary is admitted, so a body sees the functions declared around it and
    /// none of the variables.
    /// </summary>
    public Result<TEntry, Unbound> LookupVariable(string name)
    {
        var crossed = false;
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].Names.TryGetValue(name, out var value)
                && (!crossed || value.SurvivesFunctionBoundary))
                return Result.Ok<TEntry, Unbound>(value);

            // Checked after the scope's own names: a body's parameters live in the function
            // scope itself and are on this side of the barrier.
            crossed |= _scopes[i].Kind == ScopeKind.Function;
        }

        return Result.Error<TEntry, Unbound>(default);
    }

    /// <summary>
    /// A name read as a callee, with no barrier: a function's name is declared in the scope
    /// containing its body, so passing through a <see cref="ScopeKind.Function"/> scope is what
    /// lets a function call itself, its siblings and the functions around it.
    /// </summary>
    public Result<TEntry, Unbound> LookupFunction(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].Names.TryGetValue(name, out var value))
                return Result.Ok<TEntry, Unbound>(value);

        return Result.Error<TEntry, Unbound>(default);
    }

    /// <summary>
    /// The nearest enclosing scope of the given kind and its data.
    /// <para>
    /// Stops at a <see cref="ScopeKind.Function"/> scope, so a <c>break</c> inside a function
    /// called from a loop cannot see that loop — and asking for
    /// <see cref="ScopeKind.Function"/> itself finds the enclosing function and no further.
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
