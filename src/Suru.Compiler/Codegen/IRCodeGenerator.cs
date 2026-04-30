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
// Universal tagged-pointer value system:
//   Every Suru value at runtime is a `ptr` to a heap-allocated value whose FIRST i64
//   field is always the type_tag (0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String).
//   Scalars (Bool/Int32/Int64/Float64) are wrapped in a %suru.Box = { i64 type_tag, i64 payload }.
//   String/Array/Struct already carry type_tag at offset 0 in their own headers.
//   This means `load i64, ptr %anyVal` always gives the type_tag, enabling suru_println/
//   suru_array_clone_dyn / suru_array_drop_dyn to dispatch without compile-time metadata.
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

    // Per-function variable table: name → (alloca ptr SSA name, SuruType). Every alloca is `ptr`.
    private Dictionary<string, (string ptr, SuruType type)> _vars = new();

    private bool _blockOpen;
    private int  _matchCounter;
    private int  _whileCounter;

    private readonly Dictionary<string, (IReadOnlyList<FunctionParameter> Params, SuruType ReturnType)>
        _userFunctions = new();

    // LLVM return type of the function currently being emitted.
    // suru_main is always "i64"; all other non-void functions are "ptr".
    private string   _currentFnReturnLlvmType = "i64";
    private SuruType _currentFnReturnSuruType = SuruType.Int64;
    // Suru type annotation name of the current function's return type (e.g. "Point", "Struct", "void").
    // Used to thread the declared type into EmitStructLiteral for return-statement struct literals.
    private string?  _currentFnReturnTypeName;

    // Module-level constant globals: name → (LLVM global name, SuruType).
    private readonly Dictionary<string, (string GlobalName, SuruType Type)> _globalVars = new();

    // Tracks which Array variables are the argv Seq (built by @main from char**).
    // argv access goes through EmitArgAt (GEP into char**), not suru_array_at.
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
                    BoolLiteral   => SuruType.Bool,
                    IntLiteral    => SuruType.Int64,
                    FloatLiteral  => SuruType.Float64,
                    _             => throw new InvalidOperationException("unreachable"),
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
        // Scalar literals are boxed immediately — every value is a `ptr`.
        BoolLiteral b              => (BoxBool(b.Value ? "1" : "0"), SuruType.Bool),
        IntLiteral i               => (BoxInt64(i.Value.ToString(CultureInfo.InvariantCulture)), SuruType.Int64),
        FloatLiteral f             => (BoxFloat64($"0x{BitConverter.DoubleToInt64Bits(f.Value):X16}"), SuruType.Float64),
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
        var (val, _) = EmitValue(arg);
        _runtimeDecls.AddCloneDyn();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_clone_dyn(ptr {val})");
        return (tmp, SuruType.Struct);
    }

    private (string val, SuruType type) EmitDropDyn(Expression arg)
    {
        var (val, _) = EmitValue(arg);
        _runtimeDecls.AddDropDyn();
        _funcs.AppendLine($"  call void @suru_drop_dyn(ptr {val})");
        return ("null", SuruType.Bool);
    }

    // Bool NOT: unbox → xor i1 → rebox.
    private (string val, SuruType type) EmitBoolNot(Expression operand)
    {
        var (v, _) = EmitValue(operand);
        var raw    = UnboxBool(v);
        var tmp    = NextTmp();
        _funcs.AppendLine($"  {tmp} = xor i1 {raw}, true");
        return (BoxBool(tmp), SuruType.Bool);
    }

    // and/or: unbox both to i1 → op → rebox.
    private (string val, SuruType type) EmitBinaryExpr(BinaryExpression bin)
    {
        var (lv, _) = EmitValue(bin.Left);
        var (rv, _) = EmitValue(bin.Right);
        var rawL = UnboxBool(lv);
        var rawR = UnboxBool(rv);
        var op   = bin.Op == BinaryOp.And ? "and" : "or";
        var tmp  = NextTmp();
        _funcs.AppendLine($"  {tmp} = {op} i1 {rawL}, {rawR}");
        return (BoxBool(tmp), SuruType.Bool);
    }

    // Load from a local alloca (always `ptr`) or from a raw global constant (box after load).
    private (string val, SuruType type) EmitLoad(string name)
    {
        if (!_vars.TryGetValue(name, out var entry))
        {
            // Module-level constant: stored as raw LLVM type (i1/i64/double), box on load.
            var (gName, gType) = _globalVars[name];
            var rawTmp = NextTmp();
            _funcs.AppendLine($"  {rawTmp} = load {RawLlvmType(gType)}, ptr {gName}");
            var boxed = BoxValue(rawTmp, gType);
            return (boxed, gType);
        }
        var (ptr, type) = entry;
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = load ptr, ptr {ptr}");
        return (tmp, type);
    }

    // ─── Method call dispatch ────────────────────────────────────────────────

    private (string val, SuruType type) EmitMethodCall(MethodCallExpression m)
    {
        // Namespace calls: `lib.fn(args)` where `lib` is an include alias.
        if (m.Receiver is VariableReferenceExpression { Name: var nsName } &&
            _module.Namespaces.Contains(nsName))
        {
            var qualifiedName = $"{nsName}.{m.MethodName}";
            return EmitUserFunctionCall(new CallExpression(qualifiedName, m.Args));
        }

        // Static methods on type names.
        if (m.Receiver is VariableReferenceExpression { Name: "Int32" })
            return EmitInt32StaticMethod(m.MethodName, m.Args);
        if (m.Receiver is VariableReferenceExpression { Name: "Int64" })
            return EmitInt64StaticMethod(m.MethodName, m.Args);

        var (recvVal, recvType) = EmitValue(m.Receiver);

        // Array instance methods.
        // argv Seq (args.at(i)) is detected via _argvVars and routes to EmitArgAt.
        if (recvType == SuruType.Array)
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
                "at"    => EmitArrayAt(recvVal, m.Args[0]),
                "set"   => EmitArraySet(recvVal, m.Args[0], m.Args[1]),
                "add"   => EmitArrayAdd(recvVal, m.Args[0]),
                "slice" => EmitArraySlice(recvVal, m.Args[0], m.Args[1]),
                _ => throw new NotSupportedException($"IR codegen: unsupported Array method '{m.MethodName}'"),
            };
        }

        // Struct-typed values with unknown runtime kind: dispatch on type_tag.
        // .len() uses suru_dyn_len which branches on tag=5 (Array) vs tag=6 (String).
        // .at/.set are unambiguously array operations (no string analogue at this level).
        if (recvType == SuruType.Struct && m.MethodName is "at" or "len" or "set")
            return m.MethodName switch
            {
                "len" => EmitDynLen(recvVal),
                "at"  => EmitArrayAt(recvVal, m.Args[0]),
                "set" => EmitArraySet(recvVal, m.Args[0], m.Args[1]),
                _     => throw new NotSupportedException($"IR codegen: unreachable"),
            };

        // String instance methods.
        if (recvType == SuruType.String)
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

        // toString() on Int64 Box: unbox → call suru_int64_to_string.
        if (m.MethodName == "toString" && recvType == SuruType.Int64)
        {
            var rawI64 = UnboxInt64(recvVal);
            return EmitInt64ToString(rawI64);
        }

        // toString() on unknown Struct-typed value: assume Box(Int64) at runtime.
        if (m.MethodName == "toString" && recvType == SuruType.Struct)
        {
            var rawI64 = UnboxInt64(recvVal);
            return EmitInt64ToString(rawI64);
        }

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

    // User-defined function call: all args are `ptr`, return is `ptr`.
    private (string val, SuruType type) EmitUserFunctionCall(CallExpression call)
    {
        var (paramDefs, returnType) = _userFunctions[call.Name];
        var argParts = new List<string>(call.Args.Count);
        for (int i = 0; i < call.Args.Count; i++)
        {
            var (argVal, _) = EmitValue(call.Args[i]);
            argParts.Add($"ptr {argVal}");
        }
        var llvmName = _module.ExternalFunctions.TryGetValue(call.Name, out var orig) ? orig : call.Name;
        var callTmp  = NextTmp();
        _funcs.AppendLine($"  {callTmp} = call ptr @{llvmName}({string.Join(", ", argParts)})");
        return (callTmp, returnType);
    }

    // Int32.from(expr): unbox Int64 → trunc → rebox Int32.
    private (string val, SuruType type) EmitInt32From(Expression arg)
    {
        if (arg is IntLiteral lit)
            return (BoxInt32(lit.Value.ToString(CultureInfo.InvariantCulture)), SuruType.Int32);

        var (val, _) = EmitValue(arg);
        var rawI64   = UnboxInt64(val);
        var i32tmp   = NextTmp();
        _funcs.AppendLine($"  {i32tmp} = trunc i64 {rawI64} to i32");
        return (BoxInt32(i32tmp), SuruType.Int32);
    }

    // Arithmetic: unbox → op → rebox same type.
    // When ltype is Struct (unknown field type assumed to be Int64), treat as Int64.
    private (string val, SuruType type) EmitBinOp(
        string intOp, string floatOp,
        string lval, SuruType ltype, Expression argExpr)
    {
        var effectiveType = ltype == SuruType.Struct ? SuruType.Int64 : ltype;
        var (rval, _) = EmitValue(argExpr);
        var rawL   = UnboxScalar(lval, ltype);
        var rawR   = UnboxScalar(rval, ltype);
        var op     = effectiveType == SuruType.Float64 ? floatOp : intOp;
        var result = NextTmp();
        _funcs.AppendLine($"  {result} = {op} {RawLlvmType(effectiveType)} {rawL}, {rawR}");
        return (BoxValue(result, effectiveType), effectiveType);
    }

    // Unary negation: unbox → negate → rebox.
    // When recvType is Struct (unknown field type assumed to be Int64), treat as Int64.
    private (string val, SuruType type) EmitInvert(string recvVal, SuruType recvType)
    {
        var effectiveType = recvType == SuruType.Struct ? SuruType.Int64 : recvType;
        var raw = UnboxScalar(recvVal, recvType);
        var tmp = NextTmp();
        if (effectiveType == SuruType.Float64)
            _funcs.AppendLine($"  {tmp} = fneg double {raw}");
        else
            _funcs.AppendLine($"  {tmp} = sub {RawLlvmType(effectiveType)} 0, {raw}");
        return (BoxValue(tmp, effectiveType), effectiveType);
    }

    // Comparison: unbox → icmp/fcmp → rebox Bool.
    private (string val, SuruType type) EmitCmp(
        string intOp, string floatOp,
        string lval, SuruType ltype, Expression argExpr)
    {
        var (rval, _) = EmitValue(argExpr);
        var rawL   = UnboxScalar(lval, ltype);
        var rawR   = UnboxScalar(rval, ltype);
        var op     = ltype == SuruType.Float64 ? floatOp : intOp;
        var cmpTmp = NextTmp();
        _funcs.AppendLine($"  {cmpTmp} = {op} {RawLlvmType(ltype)} {rawL}, {rawR}");
        return (BoxBool(cmpTmp), SuruType.Bool);
    }

    // compare(): returns Box(Int64) with -1/0/1.
    private (string val, SuruType type) EmitCompare(
        string lval, SuruType ltype, Expression argExpr)
    {
        var (rval, _) = EmitValue(argExpr);
        var rawL   = UnboxScalar(lval, ltype);
        var rawR   = UnboxScalar(rval, ltype);
        var rawT   = RawLlvmType(ltype);
        var gtTmp  = NextTmp();
        var ltTmp  = NextTmp();
        var gtExt  = NextTmp();
        var ltExt  = NextTmp();
        var result = NextTmp();
        if (ltype == SuruType.Float64)
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
        return (BoxInt64(result), SuruType.Int64);
    }
}
