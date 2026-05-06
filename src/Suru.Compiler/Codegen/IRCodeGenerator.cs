// Covered fixtures: print, exit_test, print-error, negative-literals, arithmetic, comparisons,
//                   control-flow, fibonacci, while-loop, strings, include-test, file_io, file_io_write, arrays, structs,
//                   suru-lexer
using System.Globalization;
using System.Text;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Emits LLVM IR text (.ll) as a direct replacement for the LLVMSharp-based CodeGenerator.
//
// Value representation model (Stage 12.5f and later):
//   Scalars (Bool/Int32/Int64/Float64) use raw LLVM types (i1/i32/i64/double) in local
//   variables, function parameters, and return values. No heap allocation for scalars.
//   Non-scalars (String/ArrayType/NamedType) remain `ptr` to heap-allocated tagged structs.
//   Box calls (@suru_box_*) appear only at three runtime boundaries:
//     1. printLn/printError — runtime dispatches via type_tag
//     2. Array element store/load — suru_array_add/set/at take/return ptr
//     3. Struct field store/load — field val slot is ptrtoint(ptr)
//
// Partial class split:
//   IRFunctionCodeGenerator.cs — EmitFunction, EmitStmt, EmitMainWrapper
//   IRMatchCodeGenerator.cs    — EmitMatch*, PeekType*, EmitMatchTestChain
//   IRBoxCodeGenerator.cs      — Box/Unbox helpers, type utilities, NextTmp
//   IRFileIoCodeGenerator.cs   — EmitArgAt, EmitReadFile, EmitWriteFile
//   IRStringCodeGenerator.cs   — string methods and helpers
//   IRArrayCodeGenerator.cs    — array methods and helpers
//   IRStructCodeGenerator.cs   — struct methods and helpers
public sealed partial class IRCodeGenerator
{
    private readonly Module _module;
    private readonly string _sourceName;

    private readonly StringBuilder _funcs   = new();
    private readonly StringBuilder _helpers = new();
    private int _tmp;

    private readonly Externals _externals                   = new();
    private readonly SuruRuntimeDeclarations _runtimeDecls  = new();
    private BoolStirngGlobals _boolStringGlobals            = new();

    private readonly Dictionary<string, (string name, int byteLen)> _stringLiterals = new();
    private int _strCount;

    // Per-function variable table: name → (alloca SSA name, SuruType).
    // ArrayType carries element type; NamedType carries struct name — no side dicts needed.
    private Dictionary<string, (string ptr, SuruType type)> _vars = new();

    private bool _blockOpen;
    private int  _matchCounter;
    private int  _whileCounter;

    private readonly Dictionary<string, (IReadOnlyList<FunctionParameter> Params, SuruType ReturnType)>
        _userFunctions = new();

    // LLVM return type of the function currently being emitted.
    // suru_main is always "i64"; void Suru functions emit "ptr" (ret ptr null).
    private string   _currentFnReturnLlvmType = "i64";
    private SuruType _currentFnReturnSuruType = SuruType.Int64;
    // Suru type annotation name of the current function's return type.
    // Used to thread the declared type into EmitStructLiteral for return-statement struct literals.
    private string?  _currentFnReturnTypeName;

    // Module-level constant globals: name → (LLVM global name, SuruType).
    private readonly Dictionary<string, (string GlobalName, SuruType Type)> _globalVars = new();

    // Tracks which Array variables are the argv Seq (built by @main from char**).
    private HashSet<string> _argvVars = new();

    private IRCodeGenerator(Module module, string sourceName)
    {
        _module = module;
        _sourceName = sourceName;
    }

    public static string Generate(Module module, string sourceName = "suru_module")
        => new IRCodeGenerator(module, sourceName).Emit();

    // ─── Pass 1 + assembly ───────────────────────────────────────────────────

