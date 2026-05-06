using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

internal sealed class Scopes
{
    private readonly Stack<Scope> _stack = new();

    public int Count => _stack.Count;

    public void Enter() => _stack.Push(new Scope());
    public void Exit()  => _stack.Pop();

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
}
