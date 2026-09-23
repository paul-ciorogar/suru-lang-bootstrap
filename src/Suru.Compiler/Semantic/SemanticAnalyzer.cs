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
    private readonly ScopeStack<Binding, NoScopeData> _scopes = new();

    /// <summary>
    /// The function whose body is being analyzed, which is what a <c>return</c> checks itself
    /// against. Saved and restored around each body rather than assigned once, because a
    /// function may be declared inside another one.
    /// </summary>
    private FunctionDeclaration? _function;

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
        // The file is a declaration context and its outermost scope is never entered, so the
        // pre-pass is run here by hand rather than by whatever opened the scope.
        DeclareSignatures(_module.Statements, isDeclarationContext: true);

        foreach (var statement in _module.Statements)
            AnalyzeStatement(statement);
        return _errors;
    }

    private void AnalyzeStatement(Statement statement)
    {
        switch (statement)
        {
            // TODO(discard): a call whose result is not 'void' is discarded here. Once the
            // language has a '_' binding, require one rather than letting a value vanish.
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
                AnalyzeScope(block);
                break;
            case FunctionDeclaration declaration:
                AnalyzeFunction(declaration);
                break;
            case ReturnStatement returned:
                AnalyzeReturn(returned);
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
    /// The one place semantics enters a scope, since the kind rides in on the node. Errors are
    /// collected rather than thrown, so the exit always runs.
    /// <para>
    /// A function body is this same block with two additions, which is why it goes through here
    /// rather than beside it: its parameters are declared in the scope the body itself opens — so
    /// a <c>let</c> naming a parameter is a redeclaration and not a shadow — and they are in
    /// place before the pre-pass, so a nested <c>fn</c> cannot be shadowed by one.
    /// </para>
    /// </summary>
    private void AnalyzeScope(BlockStatement block, FunctionDeclaration? declaration = null)
    {
        _scopes.EnterNew(block.Kind);

        if (declaration is not null)
            foreach (var parameter in declaration.Parameters)
                if (parameter.Type is { } parameterType)
                    _scopes.Declare(parameter.Name, new VariableBinding(parameterType));

        DeclareSignatures(block.Statements, IsDeclarationContext(block.Kind));

        foreach (var statement in block.Statements)
            AnalyzeStatement(statement);

        _scopes.Exit();
    }

    /// <summary>
    /// Whether a <c>fn</c> may be written in a list of statements. A file, a function body and a
    /// bare block all run exactly once when they are reached, so a declaration in one is a fact;
    /// an <c>if</c> arm and a <c>while</c> body are control flow, and whether a declaration there
    /// happens at all is not something the compiler can know.
    /// </summary>
    private static bool IsDeclarationContext(ScopeKind kind) =>
        kind is ScopeKind.Function or ScopeKind.Plain;

    /// <summary>
    /// Collects every signature in a statement list before any statement in it is analyzed. That
    /// order is the whole of what makes recursion, mutual recursion and a call to a function
    /// declared further down work, and it is why this is a pass rather than a lookup that
    /// resolves lazily.
    /// <para>
    /// A signature is registered even when a type name failed or the declaration is in a place it
    /// may not be, so a call reports its own problems instead of cascading into
    /// <c>unknown function</c> — the same reasoning <see cref="AnalyzeLet"/> gives for declaring
    /// a binding whose initialiser failed.
    /// </para>
    /// </summary>
    private void DeclareSignatures(IReadOnlyList<Statement> statements, bool isDeclarationContext)
    {
        foreach (var statement in statements)
        {
            if (statement is not FunctionDeclaration declaration)
                continue;

            declaration.ReturnType =
                ResolveReturnType(declaration.ReturnTypeName, declaration.ReturnTypePosition);

            var parameters = new List<SuruType?>(declaration.Parameters.Count);
            var names = new HashSet<string>();
            foreach (var parameter in declaration.Parameters)
            {
                parameter.Type = ResolveBindingType(parameter.TypeName, parameter.TypePosition);

                // An entry per declared parameter either way, so arity is reported from what was
                // written rather than from what resolved.
                parameters.Add(parameter.Type);

                if (!names.Add(parameter.Name))
                    Error(parameter.Position, $"'{parameter.Name}' is already declared");
            }

            // TODO: since declarations are scoped we could consider shadowing
            if (declaration.Name == PrintLn)
                Error(declaration.Position,
                    $"'{PrintLn}' is a builtin and cannot be redeclared");

            if (!isDeclarationContext)
                Error(declaration.Position,
                    "a function cannot be declared inside an 'if' or a 'while'");

            // One namespace: this is the collision with a 'let' of the same name, for free.
            if (_scopes.DeclaredHere(declaration.Name))
            {
                Error(declaration.Position, $"'{declaration.Name}' is already declared");
                continue;
            }

            _scopes.Declare(declaration.Name, new FunctionBinding(
                new FunctionSignature(declaration.Name, parameters, declaration.ReturnType)));
        }
    }

    /// <summary>
    /// The body is the block it looks like, plus the parameters. The declaration is tracked while
    /// it is analyzed so a <c>return</c> inside knows what it must carry, and restored after,
    /// because a function may be declared inside another one.
    /// </summary>
    private void AnalyzeFunction(FunctionDeclaration declaration)
    {
        var enclosing = _function;
        _function = declaration;
        AnalyzeScope(declaration.Body, declaration);
        _function = enclosing;

        if (declaration.ReturnType is { } returnType
            && returnType != SuruType.Void
            && !AlwaysReturns(declaration.Body))
            Error(declaration.Position,
                $"'{declaration.Name}' must return a value of type '{returnType}' on every path");
    }

    /// <summary>
    /// A <c>return</c> is checked against the function it sits in, which is the only thing that
    /// says what it may carry — and, at the top level of a file, that there is nothing to return
    /// from at all.
    /// </summary>
    private void AnalyzeReturn(ReturnStatement returned)
    {
        var valueType = returned.Value is null ? null : AnalyzeExpression(returned.Value);

        if (_function is not { } function)
        {
            Error(returned.Position, "'return' can only appear inside a function");
            return;
        }

        // A return type that did not resolve already reported itself; the value has been
        // analyzed for its own errors and there is nothing left to compare it against.
        if (function.ReturnType is not { } returnType)
            return;

        if (returnType == SuruType.Void)
        {
            if (returned.Value is not null)
                Error(returned.Position,
                    $"'{function.Name}' returns '{SuruType.Void}'; 'return' cannot carry a value");
            return;
        }

        if (returned.Value is null)
        {
            Error(returned.Position,
                $"'{function.Name}' must return a value of type '{returnType}'");
            return;
        }

        // A null type means the value already reported its own error.
        if (valueType is not null && valueType != returnType)
            Error(returned.Value.Position,
                $"cannot return a value of type '{valueType}' from '{function.Name}' "
                + $"of type '{returnType}'");
    }

    /// <summary>
    /// Whether every path out of a statement returns. Pure and stateless: the shape of the
    /// statement is the whole of the answer, since nothing folds a constant condition.
    /// <para>
    /// This has a contract with codegen, which emits an <c>unreachable</c> at the end of a body
    /// this says always returns. <b>Any</b> statement in a block, not the last, because a
    /// <c>return</c> makes the rest of the block unreachable and codegen stops emitting there
    /// too; and an <c>if</c> only with both arms present, because one arm is a path that falls
    /// through. A <c>while</c> body may never run, so no loop counts — which rejects
    /// <c>while true { return 1 }</c> as a whole body, an accepted cost while no constant
    /// folding exists.
    /// </para>
    /// </summary>
    private static bool AlwaysReturns(Statement statement) => statement switch
    {
        ReturnStatement => true,
        BlockStatement block => block.Statements.Any(AlwaysReturns),
        // 'else if' arrives as a nested IfStatement and needs no case of its own.
        IfStatement branch => branch.Else is not null
            && AlwaysReturns(branch.Then)
            && AlwaysReturns(branch.Else),
        WhileStatement => false,
        _ => false,
    };

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

    /// <summary>
    /// A type name in a position that binds a value: a <c>let</c>, or a parameter. <c>void</c> is
    /// rejected here and nowhere else, which is what makes it writable in the one position that
    /// does not bind — a return type. It is not <c>unknown type 'void'</c>: the compiler knows
    /// that type perfectly well, and what is wrong is binding to it.
    /// </summary>
    private SuruType? ResolveBindingType(string name, SourcePosition position)
    {
        if (name == SuruType.Void.Name)
        {
            Error(position, $"nothing can be bound to '{SuruType.Void}'");
            return null;
        }

        if (NamedTypes.TryGetValue(name, out var type))
            return type;

        Error(position, $"unknown type '{name}'");
        return null;
    }

    /// <summary>
    /// A function's written return type — <see cref="ResolveBindingType"/> plus <c>void</c>. The
    /// only caller is signature collection, so no binding position can reach the extra name.
    /// </summary>
    private SuruType? ResolveReturnType(string name, SourcePosition position) =>
        name == SuruType.Void.Name ? SuruType.Void : ResolveBindingType(name, position);

    private void AnalyzeLet(LetStatement let)
    {
        var valueType = AnalyzeExpression(let.Value);

        var declared = ResolveBindingType(let.TypeName, let.TypePosition);

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
            _scopes.Declare(let.Name, new VariableBinding(declared));
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

        if (VariableType(position, name) is not { } declared)
            return;

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

    private SuruType? ResolveIdentifier(IdentifierExpression identifier) =>
        VariableType(identifier.Position, identifier.Name);

    /// <summary>
    /// The type a name reads as, or null with the reason reported. Shared by every position that
    /// wants a value out of a name — an identifier, an assignment and a <c>#mock</c> — so the
    /// three cannot drift on what they say.
    /// <para>
    /// The lookup is the one the barrier hides variables from, so a name bound in an enclosing
    /// function is simply not there. A function name found instead is a different mistake from an
    /// unbound one and says so: the name <i>is</i> declared, and there are no function values to
    /// read it as.
    /// </para>
    /// </summary>
    private SuruType? VariableType(SourcePosition position, string name)
    {
        if (!_scopes.TryLookupVariable(name, out var binding))
        {
            Error(position, $"unknown variable '{name}'");
            return null;
        }

        if (binding is not VariableBinding variable)
        {
            Error(position, $"'{name}' is a function and cannot be used as a value");
            return null;
        }

        return variable.Type;
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

        // The builtin is matched by name before the scope is consulted, which is also what makes
        // 'printLn' a name no declaration can take.
        if (call.Name == PrintLn)
            return ResolvePrintLn(call);

        if (!_scopes.TryLookupFunction(call.Name, out var binding))
        {
            Error(call.Position, $"unknown function '{call.Name}'");
            return null;
        }

        if (binding is not FunctionBinding function)
        {
            Error(call.Position, $"'{call.Name}' is not a function");
            return null;
        }

        var signature = function.Signature;
        if (call.Args.Count != signature.Parameters.Count)
        {
            Error(call.Position, Arity(call.Name, signature.Parameters.Count, call.Args.Count));

            // Still the declared return type: the call site has a problem of its own, not an
            // unknown one, and saying so twice helps nobody.
            return signature.ReturnType;
        }

        for (int i = 0; i < call.Args.Count; i++)
        {
            // A null on either side already reported its own error.
            var argument = call.Args[i];
            if (argument.Type is { } argumentType
                && signature.Parameters[i] is { } parameterType
                && argumentType != parameterType)
                Error(argument.Position,
                    $"argument {i + 1} of '{call.Name}' is of type '{argumentType}'; "
                    + $"expected '{parameterType}'");
        }

        return signature.ReturnType;
    }

    private SuruType ResolvePrintLn(CallExpression call)
    {
        if (call.Args.Count != 1)
        {
            Error(call.Position, Arity(PrintLn, 1, call.Args.Count));
            return SuruType.Void;
        }

        // A null type means the argument already reported its own error.
        var argument = call.Args[0];
        if (argument.Type is { } argumentType && !PrintableTypes.Contains(argumentType))
            Error(argument.Position,
                $"'{PrintLn}' cannot print a value of type '{argumentType}'; expected {Printable()}");

        return SuruType.Void;
    }

    /// <summary>
    /// Shared so the builtin and a declared function cannot drift on how they count. The plural
    /// follows the number expected, which is what the word before "got" is about.
    /// </summary>
    private static string Arity(string name, int expected, int got) =>
        $"'{name}' expects {expected} argument{(expected == 1 ? "" : "s")}, got {got}";

    private static string Printable() =>
        string.Join(", ", PrintableTypes.Select(type => $"'{type}'"));

    private void Error(SourcePosition position, string message) =>
        _errors.Add($"{_module.SourcePath}({position.Line},{position.Column}): {message}");
}
