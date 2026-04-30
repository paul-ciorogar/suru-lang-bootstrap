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
public sealed partial class IRCodeGenerator
{

    private readonly Module _module;
    private readonly string _sourceName;

    private readonly StringBuilder _funcs   = new();
    private readonly StringBuilder _helpers = new();
    private int _tmp;

    private readonly Externals _externals              = new();
    private readonly SuruRuntimeDeclarations _runtimeDecls = new();
    private BoolStirngGlobals _boolStringGlobals       = new();

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

    // ─── Function emission ───────────────────────────────────────────────────

    private void EmitFunction(FunctionDeclaration fn)
    {
        if (_module.ExternalFunctions.TryGetValue(fn.Name, out var originalName))
        {
            // External functions (from include): all params and return are ptr.
            var retLlvmType = fn.ReturnType.Name == "void" ? "void" : "ptr";
            var paramTypes  = string.Join(", ", fn.Parameters.Select(_ => "ptr"));
            _funcs.AppendLine($"declare {retLlvmType} @{originalName}({paramTypes})");
            return;
        }

        _blockOpen = true;
        _vars      = new();
        _argvVars  = new();

        if (fn.Name == "main")
        {
            _currentFnReturnLlvmType = "i64";
            _currentFnReturnSuruType = SuruType.Int64;
            _funcs.AppendLine("define internal i64 @suru_main(ptr %args) {");
            _funcs.AppendLine("entry:");
            _funcs.AppendLine("  %args.addr = alloca ptr");
            _funcs.AppendLine("  store ptr %args, ptr %args.addr");
            _vars["args"] = ("%args.addr", SuruType.Array);
            _argvVars.Add("args");
        }
        else
        {
            var retSuruType = FnReturnSuruType(fn);
            var retLlvmType = fn.ReturnType.Name == "void" ? "void" : "ptr";
            _currentFnReturnLlvmType = retLlvmType;
            _currentFnReturnSuruType = retSuruType;

            // All parameters are `ptr` in the universal tagged-pointer system.
            var paramStr = string.Join(", ", fn.Parameters.Select(p => $"ptr %{p.Name}"));
            _funcs.AppendLine($"define ptr @{fn.Name}({paramStr}) {{");
            _funcs.AppendLine("entry:");

            foreach (var p in fn.Parameters)
            {
                var pType    = SuruTypeFromAnnotation(p.TypeAnnotation);
                var allocPtr = $"%{p.Name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca ptr");
                _funcs.AppendLine($"  store ptr %{p.Name}, ptr {allocPtr}");
                _vars[p.Name] = (allocPtr, pType);
            }
        }

        foreach (var stmt in fn.Body)
            EmitStmt(stmt);

        if (_blockOpen)
        {
            if (fn.Name == "main")
                _funcs.AppendLine("  ret i64 0");
            else
                _funcs.AppendLine("  ret ptr null");
        }