    private string Emit()
    {
        _externals.AddMalloc();

        // Pass 0: record module-level scalar constants.
        foreach (var stmt in _module.Statements)
            if (stmt is LetStatement { Name: var cName, Value: var cVal } &&
                cVal is BoolLiteral or IntLiteral or FloatLiteral)
            {
                var cType = cVal switch
                {
                    BoolLiteral  => (SuruType)SuruType.Bool,
                    IntLiteral   => SuruType.Int64,
                    FloatLiteral => SuruType.Float64,
                    _            => throw new InvalidOperationException("unreachable"),
                };
                _globalVars[cName] = ($"@{cName}", cType);
            }

        // Pre-pass: register all non-main function signatures.
        foreach (var stmt in _module.Statements)
            if (stmt is FunctionDeclaration { Name: not "main" } fn)
                _userFunctions[fn.Name] = (fn.Parameters, FnReturnSuruType(fn));

        // Pass 1: emit all function bodies.
        foreach (var stmt in _module.Statements)
            if (stmt is FunctionDeclaration fn)
                EmitFunction(fn);

        // Pass 2: assemble the .ll file.
        var sb = new StringBuilder();
        sb.AppendLine($"; ModuleID = '{_sourceName}'");
        sb.AppendLine($"source_filename = \"{_sourceName}\"");
        sb.AppendLine();
        sb.AppendLine("%suru.String = type { i64, i64, ptr }");             // { type_tag=6, len, data }
        sb.AppendLine("%suru.Array  = type { i64, i64, i64, i64, ptr }");  // { type_tag=5, elem_tag, len, cap, data }
        sb.AppendLine("%suru.Field  = type { i64, ptr, i32, i64, ptr }");  // { type_tag=4, name, field_tag, val, next }
        sb.AppendLine("%suru.Box    = type { i64, i64 }");                  // { type_tag, payload }
        sb.AppendLine();

        // Module-level constant globals — stored as raw LLVM types; boxed on each load.
        foreach (var stmt in _module.Statements)
            if (stmt is LetStatement { Name: var gName, Value: var gVal } &&
                _globalVars.TryGetValue(gName, out var gEntry))
            {
                var (llvmType, initVal) = gVal switch
                {
                    BoolLiteral b  => ("i1",     b.Value ? "1" : "0"),
                    IntLiteral i   => ("i64",    i.Value.ToString(CultureInfo.InvariantCulture)),
                    FloatLiteral f => ("double", $"0x{BitConverter.DoubleToInt64Bits(f.Value):X16}"),
                    _              => throw new InvalidOperationException("unreachable"),
                };
                sb.AppendLine($"{gEntry.GlobalName} = internal constant {llvmType} {initVal}");
            }
        if (_globalVars.Count > 0) sb.AppendLine();

        sb.Append(_boolStringGlobals.ToString());

        foreach (var (text, (name, byteLen)) in _stringLiterals)
        {
            var escaped = EscapeStringForIR(text);
            sb.AppendLine($"{name} = private unnamed_addr constant [{byteLen} x i8] c\"{escaped}\\00\"");
        }
        sb.AppendLine();

        sb.Append(_externals.ToString());
        sb.Append(_runtimeDecls.ToString());
        sb.AppendLine();

        sb.Append(_helpers);
        sb.Append(_funcs);

        var hasMain = _module.Statements.OfType<FunctionDeclaration>().Any(f => f.Name == "main");
        if (hasMain) EmitMainWrapper(sb);

        return sb.ToString();
    }

    // ─── Expression emission ─────────────────────────────────────────────────

    private (string val, SuruType type) EmitValue(Expression expr) => expr switch
    {
        BoolLiteral b              => (b.Value ? "1" : "0", SuruType.Bool),
        IntLiteral i               => (i.Value.ToString(CultureInfo.InvariantCulture), SuruType.Int64),
        FloatLiteral f             => ($"0x{BitConverter.DoubleToInt64Bits(f.Value):X16}", SuruType.Float64),
        StringLiteralExpression s  => EmitStringLiteralValue(s.Value),
        ArrayLiteralExpression arr => EmitArrayLiteral(arr),
        StructLiteralExpression sl => EmitStructLiteral(sl, null),
        FieldAccessExpression fa   => EmitFieldAccess(fa),
        CallExpression { Name: "clone", Args: [var cloneArg] } => EmitCloneDyn(cloneArg),
        CallExpression { Name: "drop",  Args: [var dropArg]  } => EmitDropDyn(dropArg),
        VariableReferenceExpression v  => EmitLoad(v.Name),
        MethodCallExpression m         => EmitMethodCall(m),
        UnaryExpression { Op: UnaryOp.Not } u => EmitBoolNot(u.Operand),
        BinaryExpression bin           => EmitBinaryExpr(bin),
        MatchExpression match          => EmitMatchAsExpression(match),
        CallExpression { Name: "readFile", Args: [var pathArg] } => EmitReadFile(pathArg),
        CallExpression c when _userFunctions.ContainsKey(c.Name) => EmitUserFunctionCall(c),
        _ => throw new NotSupportedException($"IR codegen: unsupported expression {expr.GetType().Name}"),
    };

