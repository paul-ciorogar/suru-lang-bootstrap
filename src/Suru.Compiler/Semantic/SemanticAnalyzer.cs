using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Semantic;

public sealed class SemanticAnalyzer
{
    private readonly Module _module;
    private readonly Dictionary<string, SuruType> _symbols = new();
    private readonly Dictionary<string, List<(string Name, SuruType Type)>> _structSymbols = new();
    private readonly Dictionary<string, SuruType> _arrayElementTypes = new();
    private readonly Dictionary<string, (IReadOnlyList<SuruType> ParamTypes, SuruType? ReturnType)> _functions = new();
    private readonly List<string> _errors = [];
    private SuruType? _currentFunctionReturnType = null;
    private bool _currentFunctionIsVoid = false;

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
            var t = ResolveTypeName(p.TypeName);
            if (t is null)
                _errors.Add($"{_module.SourcePath}: unknown type '{p.TypeName}' in parameter '{p.Name}' of function '{fn.Name}'");
            else
                paramTypes.Add(t.Value);
        }

        SuruType? returnType = fn.ReturnTypeName == "void" ? null : ResolveTypeName(fn.ReturnTypeName);
        if (fn.ReturnTypeName != "void" && returnType is null)
            _errors.Add($"{_module.SourcePath}: unknown return type '{fn.ReturnTypeName}' for function '{fn.Name}'");

        _functions[fn.Name] = (paramTypes, returnType);
    }

    private static SuruType? ResolveTypeName(string name) => name switch
    {
        "Bool"    => SuruType.Bool,
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
            {
                var outerSymbols = new Dictionary<string, SuruType>(_symbols);
                var outerStructSymbols = new Dictionary<string, List<(string, SuruType)>>(_structSymbols);
                var outerArrayElementTypes = new Dictionary<string, SuruType>(_arrayElementTypes);
                _symbols.Clear();
                _structSymbols.Clear();
                _arrayElementTypes.Clear();

                if (_functions.TryGetValue(fn.Name, out var sig))
                {
                    for (int i = 0; i < fn.Parameters.Count; i++)
                    {
                        if (i < sig.ParamTypes.Count)
                            _symbols[fn.Parameters[i].Name] = sig.ParamTypes[i];
                    }
                    // fn main(args Array) Int64: args elements are Strings (CLI argv)
                    if (fn.Name == "main")
                        for (int i = 0; i < fn.Parameters.Count; i++)
                            if (i < sig.ParamTypes.Count && sig.ParamTypes[i] == SuruType.Array)
                                _arrayElementTypes[fn.Parameters[i].Name] = SuruType.String;
                }

                _currentFunctionReturnType = sig.ReturnType;
                _currentFunctionIsVoid = fn.ReturnTypeName == "void";

                bool hasReturn = false;
                foreach (var bodyStmt in fn.Body)
                {
                    AnalyzeStatement(bodyStmt);
                    if (bodyStmt is ReturnStatement) hasReturn = true;
                }

                if (!_currentFunctionIsVoid && !hasReturn)
                    _errors.Add($"{_module.SourcePath}: non-void function '{fn.Name}' has no return statement");

                _symbols.Clear();
                _structSymbols.Clear();
                _arrayElementTypes.Clear();
                foreach (var kv in outerSymbols) _symbols[kv.Key] = kv.Value;
                foreach (var kv in outerStructSymbols) _structSymbols[kv.Key] = kv.Value;
                foreach (var kv in outerArrayElementTypes) _arrayElementTypes[kv.Key] = kv.Value;
                _currentFunctionReturnType = null;
                _currentFunctionIsVoid = false;
                break;
            }

            case ReturnStatement ret:
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
                break;

            case LetStatement let:
                if (_symbols.ContainsKey(let.Name))
                    _errors.Add($"{_module.SourcePath}: variable '{let.Name}' is already declared");
                else
                {
                    AnalyzeExpression(let.Value);
                    var type = InferType(let.Value);
                    if (type.HasValue)
                    {
                        _symbols[let.Name] = type.Value;
                        if (type.Value == SuruType.Struct)
                            PropagateStructMeta(let.Name, let.Value);
                        if (type.Value == SuruType.Array)
                            PropagateArrayMeta(let.Name, let.Value);
                    }
                }
                break;

            case FieldAssignmentStatement fieldAssign:
            {
                AnalyzeExpression(fieldAssign.Value);
                if (fieldAssign.Receiver is VariableReferenceExpression rv)
                {
                    if (!_symbols.ContainsKey(rv.Name))
                        _errors.Add($"{_module.SourcePath}: undefined variable '{rv.Name}'");
                    else if (_structSymbols.TryGetValue(rv.Name, out var structFields) &&
                             !structFields.Any(f => f.Name == fieldAssign.FieldName))
                        _errors.Add($"{_module.SourcePath}: struct '{rv.Name}' has no field '{fieldAssign.FieldName}'");
                }
                break;
            }

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

    private void PropagateArrayMeta(string varName, Expression value)
    {
        switch (value)
        {
            case ArrayLiteralExpression arr when arr.Elements.Count > 0:
            {
                var elemType = InferType(arr.Elements[0]);
                if (elemType.HasValue) _arrayElementTypes[varName] = elemType.Value;
                break;
            }
            case VariableReferenceExpression v when _arrayElementTypes.TryGetValue(v.Name, out var et):
                _arrayElementTypes[varName] = et;
                break;
            case CallExpression { Name: "clone", Args.Count: 1 } call
                when call.Args[0] is VariableReferenceExpression src
                  && _arrayElementTypes.TryGetValue(src.Name, out var srcEt):
                _arrayElementTypes[varName] = srcEt;
                break;
        }
    }

    private void PropagateStructMeta(string varName, Expression value)
    {
        switch (value)
        {
            case StructLiteralExpression lit:
            {
                var fields = new List<(string Name, SuruType Type)>();
                foreach (var (name, expr) in lit.Fields)
                {
                    var t = InferType(expr);
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
        }
    }

    private void AnalyzeExpression(Expression expr)
    {
        switch (expr)
        {
            case VariableReferenceExpression varRef:
                // Type names used as static-method receivers (e.g. Int64.from(...)) are not variables.
                if (varRef.Name is not ("Int64" or "Float64" or "Bool") && !_symbols.ContainsKey(varRef.Name))
                    _errors.Add($"{_module.SourcePath}: undefined variable '{varRef.Name}'");
                break;

            case ArrayLiteralExpression arrLit:
            {
                foreach (var elem in arrLit.Elements)
                    AnalyzeExpression(elem);
                if (arrLit.Elements.Count > 1)
                {
                    var firstType = InferType(arrLit.Elements[0]);
                    foreach (var elem in arrLit.Elements.Skip(1))
                    {
                        var et = InferType(elem);
                        if (firstType.HasValue && et.HasValue && et.Value != firstType.Value)
                            _errors.Add($"{_module.SourcePath}: array elements must have the same type");
                    }
                }
                break;
            }

            case StringLiteralExpression:
                break;

            case StructLiteralExpression lit:
                foreach (var (_, fieldVal) in lit.Fields)
                    AnalyzeExpression(fieldVal);
                break;

            case FieldAccessExpression fa:
                AnalyzeExpression(fa.Receiver);
                if (fa.Receiver is VariableReferenceExpression fv &&
                    _structSymbols.TryGetValue(fv.Name, out var structFields) &&
                    !structFields.Any(f => f.Name == fa.FieldName))
                    _errors.Add($"{_module.SourcePath}: struct '{fv.Name}' has no field '{fa.FieldName}'");
                break;

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
                    if (exitArgType.HasValue && exitArgType.Value != SuruType.Int64)
                        _errors.Add($"{_module.SourcePath}: 'exit' expects Int64, got {exitArgType.Value}");
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
                if (call.Name != "printLn" && _functions.TryGetValue(call.Name, out var callSig))
                {
                    if (call.Args.Count != callSig.ParamTypes.Count)
                        _errors.Add($"{_module.SourcePath}: function '{call.Name}' called with {call.Args.Count} argument(s), expected {callSig.ParamTypes.Count}");
                    else
                    {
                        for (int i = 0; i < call.Args.Count; i++)
                        {
                            AnalyzeExpression(call.Args[i]);
                            var argType = InferType(call.Args[i]);
                            if (argType.HasValue && argType.Value != callSig.ParamTypes[i])
                                _errors.Add($"{_module.SourcePath}: argument {i + 1} of '{call.Name}' has type {argType.Value}, expected {callSig.ParamTypes[i]}");
                        }
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
                    AnalyzeExpression(arm.Body);
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
        MethodCallExpression { MethodName: "equals" or "lessThan" } => SuruType.Bool,
        MethodCallExpression { MethodName: "len" }   => SuruType.Int64,
        MethodCallExpression { MethodName: "toString" } => SuruType.String,
        MethodCallExpression { MethodName: "at" } m
            when m.Receiver is VariableReferenceExpression rv
              && _arrayElementTypes.TryGetValue(rv.Name, out var elemType) => elemType,
        MethodCallExpression { MethodName: "at" } m
            when InferType(m.Receiver) == SuruType.String => SuruType.String,
        MethodCallExpression { MethodName: "slice" or "append" } m
            when InferType(m.Receiver) == SuruType.String => SuruType.String,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: "Int64" }   => SuruType.Int64,
        MethodCallExpression { MethodName: "from" } m
            when m.Receiver is VariableReferenceExpression { Name: "Float64" } => SuruType.Float64,
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
