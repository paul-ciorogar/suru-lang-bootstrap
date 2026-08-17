using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Semantic;

/// <summary>
/// Resolves and checks the program, annotating every expression with its
/// <see cref="SuruType"/> so codegen can emit from the type rather than the
/// shape of the syntax node. Errors are collected, not thrown, so a single run
/// reports every problem in the file.
/// </summary>
public sealed class SemanticAnalyzer
{
    private const string PrintLn = "printLn";

    private static readonly SuruType[] PrintableTypes = [SuruType.Bool, SuruType.I64, SuruType.F64];

    private readonly Module _module;
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
        foreach (var statement in _module.Statements)
            AnalyzeStatement(statement);
        return _errors;
    }

    private void AnalyzeStatement(Statement statement)
    {
        switch (statement)
        {
            case ExpressionStatement { Expression: CallExpression call }:
                AnalyzeExpression(call);
                break;
            case ExpressionStatement exprStmt:
                AnalyzeExpression(exprStmt.Expression);
                Error(exprStmt.Position, "only call expressions are allowed as statements");
                break;
            default:
                Error(statement.Position, $"unsupported statement '{statement.GetType().Name}'");
                break;
        }
    }

    private SuruType? AnalyzeExpression(Expression expression)
    {
        var type = Resolve(expression);
        expression.Type = type;
        return type;
    }

    private SuruType? Resolve(Expression expression)
    {
        switch (expression)
        {
            case BoolLiteral:
                return SuruType.Bool;
            case IntLiteral:
                return SuruType.I64;
            case FloatLiteral:
                return SuruType.F64;
            case CallExpression call:
                return ResolveCall(call);
            default:
                Error(expression.Position, $"unsupported expression '{expression.GetType().Name}'");
                return null;
        }
    }

    private SuruType? ResolveCall(CallExpression call)
    {
        foreach (var arg in call.Args)
            AnalyzeExpression(arg);

        if (call.Name != PrintLn)
        {
            Error(call.Position, $"unknown function '{call.Name}'");
            return null;
        }

        if (call.Args.Count != 1)
        {
            Error(call.Position, $"'{PrintLn}' expects 1 argument, got {call.Args.Count}");
            return SuruType.Void;
        }

        // A null type means the argument already reported its own error.
        var argument = call.Args[0];
        if (argument.Type is { } argumentType && !PrintableTypes.Contains(argumentType))
            Error(argument.Position,
                $"'{PrintLn}' cannot print a value of type '{argumentType}'; expected {Printable()}");

        return SuruType.Void;
    }

    private static string Printable() =>
        string.Join(", ", PrintableTypes.Select(type => $"'{type}'"));

    private void Error(SourcePosition position, string message) =>
        _errors.Add($"{_module.SourcePath}({position.Line},{position.Column}): {message}");
}