    // Dynamic clone/drop: read type_tag at offset 0 and dispatch at runtime.
    private (string val, SuruType type) EmitCloneDyn(Expression arg)
    {
        var (val, type) = EmitValue(arg);
        if (IsScalar(type)) return (val, type);
        _runtimeDecls.AddCloneDyn();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_clone_dyn(ptr {val})");
        return (tmp, type);
    }

    private (string val, SuruType type) EmitDropDyn(Expression arg)
    {
        var (val, type) = EmitValue(arg);
        if (IsScalar(type)) return ("0", SuruType.Bool);
        _runtimeDecls.AddDropDyn();
        _funcs.AppendLine($"  call void @suru_drop_dyn(ptr {val})");
        return ("0", SuruType.Bool);
    }

    private (string val, SuruType type) EmitBoolNot(Expression operand)
    {
        var (v, vtype) = EmitValue(operand);
        var raw = IsScalar(vtype) ? v : UnboxBool(v);
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = xor i1 {raw}, true");
        return (tmp, SuruType.Bool);
    }

    private (string val, SuruType type) EmitBinaryExpr(BinaryExpression bin)
    {
        var (lv, ltype) = EmitValue(bin.Left);
        var (rv, rtype) = EmitValue(bin.Right);
        var rawL = IsScalar(ltype) ? lv : UnboxBool(lv);
        var rawR = IsScalar(rtype) ? rv : UnboxBool(rv);
        var op   = bin.Op == BinaryOp.And ? "and" : "or";
        var tmp  = NextTmp();
        _funcs.AppendLine($"  {tmp} = {op} i1 {rawL}, {rawR}");
        return (tmp, SuruType.Bool);
    }

    // Load from a local alloca or module-level constant global.
    private (string val, SuruType type) EmitLoad(string name)
    {
        if (!_vars.TryGetValue(name, out var entry))
        {
            var (gName, gType) = _globalVars[name];
            var rawTmp = NextTmp();
            _funcs.AppendLine($"  {rawTmp} = load {LlvmType(gType)}, ptr {gName}");
            return (rawTmp, gType);
        }
        var (ptr, type) = entry;
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = load {LlvmType(type)}, ptr {ptr}");
        return (tmp, type);
    }

    // ─── Method call dispatch ────────────────────────────────────────────────