        _funcs.AppendLine("}");
        _funcs.AppendLine();
    }

    // ─── Statement emission ──────────────────────────────────────────────────

    private void EmitStmt(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement { Expression: MatchExpression matchStmt }:
                EmitMatchAsStatement(matchStmt);
                break;

            // printLn(expr) — dispatch to suru_println which reads type_tag at offset 0.
            case ExpressionStatement { Expression: CallExpression { Name: "printLn", Args: [var arg] } }:
                var (pval, _) = EmitValue(arg);
                _runtimeDecls.AddSuruPrintln();
                _funcs.AppendLine($"  call void @suru_println(ptr {pval})");
                break;

            // printError(expr) — dispatch to suru_printerror (writes to stderr).
            case ExpressionStatement { Expression: CallExpression { Name: "printError", Args: [var errArg] } }:
                var (errVal, _) = EmitValue(errArg);
                _runtimeDecls.AddSuruPrintError();
                _funcs.AppendLine($"  call void @suru_printerror(ptr {errVal})");
                break;

            // exit(code) — unbox the Int64/Int32 Box ptr, trunc to i32, call @exit.
            case ExpressionStatement { Expression: CallExpression { Name: "exit", Args: [var codeExpr] } }:
                _externals.AddExit();
                var (codeVal, codeType) = EmitValue(codeExpr);
                string exitArg;
                if (codeType == SuruType.Int32)
                {
                    exitArg = UnboxInt32(codeVal);
                }
                else
                {
                    var rawI64 = UnboxInt64(codeVal);
                    var code32 = NextTmp();
                    _funcs.AppendLine($"  {code32} = trunc i64 {rawI64} to i32");
                    exitArg = code32;
                }
                _funcs.AppendLine($"  call void @exit(i32 {exitArg})");
                _funcs.AppendLine("  unreachable");
                _funcs.AppendLine($"dead_{_tmp}:");
                _blockOpen = true;
                break;

            case ExpressionStatement { Expression: CallExpression { Name: "writeFile", Args: [var wfPath, var wfContent] } }:
                EmitWriteFile(wfPath, wfContent);
                break;

            // let name TypeAnnotation: expr — every alloca is `ptr`.
            case LetStatement { Name: var name, Value: var valExpr, TypeAnnotation: var ann }:
                if (valExpr is FieldAccessExpression { ResolvedType: null } faLet)
                    faLet.ResolvedType = SuruTypeFromAnnotation(ann);
                var (letVal, letType) = EmitValue(valExpr);
                // Int32 annotation coerces an Int64 Box to an Int32 Box.
                if (ann.Name == "Int32" && letType == SuruType.Int64)
                {
                    if (valExpr is IntLiteral intLit)
                    {
                        letVal  = BoxInt32(intLit.Value.ToString(CultureInfo.InvariantCulture));
                        letType = SuruType.Int32;
                    }
                    else
                    {
                        var rawI = UnboxInt64(letVal);
                        var i32t = NextTmp();
                        _funcs.AppendLine($"  {i32t} = trunc i64 {rawI} to i32");
                        letVal  = BoxInt32(i32t);
                        letType = SuruType.Int32;
                    }
                }
                var allocPtr = $"%{name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca ptr");
                _funcs.AppendLine($"  store ptr {letVal}, ptr {allocPtr}");
                _vars[name] = (allocPtr, letType);
                break;

            // return expr — use _currentFnReturnLlvmType so `ret` matches the definition.
            case ReturnStatement { Value: var retExpr }:
                if (retExpr is FieldAccessExpression { ResolvedType: null } faRet)
                    faRet.ResolvedType = _currentFnReturnSuruType;
                var retVal = retExpr is null ? "null" : EmitValue(retExpr).Item1;
                _funcs.AppendLine($"  ret {_currentFnReturnLlvmType} {retVal}");
                _blockOpen = false;
                break;

            // while cond { body } — unbox condition to i1 before branching.
            case WhileStatement { Condition: var whileCond, Body: var whileBody }:
                var wn = _whileCounter++;
                _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_cond_{wn}:");
                var (condVal2, _) = EmitValue(whileCond);
                var condI1 = UnboxBool(condVal2);
                _funcs.AppendLine($"  br i1 {condI1}, label %while_body_{wn}, label %while_after_{wn}");
                _funcs.AppendLine($"while_body_{wn}:");
                foreach (var bodyStmt in whileBody)
                    EmitStmt(bodyStmt);
                if (_blockOpen)
                    _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_after_{wn}:");
                _blockOpen = true;
                break;

            // name: expr — store new value (always ptr) into existing alloca.
            case AssignmentStatement { Name: var assignName, Value: var assignExpr }:
                var (assignVal, _) = EmitValue(assignExpr);
                var (assignPtrAddr, _) = _vars[assignName];
                _funcs.AppendLine($"  store ptr {assignVal}, ptr {assignPtrAddr}");
                break;

            case FieldAssignmentStatement fieldAssign:
                EmitFieldAssignment(fieldAssign);
                break;

            case ExpressionStatement { Expression: var sideEffectExpr }:
                EmitValue(sideEffectExpr);
                break;

            default:
                throw new NotSupportedException($"IR codegen: unsupported statement {stmt.GetType().Name}");
        }
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
        StructLiteralExpression sl => EmitStructLiteral(sl),
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

    // ─── Match emission ───────────────────────────────────────────────────────

    private void EmitMatchAsStatement(MatchExpression match)
    {
        var (patternArms, wildcardArm, n) = EmitMatchTestChain(match);

        for (int i = 0; i < patternArms.Count; i++)
        {
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            EmitMatchArmBodyAsStatement(patternArms[i].Body);
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            EmitMatchArmBodyAsStatement(wildcardArm.Body);
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
    }

    private void EmitMatchArmBodyAsStatement(Expression body)
    {
        switch (body)
        {
            case CallExpression { Name: "printLn", Args: [var arg] }:
                var (v, _) = EmitValue(arg);
                _runtimeDecls.AddSuruPrintln();
                _funcs.AppendLine($"  call void @suru_println(ptr {v})");
                break;
            default:
                EmitValue(body);
                break;
        }
    }

    // Match-as-expression: alloca ptr (all values are ptr), store/load ptr.
    private (string val, SuruType type) EmitMatchAsExpression(MatchExpression match)
    {
        var firstArm   = match.Arms.FirstOrDefault(a => a.Pattern != null) ?? match.Arms[0];
        var resultType = PeekType(firstArm.Body);
        var resultPtr  = $"%match_result_{_matchCounter}";
        _funcs.AppendLine($"  {resultPtr} = alloca ptr");

        var (patternArms, wildcardArm, n) = EmitMatchTestChain(match);

        for (int i = 0; i < patternArms.Count; i++)
        {
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            var (armVal, _) = EmitValue(patternArms[i].Body);
            _funcs.AppendLine($"  store ptr {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            var (armVal, _) = EmitValue(wildcardArm.Body);
            _funcs.AppendLine($"  store ptr {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
        var loadTmp = NextTmp();
        _funcs.AppendLine($"  {loadTmp} = load ptr, ptr {resultPtr}");
        return (loadTmp, resultType);
    }

    private SuruType PeekType(Expression expr) => expr switch
    {
        BoolLiteral                   => SuruType.Bool,
        IntLiteral                    => SuruType.Int64,
        FloatLiteral                  => SuruType.Float64,
        StringLiteralExpression       => SuruType.String,
        ArrayLiteralExpression        => SuruType.Array,
        StructLiteralExpression       => SuruType.Struct,
        FieldAccessExpression fa      => fa.ResolvedType ?? SuruType.Struct,
        CallExpression { Name: "clone", Args: [var carg] } => PeekType(carg),
        UnaryExpression               => SuruType.Bool,
        BinaryExpression              => SuruType.Bool,
        VariableReferenceExpression v =>
            _vars.TryGetValue(v.Name, out var ve) ? ve.type : _globalVars[v.Name].Type,
        MatchExpression match         => PeekMatchType(match),
        MethodCallExpression m        => PeekMethodType(m),
        CallExpression { Name: "readFile" } => SuruType.String,
        CallExpression c when _userFunctions.ContainsKey(c.Name)
                                      => _userFunctions[c.Name].ReturnType,
        _ => throw new NotSupportedException($"IR codegen: cannot peek type of {expr.GetType().Name}"),
    };

    private SuruType PeekMatchType(MatchExpression match)
    {
        var first = match.Arms.FirstOrDefault(a => a.Pattern != null) ?? match.Arms[0];
        return PeekType(first.Body);
    }

    private SuruType PeekMethodType(MethodCallExpression m)
    {
        if (m.Receiver is VariableReferenceExpression { Name: var nsName2 } &&
            _module.Namespaces.Contains(nsName2))
            return _userFunctions[$"{nsName2}.{m.MethodName}"].ReturnType;

        if (m.Receiver is VariableReferenceExpression { Name: var typeName }
            && typeName is "Int32" or "Int64" or "Float64" or "Bool" or "String")
            return SuruTypeFromAnnotation(new TypeAnnotation(typeName));

        // Array methods: no element type tracking; at() returns Struct (generic ptr).
        if (m.Receiver is VariableReferenceExpression rv2 &&
            _vars.TryGetValue(rv2.Name, out var rv2Entry) && rv2Entry.type == SuruType.Array)
        {
            return m.MethodName switch
            {
                "len"   => SuruType.Int64,
                "at"    => SuruType.Struct,   // element type unknown at compile time
                "set"   => SuruType.Bool,
                "add"   => SuruType.Bool,
                "slice" => SuruType.Array,
                _ => throw new NotSupportedException($"IR codegen: cannot peek type for Array.{m.MethodName}"),
            };
        }

        return m.MethodName switch
        {
            "add" or "take" or "multiply" or "split" or "invert" => PeekType(m.Receiver),
            "lt" or "gt" or "lte" or "gte" or "equals"           => SuruType.Bool,
            "compare" or "ord" or "len"                           => SuruType.Int64,
            "toString" or "at" or "append" or "slice"             => SuruType.String,
            _ => throw new NotSupportedException($"IR codegen: cannot peek type for method '{m.MethodName}'"),
        };
    }

    // Match test chain: unbox scalar condition and patterns before icmp/fcmp.
    private (List<MatchArm> PatternArms, MatchArm? WildcardArm, int N) EmitMatchTestChain(
        MatchExpression match)
    {
        var (condVal, condType) = EmitValue(match.Condition);
        int n = _matchCounter++;

        var patternArms = match.Arms.Where(a => a.Pattern != null).ToList();
        var wildcardArm = match.Arms.FirstOrDefault(a => a.Pattern == null);
        var missLabel   = wildcardArm != null ? $"match_wildcard_{n}" : $"match_merge_{n}";

        // Unbox scalar condition once (String stays as ptr for strcmp dispatch).
        string rawCond;
        if (condType == SuruType.String)
            rawCond = condVal;   // stays as String ptr
        else
            rawCond = UnboxScalar(condVal, condType);

        for (int i = 0; i < patternArms.Count; i++)
        {
            var cmpTmp = NextTmp();

            if (condType == SuruType.String)
            {
                _externals.AddStrcmp();
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                var condData    = EmitExtractStringData(rawCond);
                var patternData = EmitExtractStringData(patternVal);
                var strcmpTmp   = NextTmp();
                _funcs.AppendLine($"  {strcmpTmp} = call i32 @strcmp(ptr {condData}, ptr {patternData})");
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i32 {strcmpTmp}, 0");
            }
            else if (condType == SuruType.Float64)
            {
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                var rawPat = UnboxScalar(patternVal, condType);
                _funcs.AppendLine($"  {cmpTmp} = fcmp oeq double {rawCond}, {rawPat}");
            }
            else
            {
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                var rawPat = UnboxScalar(patternVal, condType);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq {RawLlvmType(condType)} {rawCond}, {rawPat}");
            }

            var nextLabel = (i + 1 < patternArms.Count)
                ? $"match_test_{n}_{i + 1}"
                : missLabel;

            _funcs.AppendLine($"  br i1 {cmpTmp}, label %match_arm_{n}_{i}, label %{nextLabel}");

            if (i + 1 < patternArms.Count)
                _funcs.AppendLine($"match_test_{n}_{i + 1}:");
        }

        if (patternArms.Count == 0)
            _funcs.AppendLine($"  br label %{missLabel}");

        return (patternArms, wildcardArm, n);
    }

    // ─── @main wrapper ───────────────────────────────────────────────────────

    // Emits a C-ABI `int main(int argc, char** argv)` that builds a %suru.String
    // wrapping argv (type_tag=6 since it uses the String header layout), then calls
    // suru_main and returns its exit code.
    private void EmitMainWrapper(StringBuilder sb)
    {
        sb.AppendLine("define i32 @main(i32 %argc, ptr %argv) {");
        sb.AppendLine("entry:");
        sb.AppendLine("  %seq      = call ptr @malloc(i64 24)");
        sb.AppendLine("  %tag_gep  = getelementptr %suru.String, ptr %seq, i32 0, i32 0");
        sb.AppendLine("  store i64 6, ptr %tag_gep");
        sb.AppendLine("  %len_gep  = getelementptr %suru.String, ptr %seq, i32 0, i32 1");
        sb.AppendLine("  %argc64   = sext i32 %argc to i64");
        sb.AppendLine("  store i64 %argc64, ptr %len_gep");
        sb.AppendLine("  %data_gep = getelementptr %suru.String, ptr %seq, i32 0, i32 2");
        sb.AppendLine("  store ptr %argv, ptr %data_gep");
        sb.AppendLine("  %suru_ret = call i64 @suru_main(ptr %seq)");
        sb.AppendLine("  %ret32    = trunc i64 %suru_ret to i32");
        sb.AppendLine("  ret i32 %ret32");
        sb.AppendLine("}");
    }

    // ─── Array.at for argv ───────────────────────────────────────────────────

    // args.at(i) — extract the i-th element from the argv Seq built by @main.
    // The index is a Box(Int64) ptr; unbox before GEP.
    private (string val, SuruType type) EmitArgAt(string seqVal, Expression idxExpr)
    {
        var data     = EmitExtractStringData(seqVal);   // char**
        var (idxBox, _) = EmitValue(idxExpr);
        var idx      = UnboxInt64(idxBox);              // unbox Box(Int64) → i64

        var slotPtr = NextTmp();
        var cstrPtr = NextTmp();
        _funcs.AppendLine($"  {slotPtr} = getelementptr ptr, ptr {data}, i64 {idx}");
        _funcs.AppendLine($"  {cstrPtr} = load ptr, ptr {slotPtr}");

        _externals.AddStrlen();
        var len = NextTmp();
        _funcs.AppendLine($"  {len} = call i64 @strlen(ptr {cstrPtr})");

        return EmitCreateStringSeq(cstrPtr, len);
    }

    // ─── File I/O built-ins ──────────────────────────────────────────────────

    private (string val, SuruType type) EmitReadFile(Expression pathArg)
    {
        var (pathSeq, _) = EmitValue(pathArg);
        var pathData = EmitExtractStringData(pathSeq);

        _externals.AddFopen();
        _boolStringGlobals.AddModeR();
        var file = NextTmp();
        _funcs.AppendLine($"  {file} = call ptr @fopen(ptr {pathData}, ptr @.mode_r)");

        _externals.AddFseek();
        _funcs.AppendLine($"  call i32 @fseek(ptr {file}, i64 0, i32 2)");

        _externals.AddFtell();
        var size = NextTmp();
        _funcs.AppendLine($"  {size} = call i64 @ftell(ptr {file})");

        _externals.AddRewind();
        _funcs.AppendLine($"  call void @rewind(ptr {file})");

        var bufSize = NextTmp();
        var buf     = NextTmp();
        _funcs.AppendLine($"  {bufSize} = add i64 {size}, 1");
        _funcs.AppendLine($"  {buf} = call ptr @malloc(i64 {bufSize})");

        _externals.AddFread();
        _funcs.AppendLine($"  call i64 @fread(ptr {buf}, i64 1, i64 {size}, ptr {file})");

        var nullSlot = NextTmp();
        _funcs.AppendLine($"  {nullSlot} = getelementptr i8, ptr {buf}, i64 {size}");
        _funcs.AppendLine($"  store i8 0, ptr {nullSlot}");

        _externals.AddFclose();
        _funcs.AppendLine($"  call i32 @fclose(ptr {file})");

        return EmitCreateStringSeq(buf, size);
    }

    private void EmitWriteFile(Expression pathArg, Expression contentArg)
    {
        var (pathSeq, _)    = EmitValue(pathArg);
        var pathData        = EmitExtractStringData(pathSeq);
        var (contentSeq, _) = EmitValue(contentArg);
        var contentLen      = EmitExtractStringLen(contentSeq);
        var contentData     = EmitExtractStringData(contentSeq);

        _externals.AddFopen();
        _boolStringGlobals.AddModeW();
        var file = NextTmp();
        _funcs.AppendLine($"  {file} = call ptr @fopen(ptr {pathData}, ptr @.mode_w)");

        _externals.AddFwrite();
        _funcs.AppendLine($"  call i64 @fwrite(ptr {contentData}, i64 1, i64 {contentLen}, ptr {file})");

        _externals.AddFclose();
        _funcs.AppendLine($"  call i32 @fclose(ptr {file})");
    }

    // ─── Type utilities ───────────────────────────────────────────────────────

    private static SuruType SuruTypeFromAnnotation(TypeAnnotation ann) => ann.Name switch
    {
        "Bool"    => SuruType.Bool,
        "Int32"   => SuruType.Int32,
        "Int64"   => SuruType.Int64,
        "Float64" => SuruType.Float64,
        "String"  => SuruType.String,
        "Array"   => SuruType.Array,
        "Struct"  => SuruType.Struct,
        _ => throw new NotSupportedException($"IR codegen: unsupported type annotation '{ann}'"),
    };

    // Every Suru value in user .ll is a `ptr` (Box for scalars, direct heap ptr for rest).
    private static string LlvmType(SuruType _) => "ptr";

    // Raw LLVM type for scalar operations (box/unbox calls, global constant loads, arithmetic).
    private static string RawLlvmType(SuruType type) => type switch
    {
        SuruType.Bool    => "i1",
        SuruType.Int32   => "i32",
        SuruType.Int64   => "i64",
        SuruType.Float64 => "double",
        SuruType.Struct  => "i64",   // unknown struct fields unbox as i64 (see UnboxScalar)
        _                => "ptr",
    };

    private static SuruType FnReturnSuruType(FunctionDeclaration fn)
        => fn.ReturnType.Name is "void" ? SuruType.Int64 : SuruTypeFromAnnotation(fn.ReturnType);

    // ─── Box / Unbox helpers ──────────────────────────────────────────────────

    private string BoxBool(string i1val)
    {
        _runtimeDecls.AddBoxBool();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_bool(i1 {i1val})");
        return tmp;
    }

    private string BoxInt32(string i32val)
    {
        _runtimeDecls.AddBoxInt32();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_int32(i32 {i32val})");
        return tmp;
    }

    private string BoxInt64(string i64val)
    {
        _runtimeDecls.AddBoxInt64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_int64(i64 {i64val})");
        return tmp;
    }

    private string BoxFloat64(string doubleval)
    {
        _runtimeDecls.AddBoxFloat64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_float64(double {doubleval})");
        return tmp;
    }

    private string BoxValue(string rawVal, SuruType type) => type switch
    {
        SuruType.Bool    => BoxBool(rawVal),
        SuruType.Int32   => BoxInt32(rawVal),
        SuruType.Int64   => BoxInt64(rawVal),
        SuruType.Float64 => BoxFloat64(rawVal),
        _                => rawVal,   // String/Array/Struct already carry type_tag
    };

    private string UnboxBool(string ptrval)
    {
        _runtimeDecls.AddUnboxBool();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i1 @suru_unbox_bool(ptr {ptrval})");
        return tmp;
    }

    private string UnboxInt32(string ptrval)
    {
        _runtimeDecls.AddUnboxInt32();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i32 @suru_unbox_int32(ptr {ptrval})");
        return tmp;
    }

    private string UnboxInt64(string ptrval)
    {
        _runtimeDecls.AddUnboxInt64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i64 @suru_unbox_int64(ptr {ptrval})");
        return tmp;
    }

    private string UnboxFloat64(string ptrval)
    {
        _runtimeDecls.AddUnboxFloat64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call double @suru_unbox_float64(ptr {ptrval})");
        return tmp;
    }

    // Unbox a scalar value to its raw LLVM type for arithmetic/comparison.
    // For Struct-typed values (dynamic unknown type), assume Int64 at runtime.
    private string UnboxScalar(string ptrval, SuruType type) => type switch
    {
        SuruType.Bool    => UnboxBool(ptrval),
        SuruType.Int32   => UnboxInt32(ptrval),
        SuruType.Int64   => UnboxInt64(ptrval),
        SuruType.Float64 => UnboxFloat64(ptrval),
        SuruType.Struct  => UnboxInt64(ptrval),   // assume Box(Int64) at runtime
        _                => throw new NotSupportedException($"IR codegen: cannot unbox {type}"),
    };

    // ─── String utilities ─────────────────────────────────────────────────────

    // Escape a C# string (post-Suru-lexer, fully unescaped) for use as an LLVM IR
    // string constant.  LLVM's only escape form is \XX (hex), so we hex-escape every
    // non-printable byte and the two special chars (`"` and `\`).  The Suru lexer has
    // already converted escape sequences (e.g. `\n` in source → 0x0A in memory), so we
    // must NOT re-interpret `\n` here — a backslash followed by `n` is two literal bytes.
    private static string EscapeStringForIR(string source)
    {
        var sb = new StringBuilder();
        foreach (char c in source)
        {
            if (c >= 32 && c < 127 && c != '"' && c != '\\')
                sb.Append(c);
            else
                sb.Append($"\\{(int)c:X2}");
        }
        return sb.ToString();
    }

    // Each C# char maps to one byte (Suru strings are ASCII).
    // The Suru lexer has already unescaped all escape sequences, so there are no
    // multi-char escape tokens here — each char in the C# string is a real byte.
    private static int CountStringBytes(string source) => source.Length;

    private string NextTmp() => $"%t{_tmp++}";
}
