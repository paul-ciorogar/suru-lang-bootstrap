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