    private (string val, SuruType type) EmitMethodCall(MethodCallExpression m)
    {
        // Namespace calls: `lib.fn(args)` where `lib` is an include alias.
        if (m.Receiver is VariableReferenceExpression { Name: var nsName } &&
            _module.Namespaces.Contains(nsName))
            return EmitUserFunctionCall(new CallExpression($"{nsName}.{m.MethodName}", m.Args));

        // Static methods on type names.
        if (m.Receiver is VariableReferenceExpression { Name: "Int32" })
            return EmitInt32StaticMethod(m.MethodName, m.Args);
        if (m.Receiver is VariableReferenceExpression { Name: "Int64" })
            return EmitInt64StaticMethod(m.MethodName, m.Args);

        var (recvVal, recvType) = EmitValue(m.Receiver);

        // Array instance methods — element type carried in ArrayType.Element.
        if (recvType is SuruType.ArrayType arrayType)
        {
            var isArgv = m.Receiver is VariableReferenceExpression { Name: var an } && _argvVars.Contains(an);
            if (isArgv)
                return m.MethodName switch
                {
                    "at"  => EmitArgAt(recvVal, m.Args[0]),
                    "len" => EmitArrayLen(recvVal),
                    _ => throw new NotSupportedException($"IR codegen: unsupported argv method '{m.MethodName}'"),
                };

            return m.MethodName switch
            {
                "len"   => EmitArrayLen(recvVal),
                "at"    => EmitArrayAt(recvVal, m.Args[0], arrayType.Element),
                "set"   => EmitArraySet(recvVal, m.Args[0], m.Args[1]),
                "add"   => EmitArrayAdd(recvVal, m.Args[0]),
                "slice" => EmitArraySlice(recvVal, m.Args[0], m.Args[1], arrayType),
                _ => throw new NotSupportedException($"IR codegen: unsupported Array method '{m.MethodName}'"),
            };
        }

        // NamedType with dynamic runtime kind: dispatch on type_tag for collection ops.
        if (recvType is SuruType.NamedType && m.MethodName is "at" or "len" or "set")
            return m.MethodName switch
            {
                "len" => EmitDynLen(recvVal),
                "at"  => EmitArrayAt(recvVal, m.Args[0]),
                "set" => EmitArraySet(recvVal, m.Args[0], m.Args[1]),
                _     => throw new NotSupportedException($"IR codegen: unreachable"),
            };

        // String instance methods.
        if (recvType is SuruType.StringType)
            return m.MethodName switch
            {
                "len"    => EmitStringLen(recvVal),
                "append" => EmitStringAppend(recvVal, m.Args[0]),
                "at"     => EmitStringAt(recvVal, m.Args[0]),
                "equals" => EmitStringEquals(recvVal, m.Args[0]),
                "slice"  => EmitStringSlice(recvVal, m.Args[0], m.Args[1]),
                "ord"    => EmitStringOrd(recvVal),
                _ => throw new NotSupportedException($"IR codegen: unsupported String method '{m.MethodName}'"),
            };

        if (m.MethodName == "toString" && recvType is SuruType.Int64Type)
            return EmitInt64ToString(recvVal);

        if (m.MethodName == "toString" && recvType is SuruType.NamedType)
            return EmitInt64ToString(UnboxInt64(recvVal));

        if (m.MethodName == "invert")
            return EmitInvert(recvVal, recvType);

        if (m.MethodName == "compare")
            return EmitCompare(recvVal, recvType, m.Args[0]);

        return m.MethodName switch
        {
            "add"      => EmitBinOp("add",  "fadd", recvVal, recvType, m.Args[0]),
            "take"     => EmitBinOp("sub",  "fsub", recvVal, recvType, m.Args[0]),
            "multiply" => EmitBinOp("mul",  "fmul", recvVal, recvType, m.Args[0]),
            "split"    => EmitBinOp("sdiv", "fdiv", recvVal, recvType, m.Args[0]),
            "lt"     => EmitCmp("icmp slt", "fcmp olt", recvVal, recvType, m.Args[0]),
            "gt"     => EmitCmp("icmp sgt", "fcmp ogt", recvVal, recvType, m.Args[0]),
            "lte"    => EmitCmp("icmp sle", "fcmp ole", recvVal, recvType, m.Args[0]),
            "gte"    => EmitCmp("icmp sge", "fcmp oge", recvVal, recvType, m.Args[0]),
            "equals" => EmitCmp("icmp eq",  "fcmp oeq", recvVal, recvType, m.Args[0]),
            _ => throw new NotSupportedException($"IR codegen: unsupported method '{m.MethodName}'"),
        };
    }

    private (string val, SuruType type) EmitInt32StaticMethod(
        string methodName, IReadOnlyList<Expression> args) => methodName switch
    {
        "from" => EmitInt32From(args[0]),
        _ => throw new NotSupportedException($"IR codegen: unknown Int32 static method '{methodName}'"),
    };

    // User-defined function call: scalar params/returns use raw LLVM types.
    private (string val, SuruType type) EmitUserFunctionCall(CallExpression call)
    {
        var (paramDefs, returnType) = _userFunctions[call.Name];
        var argParts = new List<string>(call.Args.Count);
        for (int i = 0; i < call.Args.Count; i++)
        {
            var pType = SuruTypeFromAnnotation(paramDefs[i].TypeAnnotation);
            var (argVal, argType) = EmitValue(call.Args[i]);
            var finalVal = IsScalar(pType) && !IsScalar(argType)
                ? UnboxScalar(argVal, pType)
                : argVal;
            argParts.Add($"{LlvmType(pType)} {finalVal}");
        }
        var retLlvmType = returnType is SuruType.VoidType ? "ptr" : LlvmType(returnType);
        var llvmName    = _module.ExternalFunctions.TryGetValue(call.Name, out var orig) ? orig : call.Name;
        var callTmp     = NextTmp();
        _funcs.AppendLine($"  {callTmp} = call {retLlvmType} @{llvmName}({string.Join(", ", argParts)})");
        return (callTmp, returnType);
    }

