using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly Module _module;
    private readonly Dictionary<string, SuruType> _symbols = new();
    private readonly Dictionary<string, List<(string Name, SuruType Type)>> _structSymbols = new();
    private readonly Dictionary<string, (IReadOnlyList<SuruType> ParamTypes, SuruType? ReturnType)> _functions = new();
    private readonly HashSet<string> _constants = new();
    private readonly List<string> _errors = [];
    private SuruType? _currentFunctionReturnType = null;
    private bool _currentFunctionIsVoid = false;
    private string? _currentFunctionName = null;
    private bool _insideFunction = false;
    private readonly Dictionary<string, List<(string Name, SuruType Type)>> _functionReturnStructSymbols = new();

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
            if (stmt is FunctionDeclaration fn) RegisterFunction(fn);
        foreach (var stmt in _module.Statements)
            AnalyzeStatement(stmt);
        return _errors;
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

    private static SuruType? ResolveTypeAnnotation(TypeAnnotation ann) => ann.Name switch
    {
        "Bool"    => SuruType.Bool,
        "Int32"   => SuruType.Int32,
        "Int64"   => SuruType.Int64,
        "Float64" => SuruType.Float64,
        "Struct"  => SuruType.Struct,
        "Array"   => SuruType.Array,
        "String"  => SuruType.String,
        _         => null,
    };

    private void AnalyzeStatement(Statement stmt)
    {
        switch (stmt)
        {
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
        foreach (var bodyStmt in whileStmt.Body)
            AnalyzeStatement(bodyStmt);
    }

    private void AnalyzeAssignmentStatement(AssignmentStatement assign)
    {
        if (_constants.Contains(assign.Name))
            _errors.Add($"{_module.SourcePath}: cannot reassign constant '{assign.Name}'");
        else if (!_symbols.ContainsKey(assign.Name))
            _errors.Add($"{_module.SourcePath}: undefined variable '{assign.Name}'");
        AnalyzeExpression(assign.Value);
    }

    private void AnalyzeFieldAssignmentStatement(FieldAssignmentStatement fieldAssign)
    {
        AnalyzeExpression(fieldAssign.Value);
        if (fieldAssign.Receiver is VariableReferenceExpression rv &&
            !_symbols.ContainsKey(rv.Name))
            _errors.Add($"{_module.SourcePath}: undefined variable '{rv.Name}'");
    }

    private void AnalyzeLetStatement(LetStatement let)
    {
        if (_symbols.ContainsKey(let.Name))
            _errors.Add($"{_module.SourcePath}: variable '{let.Name}' is already declared");
        else
        {
            AnalyzeExpression(let.Value);
            SuruType? type = ResolveTypeAnnotation(let.TypeAnnotation);
            if (type is null)
                _errors.Add($"{_module.SourcePath}: unknown type '{let.TypeAnnotation}' for variable '{let.Name}'");
            else
            {
                _symbols[let.Name] = type.Value;
                if (type.Value == SuruType.Struct)
                    PropagateStructMeta(let.Name, let.Value);
            }
            if (!_insideFunction)
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
            if (_currentFunctionName != null && ret.Value is StructLiteralExpression retLit)
            {
                var fields = new List<(string Name, SuruType Type)>();
                foreach (var (name, typeAnn, _) in retLit.Fields)
                {
                    var t = ResolveTypeAnnotation(typeAnn);
                    if (t.HasValue) fields.Add((name, t.Value));
                }
                if (fields.Count > 0)
                    _functionReturnStructSymbols[_currentFunctionName] = fields;
            }
        }
    }

    private void AnalyzeFunctionDeclaration(FunctionDeclaration fn)
    {
        var outerSymbols = new Dictionary<string, SuruType>(_symbols);
        var outerStructSymbols = new Dictionary<string, List<(string, SuruType)>>(_structSymbols);
        _symbols.Clear();
        _structSymbols.Clear();
        foreach (var kv in outerSymbols)
            if (_constants.Contains(kv.Key))
                _symbols[kv.Key] = kv.Value;

        if (_functions.TryGetValue(fn.Name, out var sig))
        {
            for (int i = 0; i < fn.Parameters.Count; i++)
                if (i < sig.ParamTypes.Count)
                    _symbols[fn.Parameters[i].Name] = sig.ParamTypes[i];
        }

        _currentFunctionReturnType = sig.ReturnType;
        _currentFunctionIsVoid = fn.ReturnType.Name == "void";
        _currentFunctionName = fn.Name;
        _insideFunction = true;

        bool hasReturn = false;
        foreach (var bodyStmt in fn.Body)
        {
            AnalyzeStatement(bodyStmt);
            if (bodyStmt is ReturnStatement) hasReturn = true;
            if (bodyStmt is ExpressionStatement { Expression: CallExpression { Name: "exit" } })
                hasReturn = true;
        }

        if (!_currentFunctionIsVoid && !hasReturn && fn.Name != "main")
            _errors.Add($"{_module.SourcePath}: non-void function '{fn.Name}' has no return statement");

        _symbols.Clear();
        _structSymbols.Clear();
        foreach (var kv in outerSymbols) _symbols[kv.Key] = kv.Value;
        foreach (var kv in outerStructSymbols) _structSymbols[kv.Key] = kv.Value;
        _currentFunctionReturnType = null;
        _currentFunctionIsVoid = false;
        _currentFunctionName = null;
        _insideFunction = false;
    }

    private void PropagateStructMeta(string varName, Expression value)
    {
        switch (value)
        {
            case StructLiteralExpression lit:
                {
                    var fields = new List<(string Name, SuruType Type)>();
                    foreach (var (name, typeAnn, _) in lit.Fields)
                    {
                        var t = ResolveTypeAnnotation(typeAnn);
                        if (t.HasValue) fields.Add((name, t.Value));
                    }
                    _structSymbols[varName] = fields;
                    break;
                }
            case VariableReferenceExpression v when _structSymbols.TryGetValue(v.Name, out var meta):
                _structSymbols[varName] = new List<(string, SuruType)>(meta);
                break;
            case CallExpression { Name: "clone", Args.Count: 1 } call
                when call.Args[0] is VariableReferenceExpression src
                  && _structSymbols.TryGetValue(src.Name, out var srcMeta):
                _structSymbols[varName] = new List<(string, SuruType)>(srcMeta);
                break;
            case CallExpression call when _functionReturnStructSymbols.TryGetValue(call.Name, out var fnMeta):
                _structSymbols[varName] = new List<(string, SuruType)>(fnMeta);
                break;
            case MatchExpression match when match.Arms.Count > 0:
                PropagateStructMeta(varName, match.Arms[0].Body);
                break;
        }
    }

    private void AnalyzeExpression(Expression expr)
    {
        switch (expr)
        {
            case VariableReferenceExpression varRef:
                if (varRef.Name is not ("Int32" or "Int64" or "Float64" or "Bool")
                    && !_symbols.ContainsKey(varRef.Name)
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
                foreach (var (_, _, fieldVal) in lit.Fields)
                    AnalyzeExpression(fieldVal);
                break;

            case FieldAccessExpression fa:
                AnalyzeExpression(fa.Receiver);
                fa.ResolvedType = InferType(fa);
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
    }

    private SuruType? InferType(Expression expr) => expr switch
    {
        BoolLiteral                => SuruType.Bool,
        IntLiteral                 => SuruType.Int64,
        FloatLiteral               => SuruType.Float64,
        StructLiteralExpression    => SuruType.Struct,
        ArrayLiteralExpression     => SuruType.Array,
        StringLiteralExpression    => SuruType.String,
        FieldAccessExpression fa when fa.Receiver is VariableReferenceExpression fv
            && _structSymbols.TryGetValue(fv.Name, out var fields)
            => fields.FirstOrDefault(f => f.Name == fa.FieldName) is var field && field.Name != null
                ? field.Type : null,
        VariableReferenceExpression v => _symbols.TryGetValue(v.Name, out var t) ? t : null,
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
