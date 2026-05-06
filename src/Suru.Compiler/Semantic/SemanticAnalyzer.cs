using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly Module _module;

    // Scope stack: index 0 (bottom) = module scope, top = innermost block.
    // Each frame maps variable name → SuruType. LookupSymbol walks top→bottom.
    private readonly Stack<Dictionary<string, SuruType>> _scopes = new();

    private readonly Dictionary<string, (IReadOnlyList<SuruType> ParamTypes, SuruType? ReturnType)> _functions = new();
    private readonly HashSet<string> _constants = new();
    private readonly List<string> _errors = [];
    private SuruType? _currentFunctionReturnType = null;
    private string? _currentFunctionReturnTypeName = null;
    private bool _currentFunctionIsVoid = false;
    private string? _currentFunctionName = null;
    private readonly Dictionary<string, TypeDeclaration> _typeDeclarations = new();
    // Maps variable/parameter name → declared element type name for Array<TypeName> annotations.
    // Enables InferType to resolve field types through arr.at(i).field chains.
    private readonly Dictionary<string, string> _arrayElementTypeNames = new();
    // Maps variable/parameter name → declared struct type name for named-type annotations.
    // Enables InferType to resolve field types for var.field access chains.
    private readonly Dictionary<string, string> _varStructTypeNames = new();

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
        PushScope(); // module scope — lives for the entire analysis pass
        foreach (var stmt in _module.Statements)
            if (stmt is TypeDeclaration td) RegisterTypeDeclaration(td);
        foreach (var stmt in _module.Statements)
            if (stmt is FunctionDeclaration fn) RegisterFunction(fn);
        foreach (var stmt in _module.Statements)
            AnalyzeStatement(stmt);
        PopScope();
        return _errors;
    }

    // ─── Scope helpers ───────────────────────────────────────────────────────

    private void PushScope() => _scopes.Push(new Dictionary<string, SuruType>());

    private void PopScope() => _scopes.Pop();

    // Walks the scope stack from innermost to outermost.
    private SuruType? LookupSymbol(string name)
    {
        foreach (var scope in _scopes)
            if (scope.TryGetValue(name, out var t)) return t;
        return null;
    }

    // Only checks the current (innermost) scope — used for duplicate-declaration detection.
    private bool ExistsInCurrentScope(string name) =>
        _scopes.Count > 0 && _scopes.Peek().ContainsKey(name);

    private void DeclareSymbol(string name, SuruType type) =>
        _scopes.Peek()[name] = type;

    // Records the element type name for Array<TypeName> annotations so InferType can
    // resolve field access through arr.at(i).field chains.
    private void RecordArrayElementType(string varName, TypeAnnotation ann)
    {
        if (ann.Name == "Array" && ann.TypeParam is { } elemAnn && elemAnn.Name != "Array")
            _arrayElementTypeNames[varName] = elemAnn.Name;
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
        if (_functions.ContainsKey(fn.Name))
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
                paramTypes.Add(t.Value);
        }

        SuruType? returnType = fn.ReturnType.Name is "void" ? null : ResolveTypeAnnotation(fn.ReturnType);
        if (fn.ReturnType.Name is not "void" && returnType is null)
            _errors.Add($"{_module.SourcePath}: unknown return type '{fn.ReturnType}' for function '{fn.Name}'");

        _functions[fn.Name] = (paramTypes, returnType);
    }

    // Non-static so it can consult _typeDeclarations for user-defined named types.
    private SuruType? ResolveTypeAnnotation(TypeAnnotation ann) => ann.Name switch
    {
        "Bool"    => SuruType.Bool,
        "Int32"   => SuruType.Int32,
        "Int64"   => SuruType.Int64,
        "Float64" => SuruType.Float64,
        "Array"   => SuruType.Array,
        "String"  => SuruType.String,
        // Named types declared via `type Foo: { ... }` resolve to SuruType.Struct
        // at the semantic level — the runtime uses the same linked-list struct layout.
        _ => _typeDeclarations.ContainsKey(ann.Name) ? SuruType.Struct : null,
    };

    // ─── Statement analysis ───────────────────────────────────────────────────

    private void AnalyzeStatement(Statement stmt)
    {
        switch (stmt)
        {
            case TypeDeclaration:
                // Already registered in the pre-pass; nothing more to analyze.
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
        if (condType.HasValue && condType.Value != SuruType.Bool)
            _errors.Add($"{_module.SourcePath}: while condition must be Bool, got {condType.Value}");
        PushScope();
        foreach (var bodyStmt in whileStmt.Body)
            AnalyzeStatement(bodyStmt);
        PopScope();
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        if (_constants.Contains(assign.Name))
            _errors.Add($"{_module.SourcePath}: cannot reassign constant '{assign.Name}'");
        else if (LookupSymbol(assign.Name) is null)
            _errors.Add($"{_module.SourcePath}: undefined variable '{assign.Name}'");
        AnalyzeExpression(assign.Value);
    }

    private void AnalyzeFieldAssignmentStatement(FieldAssignmentStatement fieldAssign)
    {
        AnalyzeExpression(fieldAssign.Value);
        if (fieldAssign.Receiver is VariableReferenceExpression rv &&
            LookupSymbol(rv.Name) is null)
            _errors.Add($"{_module.SourcePath}: undefined variable '{rv.Name}'");
    }

    private void AnalyzeLetStatement(LetStatement let)
    {
        // Duplicate check is scoped to the current block — shadowing an outer scope is allowed.
        if (ExistsInCurrentScope(let.Name))
            _errors.Add($"{_module.SourcePath}: variable '{let.Name}' is already declared");
        else
        {
            AnalyzeExpression(let.Value);
            SuruType? type = ResolveTypeAnnotation(let.TypeAnnotation);
            if (type is null)
                _errors.Add($"{_module.SourcePath}: unknown type '{let.TypeAnnotation}' for variable '{let.Name}'");
            else
            {
                DeclareSymbol(let.Name, type.Value);
                RecordArrayElementType(let.Name, let.TypeAnnotation);
                if (type.Value == SuruType.Struct)
                {
                    ValidateStructLiteralFields(let.Value, let.TypeAnnotation.Name);
                    _varStructTypeNames[let.Name] = let.TypeAnnotation.Name;
                }
            }
            // Module-scope lets (stack depth == 1) are constants — reassignment is forbidden.
            if (_scopes.Count == 1)
                _constants.Add(let.Name);
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
            if (_currentFunctionReturnType.HasValue)
            {
                var retType = InferType(ret.Value);
                if (retType.HasValue && retType.Value != _currentFunctionReturnType.Value)
                    _errors.Add($"{_module.SourcePath}: return type mismatch: expected {_currentFunctionReturnType.Value}, got {retType.Value}");
            }
        }
    }

    private void AnalyzeFunctionDeclaration(FunctionDeclaration fn)
    {
        // Each function gets a fresh scope frame; the module scope stays below it on the
        // stack, so module-level constants remain visible through LookupSymbol.
        PushScope();

        _functions.TryGetValue(fn.Name, out var sig);
        for (int i = 0; i < fn.Parameters.Count; i++)
        {
            if (i < sig.ParamTypes.Count)
                DeclareSymbol(fn.Parameters[i].Name, sig.ParamTypes[i]);
            RecordArrayElementType(fn.Parameters[i].Name, fn.Parameters[i].TypeAnnotation);
            if (i < sig.ParamTypes.Count && sig.ParamTypes[i] == SuruType.Struct)
                _varStructTypeNames[fn.Parameters[i].Name] = fn.Parameters[i].TypeAnnotation.Name;
        }

        _currentFunctionReturnType    = sig.ReturnType;
        _currentFunctionReturnTypeName = fn.ReturnType.Name;
        _currentFunctionIsVoid        = fn.ReturnType.Name == "void";
        _currentFunctionName          = fn.Name;

        // Conservative reachability: treat any return/exit anywhere in the body as
        // satisfying the non-void return requirement. Full CFG analysis is out of scope.
        bool hasReturn = CheckHasReturn(fn.Body);
        foreach (var bodyStmt in fn.Body)
            AnalyzeStatement(bodyStmt);

        if (!_currentFunctionIsVoid && !hasReturn && fn.Name != "main")
            _errors.Add($"{_module.SourcePath}: non-void function '{fn.Name}' has no return statement");

        PopScope();
        _currentFunctionReturnType     = null;
        _currentFunctionReturnTypeName = null;
        _currentFunctionIsVoid         = false;
        _currentFunctionName           = null;
    }

    // Returns true if any reachable statement in stmts is a return or exit.
    // Recurses into while bodies; match arm bodies are expressions so they can't contain returns.
    private bool CheckHasReturn(IReadOnlyList<Statement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is ReturnStatement) return true;
            if (stmt is ExpressionStatement { Expression: CallExpression { Name: "exit" } }) return true;
            if (stmt is WhileStatement ws && CheckHasReturn(ws.Body)) return true;
        }
        return false;
    }

    // Validates that a struct literal's field names match the declared type (when one exists).
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
                if (varRef.Name is not ("Int32" or "Int64" or "Float64" or "Bool")
                    && LookupSymbol(varRef.Name) is null
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
                    if (_functions.TryGetValue(qualifiedName, out var nsSig))
                    {
                        if (nsCall.Args.Count != nsSig.ParamTypes.Count)
                            _errors.Add($"{_module.SourcePath}: function '{qualifiedName}' called with {nsCall.Args.Count} argument(s), expected {nsSig.ParamTypes.Count}");
                        else
                        {
                            for (int i = 0; i < nsCall.Args.Count; i++)
                                AnalyzeExpression(nsCall.Args[i]);
                        }
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

            case CallExpression { Name: "clone" or "drop" } builtIn:
                if (builtIn.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: '{builtIn.Name}' expects exactly 1 argument");
                else
                    AnalyzeExpression(builtIn.Args[0]);
                break;

            case CallExpression { Name: "exit" } exitCall:
                if (exitCall.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: 'exit' expects exactly 1 argument");
                else
                {
                    AnalyzeExpression(exitCall.Args[0]);
                    var exitArgType = InferType(exitCall.Args[0]);
                    if (exitArgType.HasValue && exitArgType.Value is not (SuruType.Int64 or SuruType.Int32))
                        _errors.Add($"{_module.SourcePath}: 'exit' expects Int64 or Int32, got {exitArgType.Value}");
                }
                break;

            case CallExpression { Name: "readFile" } readFileCall:
                if (readFileCall.Args.Count != 1)
                    _errors.Add($"{_module.SourcePath}: 'readFile' expects exactly 1 argument");
                else
                {
                    AnalyzeExpression(readFileCall.Args[0]);
                    var rfArgType = InferType(readFileCall.Args[0]);
                    if (rfArgType.HasValue && rfArgType.Value != SuruType.String)
                        _errors.Add($"{_module.SourcePath}: 'readFile' expects String, got {rfArgType.Value}");
                }
                break;

            case CallExpression { Name: "writeFile" } writeFileCall:
                if (writeFileCall.Args.Count != 2)
                    _errors.Add($"{_module.SourcePath}: 'writeFile' expects exactly 2 arguments");
                else
                {
                    AnalyzeExpression(writeFileCall.Args[0]);
                    AnalyzeExpression(writeFileCall.Args[1]);
                    var wfArg0Type = InferType(writeFileCall.Args[0]);
                    var wfArg1Type = InferType(writeFileCall.Args[1]);
                    if (wfArg0Type.HasValue && wfArg0Type.Value != SuruType.String)
                        _errors.Add($"{_module.SourcePath}: 'writeFile' argument 1 expects String, got {wfArg0Type.Value}");
                    if (wfArg1Type.HasValue && wfArg1Type.Value != SuruType.String)
                        _errors.Add($"{_module.SourcePath}: 'writeFile' argument 2 expects String, got {wfArg1Type.Value}");
                }
                break;

            case CallExpression call:
                if (call.Name != "printLn" && call.Name != "printError" && _functions.TryGetValue(call.Name, out var callSig))
                {
                    if (call.Args.Count != callSig.ParamTypes.Count)
                        _errors.Add($"{_module.SourcePath}: function '{call.Name}' called with {call.Args.Count} argument(s), expected {callSig.ParamTypes.Count}");
                    else
                    {
                        for (int i = 0; i < call.Args.Count; i++)
                            AnalyzeExpression(call.Args[i]);
                    }
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
                if (condType.HasValue && condType.Value is not (SuruType.Bool or SuruType.Int64 or SuruType.Float64 or SuruType.String))
                    _errors.Add($"{_module.SourcePath}: match condition must be Bool, Int64, Float64, or String, got {condType.Value}");
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern != null) AnalyzeExpression(arm.Pattern);
                    AnalyzeExpression(arm.Body);
                }
                break;
        }
        // Annotate every expression with its resolved type so codegen never has to guess.
        expr.ResolvedType = InferType(expr);
    }

    // ─── Type inference ───────────────────────────────────────────────────────

    private SuruType? InferType(Expression expr) => expr switch
    {
        BoolLiteral                => SuruType.Bool,
        IntLiteral                 => SuruType.Int64,
        FloatLiteral               => SuruType.Float64,
        StructLiteralExpression    => SuruType.Struct,
        ArrayLiteralExpression     => SuruType.Array,
        StringLiteralExpression    => SuruType.String,
        // arr.at(i).field — receiver is a method call on an Array<TypeName> variable.
        FieldAccessExpression fa
            when fa.Receiver is MethodCallExpression { MethodName: "at",
                                                       Receiver: VariableReferenceExpression arrVar }
              && _arrayElementTypeNames.TryGetValue(arrVar.Name, out var elemTypeName)
              && _typeDeclarations.TryGetValue(elemTypeName, out var elemTd)
            => elemTd.Fields.FirstOrDefault(f => f.Field == fa.FieldName) is var fld && fld.Field != null
                ? ResolveTypeAnnotation(fld.Type) : null,
        // var.field — receiver is a local variable with a known named struct type.
        FieldAccessExpression fa when fa.Receiver is VariableReferenceExpression fv
            && _varStructTypeNames.TryGetValue(fv.Name, out var structTypeName)
            && _typeDeclarations.TryGetValue(structTypeName, out var structTd)
            => structTd.Fields.FirstOrDefault(f => f.Field == fa.FieldName) is var fld && fld.Field != null
                ? ResolveTypeAnnotation(fld.Type) : null,
        VariableReferenceExpression v => LookupSymbol(v.Name),
        MethodCallExpression { MethodName: "compare" } => SuruType.Int64,
        MethodCallExpression { MethodName: "equals" or "lt" or "gt" or "lte" or "gte" } => SuruType.Bool,
        MethodCallExpression { MethodName: "len" or "ord" } => SuruType.Int64,
        MethodCallExpression { MethodName: "toString" } => SuruType.String,
        MethodCallExpression { MethodName: "at" } m
            when InferType(m.Receiver) == SuruType.String => SuruType.String,
        MethodCallExpression { MethodName: "at" } => null,   // array element type unknown at compile time
        MethodCallExpression { MethodName: "slice" or "append" } m
            when InferType(m.Receiver) == SuruType.String => SuruType.String,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: "Int32" }   => SuruType.Int32,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: "Int64" }   => SuruType.Int64,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: "Float64" } => SuruType.Float64,
        MethodCallExpression nsCall
            when nsCall.Receiver is VariableReferenceExpression nsRef2
              && _module.Namespaces.Contains(nsRef2.Name)
              && _functions.TryGetValue(nsRef2.Name + "." + nsCall.MethodName, out var nsFnSig)
            => nsFnSig.ReturnType,
        MethodCallExpression m     => InferType(m.Receiver),
        UnaryExpression            => SuruType.Bool,
        BinaryExpression           => SuruType.Bool,
        MatchExpression m          => m.Arms.Count > 0 ? InferType(m.Arms[0].Body) : null,
        CallExpression { Name: "clone" }    => SuruType.Struct,
        CallExpression { Name: "drop" }    => null,
        CallExpression { Name: "exit" }    => null,
        CallExpression { Name: "readFile" } => SuruType.String,
        CallExpression { Name: "writeFile" } => null,
        CallExpression call when _functions.TryGetValue(call.Name, out var fnSig) => fnSig.ReturnType,
        _                          => null,
    };
}
