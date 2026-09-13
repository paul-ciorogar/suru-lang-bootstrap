namespace Suru.Compiler;

/// <summary>
/// The names in scope, innermost last. A block enters a scope on the way in and exits it
/// on the way out; a lookup walks outward from the innermost, so an inner binding shadows
/// an outer one of the same name. The outermost scope is the file itself and is never
/// exited.
/// <para>
/// Semantic analysis and codegen both need this over their own payload — a type and a
/// stack slot — so it is one generic type rather than the same list of dictionaries
/// written twice.
/// </para>
/// </summary>
// TODO(scope-kinds): this type is the anchor of a change that removes the two stage-local
// structures currently tracking "which loop am I inside" — 'SemanticAnalyzer._loopDepth' and
// 'CodeGenerator._loops'. The design, in full, so the other TODOs can be one line each:
//
//   1. 'BlockStatement' gains a 'Kind' (Plain | Loop; Function when functions exist), set by
//      the parser, which is the only stage that knows *why* it is building a block.
//   2. This class gains per-scope data alongside the per-name dictionary — 'EnterNew(kind, …)'
//      and an outward search over scopes, the same walk 'TryLookup' already does over names.
//      Semantics needs only the kind; codegen needs the loop's two basic blocks with it, so the
//      per-scope payload is a second type parameter rather than a shared enum.
//   3. 'RequireInLoop' and 'Loop(keyword)' both become that search, and both stage-local
//      structures go.
//
// The point is that "what control flow am I inside" and "what names am I inside" are the same
// question — innermost first, outward, stopping at a barrier — so they should not be two
// mechanisms. A 'Function' kind is that barrier: the search stops at one, which is what makes a
// 'break' inside a function called from a loop unable to see that loop by construction, with no
// save/restore to forget. Add the member when functions exist, not before.
//
// Only kinds some lookup actually distinguishes should exist: 'if' arms and struct bodies stay
// Plain until something asks them apart. And the kind must be derived from the AST node being
// walked, never written by hand per call site, or the two stages can drift on it.
public sealed class ScopeStack<T>
{
    private readonly List<Dictionary<string, T>> _scopes = [[]];

    public void EnterNew() => _scopes.Add([]);

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
    public bool DeclaredHere(string name) => _scopes[^1].ContainsKey(name);

    /// <summary>Binds in the innermost scope, shadowing any outer binding of the same name.</summary>
    public void Declare(string name, T value) => _scopes[^1][name] = value;

    /// <summary>Innermost first, then outward. False when the name is bound nowhere.</summary>
    public bool TryLookup(string name, out T value)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out value!))
                return true;

        value = default!;
        return false;
    }
}
