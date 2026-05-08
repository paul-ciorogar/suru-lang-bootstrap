namespace Suru.Compiler.Parse.Ast;

// Canonical lookup table for all imported declarations in one compiled module.
// Key: (absoluteSourcePath, unqualifiedName) — independent of any namespace alias.
// Covers FunctionDeclaration, TypeDeclaration, and scalar LetStatement constants.
internal sealed class ExternalDeclarationRegistry
{
    private readonly Dictionary<(string, string), Statement> _map = new();

    // TryAdd semantics: first registration wins (idempotent on diamond re-merge).
    public void Register(string sourcePath, Statement decl)
    {
        var name = decl switch
        {
            FunctionDeclaration fn => fn.Name,
            TypeDeclaration td    => td.Name,
            LetStatement ls       => ls.Name,
            _ => throw new ArgumentException(
                $"Unsupported declaration kind: {decl.GetType().Name}")
        };
        _map.TryAdd((sourcePath, name), decl);
    }

    public Statement? Lookup(string sourcePath, string name) =>
        _map.TryGetValue((sourcePath, name), out var decl) ? decl : null;

    public FunctionDeclaration? LookupFunction(string sourcePath, string name) =>
        Lookup(sourcePath, name) as FunctionDeclaration;

    public bool Contains(string sourcePath, string name) =>
        _map.ContainsKey((sourcePath, name));

    // TryAdd semantics: used for transitive propagation.
    public void MergeFrom(ExternalDeclarationRegistry other)
    {
        foreach (var (key, decl) in other._map)
            _map.TryAdd(key, decl);
    }
}