    private (string val, SuruType type) EmitInt32From(Expression arg)
    {
        if (arg is IntLiteral lit)
            return (lit.Value.ToString(CultureInfo.InvariantCulture), SuruType.Int32);
        var (val, vtype) = EmitValue(arg);
        var rawI64 = IsScalar(vtype) ? val : UnboxInt64(val);
        var i32tmp = NextTmp();
        _funcs.AppendLine($"  {i32tmp} = trunc i64 {rawI64} to i32");
        return (i32tmp, SuruType.Int32);
    }

    // Arithmetic on raw values. NamedType fields assumed to be Int64 at runtime.
    private (string val, SuruType type) EmitBinOp(
        string intOp, string floatOp,
        string lval, SuruType ltype, Expression argExpr)
    {
        var effectiveType = ltype is SuruType.NamedType ? SuruType.Int64 : ltype;
        var (rval, _) = EmitValue(argExpr);
        var rawL = IsScalar(ltype)         ? lval : UnboxScalar(lval, ltype);
        var rawR = IsScalar(effectiveType) ? rval : UnboxScalar(rval, effectiveType);
        var op   = effectiveType is SuruType.Float64Type ? floatOp : intOp;
        var result = NextTmp();
        _funcs.AppendLine($"  {result} = {op} {RawLlvmType(effectiveType)} {rawL}, {rawR}");
        return (result, effectiveType);
    }

    private (string val, SuruType type) EmitInvert(string recvVal, SuruType recvType)
    {
        var effectiveType = recvType is SuruType.NamedType ? SuruType.Int64 : recvType;
        var raw = IsScalar(recvType) ? recvVal : UnboxScalar(recvVal, recvType);
        var tmp = NextTmp();
        if (effectiveType is SuruType.Float64Type)
            _funcs.AppendLine($"  {tmp} = fneg double {raw}");
        else
            _funcs.AppendLine($"  {tmp} = sub {RawLlvmType(effectiveType)} 0, {raw}");
        return (tmp, effectiveType);
    }

    private (string val, SuruType type) EmitCmp(
        string intOp, string floatOp,
        string lval, SuruType ltype, Expression argExpr)
    {
        var effectiveType = ltype is SuruType.NamedType ? SuruType.Int64 : ltype;
        var (rval, _) = EmitValue(argExpr);
        var rawL = IsScalar(ltype)         ? lval : UnboxScalar(lval, ltype);
        var rawR = IsScalar(effectiveType) ? rval : UnboxScalar(rval, effectiveType);
        var op   = effectiveType is SuruType.Float64Type ? floatOp : intOp;
        var cmpTmp = NextTmp();
        _funcs.AppendLine($"  {cmpTmp} = {op} {RawLlvmType(effectiveType)} {rawL}, {rawR}");
        return (cmpTmp, SuruType.Bool);
    }

    private (string val, SuruType type) EmitCompare(
        string lval, SuruType ltype, Expression argExpr)
    {
        var effectiveType = ltype is SuruType.NamedType ? SuruType.Int64 : ltype;
        var (rval, _) = EmitValue(argExpr);
        var rawL  = IsScalar(ltype)         ? lval : UnboxScalar(lval, ltype);
        var rawR  = IsScalar(effectiveType) ? rval : UnboxScalar(rval, effectiveType);
        var rawT  = RawLlvmType(effectiveType);
        var gtTmp = NextTmp(); var ltTmp = NextTmp();
        var gtExt = NextTmp(); var ltExt = NextTmp();
        var result = NextTmp();
        if (effectiveType is SuruType.Float64Type)
        {
            _funcs.AppendLine($"  {gtTmp} = fcmp ogt {rawT} {rawL}, {rawR}");
            _funcs.AppendLine($"  {ltTmp} = fcmp olt {rawT} {rawL}, {rawR}");
        }
        else
        {
            _funcs.AppendLine($"  {gtTmp} = icmp sgt {rawT} {rawL}, {rawR}");
            _funcs.AppendLine($"  {ltTmp} = icmp slt {rawT} {rawL}, {rawR}");
        }
        _funcs.AppendLine($"  {gtExt} = zext i1 {gtTmp} to i64");
        _funcs.AppendLine($"  {ltExt} = zext i1 {ltTmp} to i64");
        _funcs.AppendLine($"  {result} = sub i64 {gtExt}, {ltExt}");
        return (result, SuruType.Int64);
    }
}
