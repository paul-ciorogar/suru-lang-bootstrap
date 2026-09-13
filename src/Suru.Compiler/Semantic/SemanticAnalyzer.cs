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

    /// <summary>
    /// The bindings in scope, and what kind of scope each one is. A block enters a scope and
    /// exits it again. Semantics needs nothing per scope beyond the kind — only codegen has a
    /// payload to carry — so the scope data is <see cref="NoScopeData"/>.
    /// </summary>
    private readonly ScopeStack<SuruType, NoScopeData> _scopes = new();

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
            case BlockStatement block:
                // Errors are collected rather than thrown, so the exit always runs. The only
                // place semantics enters a scope, since the kind rides in on the node.
                _scopes.EnterNew(block.Kind);
                foreach (var inner in block.Statements)
                    AnalyzeStatement(inner);
                _scopes.Exit();
                break;
            case IfStatement branch:
                AnalyzeIf(branch);
                break;
            case WhileStatement loop:
                AnalyzeWhile(loop);
                break;
            case BreakStatement:
                RequireInLoop(statement.Position, "break");
                break;
            case ContinueStatement:
                RequireInLoop(statement.Position, "continue");
                break;
            case MockDirective mock:
                // A mock is an assignment, down to the diagnostics it produces.
                AnalyzeStore(mock.NamePosition, mock.Name, mock.Value);
                break;
            case ViewDirective view:
                AnalyzeView(view);
                break;
            case AssertDirective assert:
                AnalyzeAssert(assert);
                break;
            default:
                Error(statement.Position, $"unsupported statement '{statement.GetType().Name}'");
                break;
        }
    }

    /// <summary>
    /// Each arm is analyzed as the ordinary statement it is, which is where its scope comes from
    /// and why <c>else if</c> needs nothing of its own here.
    /// </summary>
    private void AnalyzeIf(IfStatement branch)
    {
        RequireBoolCondition("if", "branch on", branch.Condition);

        AnalyzeStatement(branch.Then);
        if (branch.Else is not null)
            AnalyzeStatement(branch.Else);
    }

    /// <summary>
    /// The body is analyzed as the ordinary block it is, so its scope costs nothing here — the
    /// only thing a loop adds is that a <c>break</c> or <c>continue</c> inside it now has
    /// somewhere to go, and the body block carries the loop kind that says so. Analyzing a loop
    /// is therefore nothing but checking its condition.
    /// </summary>
    private void AnalyzeWhile(WhileStatement loop)
    {
        RequireBoolCondition("while", "loop on", loop.Condition);
        AnalyzeStatement(loop.Body);
    }

    /// <summary>
    /// A condition must already be a <c>bool</c> — nothing converts implicitly, so a number is
    /// not a truth value. Shared by <c>if</c> and <c>while</c> so the two cannot drift apart in
    /// what they accept, which is the reason <see cref="AnalyzeStore"/> is shared too. Only
    /// <paramref name="verb"/> differs, because "branch on" is wrong for a loop.
    /// </summary>
    private void RequireBoolCondition(string keyword, string verb, Expression condition)
    {
        var type = AnalyzeExpression(condition);

        // A null type means the condition already reported its own error.
        if (type is not null && type != SuruType.Bool)
            Error(condition.Position,
                $"'{keyword}' cannot {verb} a value of type '{type}'; expected '{SuruType.Bool}'");
    }

    /// <summary>
    /// <c>break</c> and <c>continue</c> pick their loop by nesting, so being inside one at all is
    /// the whole of what there is to check.
    /// </summary>
    private void RequireInLoop(SourcePosition position, string keyword)
    {
        if (!_scopes.TryFindEnclosing(ScopeKind.Loop, out _))
            Error(position, $"'{keyword}' can only appear inside a loop");
    }

    private void AnalyzeLet(LetStatement let)
    {
        var valueType = AnalyzeExpression(let.Value);

        var declared = NamedTypes.GetValueOrDefault(let.TypeName);
        if (declared is null)
            Error(let.TypePosition, $"unknown type '{let.TypeName}'");

        // Only the innermost scope: a name may shadow an outer binding, but not one of its own.
        if (_scopes.DeclaredHere(let.Name))
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
            _scopes.Declare(let.Name, declared);
    }

    private void AnalyzeAssignment(AssignmentStatement assignment) =>
        AnalyzeStore(assignment.Position, assignment.Name, assignment.Value);

    /// <summary>
    /// Checks a store into an existing binding. Shared by <c>&lt;name&gt;: &lt;value&gt;</c>
    /// and by <c>#mock</c>, which is that statement restricted to test builds — so the two
    /// cannot drift apart in what they accept or in what they say when they refuse.
    /// </summary>
    private void AnalyzeStore(SourcePosition position, string name, Expression value)
    {
        var valueType = AnalyzeExpression(value);

        if (!_scopes.TryLookup(name, out var declared))
        {
            Error(position, $"unknown variable '{name}'");
            return;
        }

        if (valueType is not null && valueType != declared)
            Error(value.Position,
                $"cannot assign a value of type '{valueType}' to '{name}' of type '{declared}'");
    }

    /// <summary>A view prints its subject, so it accepts exactly what <c>printLn</c> does.</summary>
    private void AnalyzeView(ViewDirective view)
    {
        var type = AnalyzeExpression(view.Subject);
        if (type is not null && !PrintableTypes.Contains(type))
            Error(view.Subject.Position,
                $"'#view' cannot show a value of type '{type}'; expected {Printable()}");
    }

    /// <summary>
    /// An assertion compares, then prints both sides, so its operands must satisfy the rule
    /// <c>=</c> already applies and be printable — which for the types that exist today is
    /// the same set twice over.
    /// </summary>
    private void AnalyzeAssert(AssertDirective assert)
    {
        var actual = AnalyzeExpression(assert.Actual);
        var expected = AnalyzeExpression(assert.Expected);

        // A null type means that operand already reported its own error.
        if (actual is null || expected is null)
            return;

        if (actual != expected)
        {
            Error(assert.Position,
                $"'#assert' cannot compare '{actual}' with '{expected}'");
            return;
        }

        if (!PrintableTypes.Contains(actual))
            Error(assert.Position,
                $"'#assert' cannot compare values of type '{actual}'; expected {Printable()}");
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
        if (_scopes.TryLookup(identifier.Name, out var type))
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
