using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

// Manages the stack of Scope frames during semantic analysis.
// Enter/Exit push and pop frames; Lookup* methods walk the stack top-to-bottom
// so inner scopes shadow outer ones.
internal sealed class Scopes
{
    private readonly Stack<Scope> _stack = new();

    public int Count => _stack.Count;

    public void Enter() => _stack.Push(new Scope());
    public void Exit()  => _stack.Pop();

    // ─── Variables ────────────────────────────────────────────────────────────

    public SuruType? Lookup(string name)
    {
        foreach (var scope in _stack)
        {
            var t = scope.TryGet(name);
            if (t is not null) return t;
        }
        return null;
    }

    public bool ExistsInCurrent(string name) =>
        _stack.Count > 0 && _stack.Peek().Contains(name);

    public void DeclareInCurrent(string name, SuruType type) =>
        _stack.Peek().Declare(name, type);

    // ─── Function signatures ──────────────────────────────────────────────────

    // Registers a function in the current (innermost) scope.
    public void RegisterFunction(string name, FunctionSig sig) =>
        _stack.Peek().DeclareFunction(name, sig);

    // True when name is already registered in the current scope only (duplicate check).
    public bool FunctionExistsInCurrent(string name) =>
        _stack.Count > 0 && _stack.Peek().ContainsFunction(name);

    // Walks the stack top-to-bottom; returns null if not found in any scope.
    public FunctionSig? LookupFunction(string name)
    {
        foreach (var scope in _stack)
        {
            var sig = scope.TryGetFunction(name);
            if (sig is not null) return sig;
        }
        return null;
    }

    // ─── Constants ────────────────────────────────────────────────────────────

    // Marks a name in the current scope as immutable (module-level let).
    public void MarkConstant(string name) =>
        _stack.Peek().MarkConstant(name);

    // True when name is a constant in any enclosing scope.
    public bool IsConstant(string name)
    {
        foreach (var scope in _stack)
            if (scope.IsConstant(name)) return true;
        return false;
    }
}
