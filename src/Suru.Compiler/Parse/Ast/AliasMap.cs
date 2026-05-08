namespace Suru.Compiler.Parse.Ast;

// Maps namespace aliases to absolute source paths for one compiled module.
// e.g. `include "lexer.suru" as lex` → Register("lex", "/abs/path/lexer.suru")
internal sealed class AliasMap
{
    private readonly Dictionary<string, string> _map = new();

    public void Register(string alias, string absolutePath) => _map[alias] = absolutePath;

    public bool Contains(string alias) => _map.ContainsKey(alias);

    public string? Resolve(string alias) =>
        _map.TryGetValue(alias, out var path) ? path : null;

    // TryAdd semantics: the calling module's own entries win over merged-in entries.
    public void MergeFrom(AliasMap other)
    {
        foreach (var (alias, path) in other._map)
            _map.TryAdd(alias, path);
    }

    public IReadOnlyDictionary<string, string> All => _map;
}
