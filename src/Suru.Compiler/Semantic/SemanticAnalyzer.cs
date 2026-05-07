using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly Module _module;

    private readonly Scopes _scopes = new();

    private readonly List<string> _errors = [];
    private SuruType? _currentFunctionReturnType = null;
    private bool _currentFunctionIsVoid = false;
    private readonly Dictionary<string, TypeDeclaration> _typeDeclarations = new();

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
        _scopes.Enter(); // module scope — lives for the entire analysis pass
        foreach (var stmt in _module.Statements)
            if (stmt is TypeDeclaration td) RegisterTypeDeclaration(td);
        foreach (var stmt in _module.Statements)
            if (stmt is FunctionDeclaration fn) RegisterFunction(fn);
        foreach (var stmt in _module.Statements)
            AnalyzeStatement(stmt);
        _scopes.Exit();
        return _errors;
    }

    // ─── Pre-pass registrations ───────────────────────────────────────────────

    private void RegisterTypeDeclaration(TypeDeclaration td)
    {
        if (_typeDeclarations.ContainsKey(td.Name))
        {
            _errors.Add($"{_module.SourcePath}: type '{td.Name}' is already declared");
            return;
        }
        _typeDeclarations[td.Name] = td;
    }

    private void RegisterFunction(FunctionDeclaration fn)
    {
        if (_scopes.FunctionExistsInCurrent(fn.Name))
        {
            _errors.Add($"{_module.SourcePath}: function '{fn.Name}' is already declared");
            return;
        }

        var paramTypes = new List<SuruType>();
        foreach (var p in fn.Parameters)
        {
            var t = ResolveTypeAnnotation(p.TypeAnnotation);
            if (t is null)
                _errors.Add($"{_module.SourcePath}: unknown type '{p.TypeAnnotation}' in parameter '{p.Name}' of function '{fn.Name}'");
            else
            {
                paramTypes.Add(t);
                p.ResolvedType = t;
            }
        }

        SuruType? returnType = fn.ReturnType.Name is BuiltinNames.Void ? null : ResolveTypeAnnotation(fn.ReturnType);
        if (fn.ReturnType.Name is not BuiltinNames.Void && returnType is null)
            _errors.Add($"{_module.SourcePath}: unknown return type '{fn.ReturnType}' for function '{fn.Name}'");

        _scopes.RegisterFunction(fn.Name, new FunctionSig(paramTypes, returnType));
    }

    // Resolves a type annotation to a SuruType.
    // Returns null when the name is unrecognised (caller reports the error).
    private SuruType? ResolveTypeAnnotation(TypeAnnotation ann)
        => SuruTypeSystem.TryResolve(ann, _typeDeclarations);

    // ─── Statement analysis ───────────────────────────────────────────────────

    private void AnalyzeStatement(Statement stmt)
    {
        switch (stmt)
        {
            case TypeDeclaration:
                break;

            case FunctionDeclaration fn:
                AnalyzeFunctionDeclaration(fn);
                break;

            case ReturnStatement ret:
                AnalyzeReturnStatement(ret);
                break;

            case LetStatement let:
                AnalyzeLetStatement(let);
                break;

            case FieldAssignmentStatement fieldAssign:
                AnalyzeFieldAssignmentStatement(fieldAssign);
                break;

            case AssignmentStatement assign:
                AnalyzeAssignmentStatement(assign);
                break;

            case WhileStatement whileStmt:
                AnalyzeWhileStatement(whileStmt);
                break;

            case ExpressionStatement expr:
                AnalyzeExpression(expr.Expression);
                break;
        }
    }

    private void AnalyzeWhileStatement(WhileStatement whileStmt)
    {
        AnalyzeExpression(whileStmt.Condition);
        var condType = InferType(whileStmt.Condition);
        if (condType is not null && condType is not SuruType.BoolType)
            _errors.Add($"{_module.SourcePath}: while condition must be Bool, got {condType}");
        _scopes.Enter();
        foreach (var bodyStmt in whileStmt.Body)
            AnalyzeStatement(bodyStmt);
        _scopes.Exit();
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        if (_scopes.IsConstant(assign.Name))
            _errors.Add($"{_module.SourcePath}: cannot reassign constant '{assign.Name}'");
        else if (_scopes.Lookup(assign.Name) is null)
            _errors.Add($"{_module.SourcePath}: undefined variable '{assign.Name}'");
        AnalyzeExpression(assign.Value);
    }

    private void AnalyzeFieldAssignmentStatement(FieldAssignmentStatement fieldAssign)
    {
        AnalyzeExpression(fieldAssign.Value);
        if (fieldAssign.Receiver is VariableReferenceExpression rv &&
            _scopes.Lookup(rv.Name) is null)
            _errors.Add($"{_module.SourcePath}: undefined variable '{rv.Name}'");
    }

    private void AnalyzeLetStatement(LetStatement let)
    {
        if (_scopes.ExistsInCurrent(let.Name))
            _errors.Add($"{_module.SourcePath}: variable '{let.Name}' is already declared");
        else
        {
            AnalyzeExpression(let.Value);
            SuruType? type = ResolveTypeAnnotation(let.TypeAnnotation);
            if (type is null)
                _errors.Add($"{_module.SourcePath}: unknown type '{let.TypeAnnotation}' for variable '{let.Name}'");
            else
            {
                _scopes.DeclareInCurrent(let.Name, type);
                if (type is SuruType.NamedType)
                    ValidateStructLiteralFields(let.Value, let.TypeAnnotation.Name);
            }
            if (_scopes.Count == 1)
                _scopes.MarkConstant(let.Name);
        }
    }

    private void AnalyzeReturnStatement(ReturnStatement ret)
    {
        if (ret.Value is null)
        {
            if (!_currentFunctionIsVoid)
                _errors.Add($"{_module.SourcePath}: bare 'return' in non-void function");
        }
        else
        {
            AnalyzeExpression(ret.Value);
            if (_currentFunctionReturnType is not null)
            {
                var retType = InferType(ret.Value);
                if (retType is not null && !retType.Equals(_currentFunctionReturnType))
                    _errors.Add($"{_module.SourcePath}: return type mismatch: expected {_currentFunctionReturnType}, got {retType}");
            }
        }
    }

    private void AnalyzeFunctionDeclaration(FunctionDeclaration fn)
    {
        _scopes.Enter();

        var sig = _scopes.LookupFunction(fn.Name);
        for (int i = 0; i < fn.Parameters.Count; i++)
        {
            if (sig is not null && i < sig.ParamTypes.Count)
                _scopes.DeclareInCurrent(fn.Parameters[i].Name, sig.ParamTypes[i]);
        }

        _currentFunctionReturnType = sig?.ReturnType;
        _currentFunctionIsVoid     = fn.ReturnType.Name == BuiltinNames.Void;

        bool hasReturn = CheckHasReturn(fn.Body);
        foreach (var bodyStmt in fn.Body)
            AnalyzeStatement(bodyStmt);

        if (!_currentFunctionIsVoid && !hasReturn && fn.Name != BuiltinNames.Main)
            _errors.Add($"{_module.SourcePath}: non-void function '{fn.Name}' has no return statement");

        _scopes.Exit();
        _currentFunctionReturnType = null;
        _currentFunctionIsVoid     = false;
    }

    private static bool CheckHasReturn(IReadOnlyList<Statement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is ReturnStatement) return true;
            if (stmt is ExpressionStatement { Expression: CallExpression { Name: BuiltinNames.Exit } }) return true;
            if (stmt is WhileStatement ws && CheckHasReturn(ws.Body)) return true;
        }
        return false;
    }

    private void ValidateStructLiteralFields(Expression value, string typeName)
    {
        if (value is not StructLiteralExpression lit) return;
        if (!_typeDeclarations.TryGetValue(typeName, out var typeDecl)) return;

        var declaredNames = typeDecl.Fields.Select(f => f.Field).ToHashSet();
        foreach (var (fname, _) in lit.Fields)
            if (!declaredNames.Contains(fname))
                _errors.Add($"{_module.SourcePath}: struct '{typeName}' has no field '{fname}'");
        if (lit.Fields.Count != typeDecl.Fields.Count)
            _errors.Add($"{_module.SourcePath}: struct '{typeName}' expects {typeDecl.Fields.Count} field(s), got {lit.Fields.Count}");
    }

    // ─── Expression analysis ──────────────────────────────────────────────────

    private void AnalyzeExpression(Expression expr)
    {
        switch (expr)
        {
            case VariableReferenceExpression varRef:
                if (varRef.Name is not (BuiltinNames.Int32 or BuiltinNames.Int64 or BuiltinNames.Float64 or BuiltinNames.Bool)
                    && _scopes.Lookup(varRef.Name) is null
                    && !_module.Namespaces.Contains(varRef.Name))
                    _errors.Add($"{_module.SourcePath}: undefined variable '{varRef.Name}'");
                break;

            case ArrayLiteralExpression arrLit:
                foreach (var elem in arrLit.Elements)
                    AnalyzeExpression(elem);
                break;

            case StringLiteralExpression:
                break;

            case StructLiteralExpression lit:
                foreach (var (_, fieldVal) in lit.Fields)
                    AnalyzeExpression(fieldVal);
                break;

            case FieldAccessExpression fa:
                AnalyzeExpression(fa.Receiver);
                break;

            case MethodCallExpression nsCall
                when nsCall.Receiver is VariableReferenceExpression nsRef
                  && _module.Namespaces.Contains(nsRef.Name):
                {
                    var qualifiedName = nsRef.Name + "." + nsCall.MethodName;
                    var nsSig = _scopes.LookupFunction(qualifiedName);
                    if (nsSig is not null)
                    {
                        if (nsCall.Args.Count != nsSig.ParamTypes.Count)
                            _errors.Add($"{_module.SourcePath}: function '{qualifiedName}' called with {nsCall.Args.Count} argument(s), expected {nsSig.ParamTypes.Count}");
                        else
                            for (int i = 0; i < nsCall.Args.Count; i++)
                                AnalyzeExpression(nsCall.Args[i]);
                    }
                    else
                    {
                        _errors.Add($"{_module.SourcePath}: unknown function '{qualifiedName}'");
                    }
                    break;
                }

            case MethodCallExpression method:
                AnalyzeExpression(method.Receiver);
                foreach (var arg in method.Args)
                    AnalyzeExpression(arg);
                break;

            case CallExpression { Name: BuiltinNames.PrintLn or BuiltinNames.PrintError } printCall:
                if (printCall.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: '{printCall.Name}' expects exactly 1 argument");
                else
                    AnalyzeExpression(printCall.Args[0]);
                break;

            case CallExpression { Name: BuiltinNames.Clone or BuiltinNames.Drop } builtIn:
                if (builtIn.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: '{builtIn.Name}' expects exactly 1 argument");
                else
                    AnalyzeExpression(builtIn.Args[0]);
                break;

            case CallExpression { Name: BuiltinNames.Exit } exitCall:
                if (exitCall.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: 'exit' expects exactly 1 argument");
                else
                {
                    AnalyzeExpression(exitCall.Args[0]);
                    var exitArgType = InferType(exitCall.Args[0]);
                    if (exitArgType is not null and not SuruType.Int64Type and not SuruType.Int32Type)
                        _errors.Add($"{_module.SourcePath}: 'exit' expects Int64 or Int32, got {exitArgType}");
                }
                break;

            case CallExpression { Name: BuiltinNames.ReadFile } readFileCall:
                if (readFileCall.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: 'readFile' expects exactly 1 argument");
                else
                {
                    AnalyzeExpression(readFileCall.Args[0]);
                    var rfArgType = InferType(readFileCall.Args[0]);
                    if (rfArgType is not null and not SuruType.StringType)
                        _errors.Add($"{_module.SourcePath}: 'readFile' expects String, got {rfArgType}");
                }
                break;

            case CallExpression { Name: BuiltinNames.WriteFile } writeFileCall:
                if (writeFileCall.Args.Count != 2)
                    _errors.Add($"{_module.SourcePath}: 'writeFile' expects exactly 2 arguments");
                else
                {
                    AnalyzeExpression(writeFileCall.Args[0]);
                    AnalyzeExpression(writeFileCall.Args[1]);
                    var wfArg0Type = InferType(writeFileCall.Args[0]);
                    var wfArg1Type = InferType(writeFileCall.Args[1]);
                    if (wfArg0Type is not null and not SuruType.StringType)
                        _errors.Add($"{_module.SourcePath}: 'writeFile' argument 1 expects String, got {wfArg0Type}");
                    if (wfArg1Type is not null and not SuruType.StringType)
                        _errors.Add($"{_module.SourcePath}: 'writeFile' argument 2 expects String, got {wfArg1Type}");
                }
                break;

            case CallExpression call:
                var callSig = _scopes.LookupFunction(call.Name);
                if (callSig is not null)
                {
                    if (call.Args.Count != callSig.ParamTypes.Count)
                        _errors.Add($"{_module.SourcePath}: function '{call.Name}' called with {call.Args.Count} argument(s), expected {callSig.ParamTypes.Count}");
                    else
                        for (int i = 0; i < call.Args.Count; i++)
                            AnalyzeExpression(call.Args[i]);
                }
                else
                {
                    foreach (var arg in call.Args)
                        AnalyzeExpression(arg);
                }
                break;

            case UnaryExpression unary:
                AnalyzeExpression(unary.Operand);
                break;

            case BinaryExpression binary:
                AnalyzeExpression(binary.Left);
                AnalyzeExpression(binary.Right);
                break;

            case MatchExpression match:
                AnalyzeExpression(match.Condition);
                var condType = InferType(match.Condition);
                if (condType is not null
                    and not SuruType.BoolType
                    and not SuruType.Int64Type
                    and not SuruType.Float64Type
                    and not SuruType.StringType)
                    _errors.Add($"{_module.SourcePath}: match condition must be Bool, Int64, Float64, or String, got {condType}");
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern != null) AnalyzeExpression(arm.Pattern);
                    AnalyzeExpression(arm.Body);
                }
                break;
        }
        expr.ResolvedType = InferType(expr);
    }

    // ─── Type inference ───────────────────────────────────────────────────────

    private SuruType? InferType(Expression expr) => expr switch
    {
        BoolLiteral                => SuruType.Bool,
        IntLiteral                 => SuruType.Int64,
        FloatLiteral               => SuruType.Float64,
        StringLiteralExpression    => SuruType.String,
        ArrayLiteralExpression     => null,   // element type depends on annotation context
        StructLiteralExpression    => null,   // type depends on annotation context

        // arr.at(i).field — receiver is a method call on an Array<NamedType> variable.
        FieldAccessExpression fa
            when fa.Receiver is MethodCallExpression { MethodName: "at",
                                                       Receiver: VariableReferenceExpression arrVar }
              && _scopes.Lookup(arrVar.Name) is SuruType.ArrayType { Element: SuruType.NamedType elemNt }
              && _typeDeclarations.TryGetValue(elemNt.Name, out var elemTd)
            => elemTd.Fields.FirstOrDefault(f => f.Field == fa.FieldName) is var fld && fld.Field != null
                ? ResolveTypeAnnotation(fld.Type) : null,

        // var.field — receiver is a local variable with a known named struct type.
        FieldAccessExpression fa
            when fa.Receiver is VariableReferenceExpression fv
              && _scopes.Lookup(fv.Name) is SuruType.NamedType structNt
              && _typeDeclarations.TryGetValue(structNt.Name, out var structTd)
            => structTd.Fields.FirstOrDefault(f => f.Field == fa.FieldName) is var fld && fld.Field != null
                ? ResolveTypeAnnotation(fld.Type) : null,

        VariableReferenceExpression v  => _scopes.Lookup(v.Name),
        MethodCallExpression { MethodName: "compare" }                              => SuruType.Int64,
        MethodCallExpression { MethodName: "equals" or "lt" or "gt" or "lte" or "gte" } => SuruType.Bool,
        MethodCallExpression { MethodName: "len" or "ord" }                         => SuruType.Int64,
        MethodCallExpression { MethodName: "toString" }                             => SuruType.String,
        MethodCallExpression { MethodName: "at" } m
            when InferType(m.Receiver) is SuruType.StringType                      => SuruType.String,
        MethodCallExpression { MethodName: "at" } m
            when InferType(m.Receiver) is SuruType.ArrayType at                    => at.Element,
        MethodCallExpression { MethodName: "slice" or "append" } m
            when InferType(m.Receiver) is SuruType.StringType                      => SuruType.String,
        MethodCallExpression { MethodName: "slice" } m
            when InferType(m.Receiver) is SuruType.ArrayType at                    => at,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: BuiltinNames.Int32 }    => SuruType.Int32,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: BuiltinNames.Int64 }    => SuruType.Int64,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: BuiltinNames.Float64 }  => SuruType.Float64,
        MethodCallExpression nsCall
            when nsCall.Receiver is VariableReferenceExpression nsRef2
              && _module.Namespaces.Contains(nsRef2.Name)
              && _scopes.LookupFunction(nsRef2.Name + "." + nsCall.MethodName) is { } nsFnSig
            => nsFnSig.ReturnType,
        MethodCallExpression m => InferType(m.Receiver),
        UnaryExpression        => SuruType.Bool,
        BinaryExpression       => SuruType.Bool,
        MatchExpression m      => m.Arms.Count > 0 ? InferType(m.Arms[0].Body) : null,
        CallExpression { Name: BuiltinNames.Clone }     => null,   // type propagated from annotation context
        CallExpression { Name: BuiltinNames.Drop }      => null,
        CallExpression { Name: BuiltinNames.Exit }      => null,
        CallExpression { Name: BuiltinNames.ReadFile }  => SuruType.String,
        CallExpression { Name: BuiltinNames.WriteFile } => null,
        CallExpression call when _scopes.LookupFunction(call.Name) is { } fnSig => fnSig.ReturnType,
        _                                    => null,
    };
}
