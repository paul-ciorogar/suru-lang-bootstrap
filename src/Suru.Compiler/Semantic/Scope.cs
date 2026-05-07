using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

// One frame in the scope stack.
// Holds variable symbols, function signatures, and which names are constants.
internal sealed class Scope
{
    private readonly Dictionary<string, SuruType>    _symbols   = new();
    private readonly Dictionary<string, FunctionSig> _functions = new();
    private readonly HashSet<string>                 _constants = new();

    // ─── Variables ────────────────────────────────────────────────────────────

    public SuruType? TryGet(string name)  =>
        _symbols.TryGetValue(name, out var t) ? t : null;

    public bool Contains(string name)     => _symbols.ContainsKey(name);
    public void Declare(string name, SuruType type) => _symbols[name] = type;

    // ─── Function signatures ──────────────────────────────────────────────────

    public FunctionSig? TryGetFunction(string name) =>
        _functions.TryGetValue(name, out var s) ? s : null;

    public bool ContainsFunction(string name)              => _functions.ContainsKey(name);
    public void DeclareFunction(string name, FunctionSig sig) => _functions[name] = sig;

    // ─── Constants ────────────────────────────────────────────────────────────

    // Marks a previously declared variable name as a constant in this scope.
    public void MarkConstant(string name) => _constants.Add(name);
    public bool IsConstant(string name)   => _constants.Contains(name);
}
