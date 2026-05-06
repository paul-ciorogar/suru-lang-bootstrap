using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

internal sealed class Scope
{
    private readonly Dictionary<string, SuruType> _symbols = new();

    public SuruType? TryGet(string name) =>
        _symbols.TryGetValue(name, out var t) ? t : null;

    public bool Contains(string name) => _symbols.ContainsKey(name);
    public void Declare(string name, SuruType type) => _symbols[name] = type;
}
