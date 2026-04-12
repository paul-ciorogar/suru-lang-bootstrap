using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly Module _module;

    private SemanticAnalyzer(Module module)
    {
        _module = module;
    }

    public static IReadOnlyList<string> Analyze(Module module)
    {
        var analyzer = new SemanticAnalyzer(module);
        return analyzer._Analyze();
    }

    private IReadOnlyList<string> _Analyze()
    {
        // No declarations to validate yet.
        _ = _module;
        return [];
    }
}
