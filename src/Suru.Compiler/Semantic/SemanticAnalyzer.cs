using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly Module _module;
    private readonly Dictionary<string, SuruType> _symbols = new();
    private readonly List<string> _errors = [];

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
        foreach (var stmt in _module.Statements)
            AnalyzeStatement(stmt);
        return _errors;
    }

    private void AnalyzeStatement(Statement stmt)
    {
        switch (stmt)
        {
            case LetStatement let:
                if (_symbols.ContainsKey(let.Name))
                    _errors.Add($"{_module.SourcePath}: variable '{let.Name}' is already declared");
                else
                {
                    AnalyzeExpression(let.Value);
                    var type = InferType(let.Value);
                    if (type.HasValue)
                        _symbols[let.Name] = type.Value;
                }
                break;

            case AssignmentStatement assign:
                if (!_symbols.ContainsKey(assign.Name))
                    _errors.Add($"{_module.SourcePath}: undefined variable '{assign.Name}'");
                AnalyzeExpression(assign.Value);
                break;

            case ExpressionStatement expr:
                AnalyzeExpression(expr.Expression);
                break;
        }
    }

    private void AnalyzeExpression(Expression expr)
    {
        switch (expr)
        {
            case VariableReferenceExpression varRef:
                if (!_symbols.ContainsKey(varRef.Name))
                    _errors.Add($"{_module.SourcePath}: undefined variable '{varRef.Name}'");
                break;

            case MethodCallExpression method:
                AnalyzeExpression(method.Receiver);
                foreach (var arg in method.Args)
                    AnalyzeExpression(arg);
                break;

            case CallExpression call:
                foreach (var arg in call.Args)
                    AnalyzeExpression(arg);
                break;

            case UnaryExpression unary:
                AnalyzeExpression(unary.Operand);
                break;

            case BinaryExpression binary:
                AnalyzeExpression(binary.Left);
                AnalyzeExpression(binary.Right);
                break;
        }
    }

    private SuruType? InferType(Expression expr) => expr switch
    {
        BoolLiteral                => SuruType.Bool,
        IntLiteral                 => SuruType.Int64,
        FloatLiteral               => SuruType.Float64,
        VariableReferenceExpression v => _symbols.TryGetValue(v.Name, out var t) ? t : null,
        MethodCallExpression m     => InferType(m.Receiver),
        UnaryExpression            => SuruType.Bool,
        BinaryExpression           => SuruType.Bool,
        _                          => null,
    };
}
