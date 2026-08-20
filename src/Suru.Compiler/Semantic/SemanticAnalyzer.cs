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

    private static readonly SuruType[] NumericTypes = [SuruType.I64, SuruType.F64];

    /// <summary>The types a program can write down, by name. `void` is not among them.</summary>
    private static readonly Dictionary<string, SuruType> NamedTypes = new()
    {
        [SuruType.Bool.Name] = SuruType.Bool,
        [SuruType.I64.Name] = SuruType.I64,
        [SuruType.F64.Name] = SuruType.F64,
    };

    private readonly Module _module;
    private readonly List<string> _errors = [];

    /// <summary>One flat scope: there are no blocks or functions to nest one inside yet.</summary>
    private readonly Dictionary<string, SuruType> _bindings = [];

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
            case LetStatement let:
                AnalyzeLet(let);
                break;
            case AssignmentStatement assignment:
                AnalyzeAssignment(assignment);
                break;
            default:
                Error(statement.Position, $"unsupported statement '{statement.GetType().Name}'");
                break;
        }
    }

    private void AnalyzeLet(LetStatement let)
    {
        var valueType = AnalyzeExpression(let.Value);

        var declared = NamedTypes.GetValueOrDefault(let.TypeName);
        if (declared is null)
            Error(let.TypePosition, $"unknown type '{let.TypeName}'");

        if (_bindings.ContainsKey(let.Name))
        {
            Error(let.Position, $"'{let.Name}' is already declared");
            return;
        }

        if (declared is not null && valueType is not null && valueType != declared)
            Error(let.Value.Position,
                $"cannot bind a value of type '{valueType}' to '{let.Name}' of type '{declared}'");

        // Registered even when the initialiser or the type name failed, so later uses
        // of the name report their own problems instead of 'unknown variable'.
        if (declared is not null)
            _bindings[let.Name] = declared;
    }

    private void AnalyzeAssignment(AssignmentStatement assignment)
    {
        var valueType = AnalyzeExpression(assignment.Value);

        if (!_bindings.TryGetValue(assignment.Name, out var declared))
        {
            Error(assignment.Position, $"unknown variable '{assignment.Name}'");
            return;
        }

        if (valueType is not null && valueType != declared)
            Error(assignment.Value.Position,
                $"cannot assign a value of type '{valueType}' to '{assignment.Name}' of type '{declared}'");
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
            case IdentifierExpression identifier:
                return ResolveIdentifier(identifier);
            case BinaryExpression binary:
                return ResolveBinary(binary);
            case UnaryExpression unary:
                return ResolveUnary(unary);
            default:
                Error(expression.Position, $"unsupported expression '{expression.GetType().Name}'");
                return null;
        }
    }

    private SuruType? ResolveIdentifier(IdentifierExpression identifier)
    {
        if (_bindings.TryGetValue(identifier.Name, out var type))
            return type;

        Error(identifier.Position, $"unknown variable '{identifier.Name}'");
        return null;
    }

    /// <summary>
    /// Both operands must already have the same type — Suru converts nothing
    /// implicitly, so an operator either accepts that type or it does not.
    /// </summary>
    private SuruType? ResolveBinary(BinaryExpression binary)
    {
        var left = AnalyzeExpression(binary.Left);
        var right = AnalyzeExpression(binary.Right);

        // A null operand type means that operand already reported its own error.
        if (left is null || right is null)
            return null;

        var op = binary.Operator;
        var accepted = op switch
        {
            BinaryOperator.And or BinaryOperator.Or => (SuruType[])[SuruType.Bool],
            BinaryOperator.Equal or BinaryOperator.NotEqual => [SuruType.Bool, SuruType.I64, SuruType.F64],
            _ => NumericTypes,
        };

        if (left != right || !accepted.Contains(left))
        {
            Error(binary.Position,
                $"operator '{Operators.Text(op)}' cannot be applied to '{left}' and '{right}'");
            return null;
        }

        return op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
                or BinaryOperator.LessOrEqual or BinaryOperator.Greater
                or BinaryOperator.GreaterOrEqual => SuruType.Bool,
            _ => left,
        };
    }

    private SuruType? ResolveUnary(UnaryExpression unary)
    {
        var operand = AnalyzeExpression(unary.Operand);
        if (operand is null)
            return null;

        SuruType[] accepted = unary.Operator == UnaryOperator.Not ? [SuruType.Bool] : NumericTypes;
        if (!accepted.Contains(operand))
        {
            Error(unary.Position,
                $"operator '{Operators.Text(unary.Operator)}' cannot be applied to '{operand}'");
            return null;
        }

        return operand;
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
