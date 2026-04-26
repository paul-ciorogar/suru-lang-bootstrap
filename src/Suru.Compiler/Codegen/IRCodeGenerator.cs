// Covered fixtures: print, exit_test, print-error, negative-literals, arithmetic, comparisons,
//                   control-flow, fibonacci, while-loop, strings, include-test, file_io, file_io_write, arrays, structs,
//                   suru-lexer
using System.Globalization;
using System.Text;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Emits LLVM IR text (.ll) as a direct replacement for the LLVMSharp-based CodeGenerator.
// The output is fed to `clang` rather than the LLVM C API, which removes the native binding
// dependency and makes the generated IR human-readable for debugging.
//
// Structure: Emit() drives two passes over the module:
//   Pass 1 — EmitFunction() calls populate _funcs and set the "need" flags.
//   Pass 2 — Emit() assembles the final .ll file: module header, globals, extern
//             declarations (driven by _externals), then the buffered function bodies,
//             and finally the fixed @main wrapper.
public sealed partial class IRCodeGenerator
{

    private readonly Module _module;
    private readonly string _sourceName;

    // Accumulates LLVM IR for all function bodies during pass 1.
    private readonly StringBuilder _funcs = new();

    // Accumulates module-level helper functions (e.g. suru_find_field) that must
    // appear before user functions in the final .ll output. These are emitted lazily
    // the first time they are needed and must not be written to _funcs (which is
    // open inside a function body at the point of first use).
    private readonly StringBuilder _helpers = new();

    // Monotonically increasing counter for SSA temporaries (%t0, %t1, …).
    private int _tmp;

    // Every C runtime symbol used by emitted code is listed here.
    // _externals is populated lazily; Emit() emits only what is needed.
    private readonly Externals _externals = new();

    // Format-string and bool-string globals — emitted only if referenced.
    private BoolStirngGlobals _boolStringGlobals = new();

    // String literal globals: maps source-text → (LLVM global name, byte length with null).
    // Deduplicates identical literals across the whole module.
    private readonly Dictionary<string, (string name, int byteLen)> _stringLiterals = new();
    private int _strCount;

    // Per-function variable table: Suru name → (alloca ptr SSA name, SuruType).
    // Reset at the start of each function so names don't leak across functions.
    private Dictionary<string, (string ptr, SuruType type)> _vars = new();

    // Whether the basic block currently being emitted still needs a terminator.
    // False after `ret`; true after `exit` (which opens a dead block to absorb
    // any statements the parser placed after the exit call).
    private bool _blockOpen;

    // Monotonically increasing counter for unique match label names (match_arm_N_M,
    // match_test_N_M, match_wildcard_N, match_merge_N). Incremented per match site.
    private int _matchCounter;

    // Monotonically increasing counter for unique while-loop label names
    // (while_cond_N, while_body_N, while_after_N). Incremented per while site.
    private int _whileCounter;

    // Registered non-main function signatures: name → (params, Suru return type).
    // Populated by a pre-pass before body emission so EmitValue can resolve
    // user-defined call sites — including recursive self-calls — without needing
    // LLVM forward declarations (IR definitions are visible module-wide regardless
    // of textual order).
    private readonly Dictionary<string, (IReadOnlyList<FunctionParameter> Params, SuruType ReturnType)>
        _userFunctions = new();

    // LLVM return type of the function currently being emitted (e.g. "i64" or "ptr").
    // Set at the start of EmitFunction and used by the ReturnStatement emitter so that
    // `ret <type>` matches the function's definition regardless of return type.
    private string _currentFnReturnLlvmType = "i64";

    // Element type of each array variable: name → SuruType.
    // Populated by LetStatement when the RHS produces an Array (literal or slice).
    // Reset at the start of each function alongside _vars.
    private Dictionary<string, SuruType> _arrayElementTypes = new();

    // Pending element type emitted by EmitArrayLiteral / EmitArraySlice.
    // Consumed by the LetStatement handler to record the element type without
    // having to re-inspect the already-emitted expression.
    private SuruType? _pendingArrayElemType;

    // Module-level constant globals: Suru name → (LLVM global name "@name", SuruType).
    // Populated in pass 0 by EmitGlobalConstants(); used by EmitLoad and PeekType as a
    // fallback when the name is absent from the per-function _vars table.
    private readonly Dictionary<string, (string GlobalName, SuruType Type)> _globalVars = new();

    // Whether the suru_find_field helper function has been emitted into _funcs.
    // Guarded so it is emitted at most once per module regardless of how many
    // struct literals appear. Set true by EnsureFindFieldHelper().
    private bool _findFieldEmitted = false;

    // Monotonically increasing counter for clone/drop loop label uniqueness
    // (clone_cond_N, drop_cond_N, etc). Incremented at the start of each
    // EmitCloneStruct / EmitDropStruct call.
    private int _structCounter = 0;

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
        // @main always calls @malloc to build the argv Seq.
        _externals.AddMalloc();

        // Pass 0: record module-level constants (Bool/Int64/Float64 literals declared
        // with `let` at the top level). Populates _globalVars so EmitLoad and PeekType
        // can resolve references to these constants from inside function bodies.
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

        // Pre-pass: register all non-main function signatures before emitting any
        // body. This lets EmitValue resolve user-defined call sites (including
        // recursive self-calls) without requiring LLVM forward declarations —
        // LLVM IR definitions are module-wide regardless of textual order.
        foreach (var stmt in _module.Statements)
            if (stmt is FunctionDeclaration { Name: not "main" } fn)
                _userFunctions[fn.Name] = (fn.Parameters, FnReturnSuruType(fn));

        // Pass 1: emit all function bodies into _funcs (and set flags).
        foreach (var stmt in _module.Statements)
            if (stmt is FunctionDeclaration fn)
                EmitFunction(fn);

        // Pass 2: assemble the .ll file in the required declaration-before-use order.
        var sb = new StringBuilder();
        sb.AppendLine($"; ModuleID = '{_sourceName}'");
        sb.AppendLine($"source_filename = \"{_sourceName}\"");
        sb.AppendLine();
        sb.AppendLine("%suru.Seq   = type { i64, ptr }");
        sb.AppendLine("%suru.Field = type { ptr, i32, i64, ptr }");
        sb.AppendLine();

        // Module-level constant globals (from pass 0)
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

        // Format / bool-string globals
        sb.Append(_boolStringGlobals.ToString());

        // String literal globals — one per unique source-text value
        foreach (var (text, (name, byteLen)) in _stringLiterals)
        {
            var escaped = EscapeStringForIR(text);
            sb.AppendLine($"{name} = private unnamed_addr constant [{byteLen} x i8] c\"{escaped}\\00\"");
        }
        sb.AppendLine();

        // External symbol declarations — only what the module actually uses
        sb.Append(_externals.ToString());
        sb.AppendLine();

        sb.Append(_helpers);
        sb.Append(_funcs);
        EmitMainWrapper(sb);

        return sb.ToString();
    }

    // ─── Function emission ───────────────────────────────────────────────────

    private void EmitFunction(FunctionDeclaration fn)
    {
        // Reset per-function state before emitting the body.
        _blockOpen = true;
        _vars = new();
        _arrayElementTypes = new();

        if (fn.Name == "main")
        {
            // The user-defined `main` is compiled as `suru_main` (internal) so the
            // fixed C-ABI @main wrapper can call it with the already-built argv Seq.
            // The argv Seq is passed as a raw `ptr` — no typed parameter needed here.
            _currentFnReturnLlvmType = "i64";
            _funcs.AppendLine("define internal i64 @suru_main(ptr %args) {");
            _funcs.AppendLine("entry:");

            // Register `args` so the body can load it via the standard EmitLoad path.
            // The Seq was built by @main: { len=argc, data=argv (char**) }.
            // args.at(i) GEPs into the char** data field — see EmitArgAt.
            _funcs.AppendLine("  %args.addr = alloca ptr");
            _funcs.AppendLine("  store ptr %args, ptr %args.addr");
            _vars["args"] = ("%args.addr", SuruType.Array);
        }
        else
        {
            // Non-main functions: build the LLVM parameter list and return type from
            // the declared Suru types. The LLVM return type must match the actual type
            // (e.g. `ptr` for String-returning functions, `i64` for Int64).
            var retLlvmType = LlvmType(FnReturnSuruType(fn));
            _currentFnReturnLlvmType = retLlvmType;
            var paramStr = string.Join(", ", fn.Parameters.Select(
                p => $"{LlvmType(SuruTypeFromAnnotation(p.TypeAnnotation))} %{p.Name}"));
            _funcs.AppendLine($"define internal {retLlvmType} @{fn.Name}({paramStr}) {{");
            _funcs.AppendLine("entry:");

            // Alloca + store each parameter so the body can read them via EmitLoad.
            // This is the standard LLVM mem2reg pattern: every incoming value gets a
            // stack slot in the entry block; the optimizer promotes these to SSA
            // registers. It keeps EmitLoad uniform — all variables go through _vars.
            foreach (var p in fn.Parameters)
            {
                var pType    = SuruTypeFromAnnotation(p.TypeAnnotation);
                var llvmT    = LlvmType(pType);
                var allocPtr = $"%{p.Name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca {llvmT}");
                _funcs.AppendLine($"  store {llvmT} %{p.Name}, ptr {allocPtr}");
                _vars[p.Name] = (allocPtr, pType);
                // Array<T> param: record element type from annotation so .at() knows the type.
                if (pType == SuruType.Array && p.TypeAnnotation.TypeParam is not null)
                    _arrayElementTypes[p.Name] = SuruTypeFromAnnotation(p.TypeAnnotation.TypeParam);
            }
        }

        foreach (var stmt in fn.Body)
            EmitStmt(stmt);

        // If the function body fell off the end without a terminator, close the block.
        // main gets an implicit `ret i64 0`; other non-void functions get `unreachable`.
        if (_blockOpen)
            _funcs.AppendLine(fn.Name == "main" ? "  ret i64 0" : "  unreachable");

        _funcs.AppendLine("}");
        _funcs.AppendLine();
    }

    // ─── Statement emission ──────────────────────────────────────────────────

    private void EmitStmt(Statement stmt)
    {
        switch (stmt)
        {
            // ── match expr { ... } used as a statement ─────────────────────
            // Each arm body is a side-effecting expression (typically printLn).
            case ExpressionStatement { Expression: MatchExpression matchStmt }:
                EmitMatchAsStatement(matchStmt);
                break;

            // ── printLn(expr) ──────────────────────────────────────────────
            case ExpressionStatement { Expression: CallExpression { Name: "printLn", Args: [var arg] } }:
                var (val, type) = EmitValue(arg);
                EmitPrintLn(val, type);
                break;

            // ── printError(expr) — writes to stderr via fprintf ────────────
            case ExpressionStatement { Expression: CallExpression { Name: "printError", Args: [var errArg] } }:
                _boolStringGlobals.AddFmtS();
                _externals.AddFprintf();
                _externals.AddStderr();
                var (errVal, _) = EmitValue(errArg);
                var stderrFp = NextTmp();
                // Strings are Seq structs — extract the data ptr before passing to fprintf.
                var errDataPtr = EmitExtractStringData(errVal);
                _funcs.AppendLine($"  {stderrFp} = load ptr, ptr @stderr");
                _funcs.AppendLine($"  call i32 (ptr, ptr, ...) @fprintf(ptr {stderrFp}, ptr @.fmt_s, ptr {errDataPtr})");
                break;

            // ── exit(code) — Int32 is passed directly; Int64 is truncated ──
            case ExpressionStatement { Expression: CallExpression { Name: "exit", Args: [var codeExpr] } }:
                _externals.AddExit();
                var (codeVal, codeType) = EmitValue(codeExpr);
                // When the argument is already an i32 (from Int32.from), no conversion
                // is needed — the value goes straight to @exit. An Int64 still needs
                // a trunc to match @exit's i32 parameter.
                string exitArg;
                if (codeType == SuruType.Int32)
                {
                    exitArg = codeVal;
                }
                else
                {
                    var code32 = NextTmp();
                    _funcs.AppendLine($"  {code32} = trunc i64 {codeVal} to i32");
                    exitArg = code32;
                }
                _funcs.AppendLine($"  call void @exit(i32 {exitArg})");
                // `exit` is `noreturn` but LLVM still needs a basic-block terminator.
                // Open a dead block so any statements after exit are absorbed cleanly.
                _funcs.AppendLine("  unreachable");
                _funcs.AppendLine($"dead_{_tmp}:");
                _blockOpen = true;
                break;

            // ── writeFile(path, content) — write a String to a file ───────
            case ExpressionStatement { Expression: CallExpression { Name: "writeFile", Args: [var wfPath, var wfContent] } }:
                EmitWriteFile(wfPath, wfContent);
                break;

            // ── let name TypeAnnotation: expr — allocate stack slot ──────────
            case LetStatement { Name: var name, Value: var valExpr, TypeAnnotation: var ann }:
                var (letVal, letType) = EmitValue(valExpr);
                // Int32 annotation coerces Int64 literals / expressions to i32.
                if (ann.Name == "Int32" && letType == SuruType.Int64)
                {
                    if (valExpr is IntLiteral)
                    {
                        letType = SuruType.Int32;
                    }
                    else
                    {
                        var truncTmp = NextTmp();
                        _funcs.AppendLine($"  {truncTmp} = trunc i64 {letVal} to i32");
                        letVal  = truncTmp;
                        letType = SuruType.Int32;
                    }
                }
                var llvmT    = LlvmType(letType);
                var allocPtr = $"%{name}.addr";
                _funcs.AppendLine($"  {allocPtr} = alloca {llvmT}");
                _funcs.AppendLine($"  store {llvmT} {letVal}, ptr {allocPtr}");
                _vars[name] = (allocPtr, letType);
                // Array element type: annotation is authoritative (Array<T>).
                // Fall back to the pending value emitted by EmitArrayLiteral/EmitArraySlice.
                if (letType == SuruType.Array)
                {
                    if (ann.TypeParam is not null)
                    {
                        _arrayElementTypes[name] = SuruTypeFromAnnotation(ann.TypeParam);
                        _pendingArrayElemType = null;
                    }
                    else if (_pendingArrayElemType.HasValue)
                    {
                        _arrayElementTypes[name] = _pendingArrayElemType.Value;
                        _pendingArrayElemType = null;
                    }
                }
                break;

            // ── return expr ────────────────────────────────────────────────
            // Use _currentFnReturnLlvmType so the `ret` instruction matches the
            // function's declared return type (e.g. `ret ptr` for String-returning fns).
            case ReturnStatement { Value: var retExpr }:
                var retVal = retExpr is null ? "0" : EmitValue(retExpr).Item1;
                _funcs.AppendLine($"  ret {_currentFnReturnLlvmType} {retVal}");
                _blockOpen = false;
                break;

            // ── while cond { body } ────────────────────────────────────────
            // Emits three labelled basic blocks:
            //   while_cond_N — evaluate the boolean condition and branch
            //   while_body_N — execute the loop body; if still open, loop back to cond
            //   while_after_N — execution continues here when the condition is false
            //
            // The unconditional `br label %while_cond_N` before the cond block closes the
            // preceding block so LLVM sees no fall-through between non-contiguous blocks.
            case WhileStatement { Condition: var whileCond, Body: var whileBody }:
                var wn = _whileCounter++;
                _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_cond_{wn}:");
                var (condVal2, _) = EmitValue(whileCond);
                _funcs.AppendLine($"  br i1 {condVal2}, label %while_body_{wn}, label %while_after_{wn}");
                _funcs.AppendLine($"while_body_{wn}:");
                foreach (var bodyStmt in whileBody)
                    EmitStmt(bodyStmt);
                // Only loop back if the body didn't terminate with exit/return.
                if (_blockOpen)
                    _funcs.AppendLine($"  br label %while_cond_{wn}");
                _funcs.AppendLine($"while_after_{wn}:");
                _blockOpen = true;
                break;

            // ── name: expr — overwrite an existing variable ────────────────
            // AssignmentStatement stores a new value into the alloca that was created
            // by the earlier LetStatement for this variable. The declared type (from _vars)
            // is used as the store width so the alloca and store sizes always agree.
            case AssignmentStatement { Name: var assignName, Value: var assignExpr }:
                var (assignVal, _) = EmitValue(assignExpr);
                var (assignPtr, assignType) = _vars[assignName];
                _funcs.AppendLine($"  store {LlvmType(assignType)} {assignVal}, ptr {assignPtr}");
                break;

            // ── receiver.field: value — update a single struct field in-place ──
            case FieldAssignmentStatement fieldAssign:
                EmitFieldAssignment(fieldAssign);
                break;

            // ── generic side-effecting expression (e.g. arr.set, arr.add) ───
            // Evaluated for its side effects; the result value is discarded.
            // Must come last — all named built-ins are matched by the cases above.
            case ExpressionStatement { Expression: var sideEffectExpr }:
                EmitValue(sideEffectExpr);
                break;

            default:
                throw new NotSupportedException($"IR codegen: unsupported statement {stmt.GetType().Name}");
        }
    }

    // ─── Expression emission ─────────────────────────────────────────────────

    // Returns an SSA value string and its Suru type.
    // Literals produce inline constants (no instructions emitted).
    // Variable references and method calls emit instructions to _funcs.
    private (string val, SuruType type) EmitValue(Expression expr) => expr switch
    {
        BoolLiteral b             => (b.Value ? "1" : "0", SuruType.Bool),
        // IntLiteral is always Int64 at the AST level; use Int32.from(...) to narrow.
        IntLiteral i              => (i.Value.ToString(CultureInfo.InvariantCulture), SuruType.Int64),
        // LLVM requires double constants in hex IEEE-754 form when the value
        // cannot be represented exactly in decimal — using the hex form always
        // is safe and avoids rounding surprises.
        FloatLiteral f            => ($"0x{BitConverter.DoubleToInt64Bits(f.Value):X16}", SuruType.Float64),
        StringLiteralExpression s  => EmitStringLiteralValue(s.Value),
        ArrayLiteralExpression arr => EmitArrayLiteral(arr),
        StructLiteralExpression structLit => EmitStructLiteral(structLit),
        FieldAccessExpression fa   => EmitFieldAccess(fa),
        // clone(x) for Struct — deep copy via linked-list traversal.
        CallExpression { Name: "clone", Args: [var cloneArg] }
            when PeekType(cloneArg) == SuruType.Struct
            => EmitCloneStruct(EmitValue(cloneArg).val),
        // drop(x) for Struct — free all field nodes; result is discarded by caller.
        CallExpression { Name: "drop", Args: [var dropArg] }
            when PeekType(dropArg) == SuruType.Struct
            => EmitDropStruct(EmitValue(dropArg).val),
        VariableReferenceExpression v => EmitLoad(v.Name),
        MethodCallExpression m    => EmitMethodCall(m),
        // `not x` — boolean NOT. Emits `xor i1 %val, true`.
        UnaryExpression { Op: UnaryOp.Not } u => EmitBoolNot(u.Operand),
        // Boolean binary operators: `x and y` → `and i1`, `x or y` → `or i1`.
        // Both operands are always Bool (enforced by the semantic analyzer).
        BinaryExpression bin      => EmitBinaryExpr(bin),
        // Match used as an expression: all arms produce a value collected via alloca.
        MatchExpression match     => EmitMatchAsExpression(match),
        // Built-in readFile in expression position (e.g. `let content: readFile(path)`).
        CallExpression { Name: "readFile", Args: [var pathArg] } => EmitReadFile(pathArg),
        // User-defined function call in expression position (e.g., as an argument to
        // a method call like `fibonacci(n.take(1)).add(...)`). Built-in calls like
        // printLn/exit are handled at the statement level in EmitStmt and never appear
        // here as expressions.
        CallExpression c when _userFunctions.ContainsKey(c.Name) => EmitUserFunctionCall(c),
        _ => throw new NotSupportedException($"IR codegen: unsupported expression {expr.GetType().Name}"),
    };

    // EmitStringLiteralValue is defined in IRStringCodeGenerator.cs (the string partial class).
    // It interns the raw bytes as a [N x i8] global and wraps them in a heap-allocated
    // %suru.Seq header so the value is a uniform `ptr` regardless of how it is used.

    // Emit `xor i1 %val, true` for `not x`. Result type is always Bool.
    private (string val, SuruType type) EmitBoolNot(Expression operand)
    {
        var (v, _) = EmitValue(operand);
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = xor i1 {v}, true");
        return (tmp, SuruType.Bool);
    }

    // Emit `and i1` or `or i1` for `x and y` / `x or y`. Result type is always Bool.
    private (string val, SuruType type) EmitBinaryExpr(BinaryExpression bin)
    {
        var (lv, _) = EmitValue(bin.Left);
        var (rv, _) = EmitValue(bin.Right);
        var op = bin.Op == BinaryOp.And ? "and" : "or";
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = {op} i1 {lv}, {rv}");
        return (tmp, SuruType.Bool);
    }

    // Emit a `load` from the alloca recorded in _vars, or from a module-level global.
    private (string val, SuruType type) EmitLoad(string name)
    {
        if (!_vars.TryGetValue(name, out var entry))
        {
            // Fall back to module-level constant globals (e.g. TOK_TRUE).
            var (gName, gType) = _globalVars[name];
            var gTmp = NextTmp();
            _funcs.AppendLine($"  {gTmp} = load {LlvmType(gType)}, ptr {gName}");
            return (gTmp, gType);
        }
        var (ptr, type) = entry;
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = load {LlvmType(type)}, ptr {ptr}");
        return (tmp, type);
    }

    // Dispatch method calls. Checks in priority order:
    //   1. Namespace calls — receiver is a namespace alias (from an `include` directive);
    //      dispatch as a user-defined function call with the qualified name "ns.method".
    //   2. Static type-name receivers (Int32.from, Int64.from) — must come before
    //      instance dispatch to avoid evaluating the receiver as a variable.
    //   3. Instance methods on the evaluated receiver value.
    private (string val, SuruType type) EmitMethodCall(MethodCallExpression m)
    {
        // Namespace calls: `lib.fn(args)` where `lib` is an include alias.
        // The receiver is not a variable — it's a namespace name stored in _module.Namespaces.
        // Build the qualified name ("lib.fn") and delegate to EmitUserFunctionCall, which
        // has already registered "lib.fn" in _userFunctions via the pre-pass.
        if (m.Receiver is VariableReferenceExpression { Name: var nsName } &&
            _module.Namespaces.Contains(nsName))
        {
            var qualifiedName = $"{nsName}.{m.MethodName}";
            return EmitUserFunctionCall(new CallExpression(qualifiedName, m.Args));
        }

        // Static methods: receiver is a type name, not a variable.
        if (m.Receiver is VariableReferenceExpression { Name: "Int32" })
            return EmitInt32StaticMethod(m.MethodName, m.Args);
        if (m.Receiver is VariableReferenceExpression { Name: "Int64" })
            return EmitInt64StaticMethod(m.MethodName, m.Args);

        // Instance methods — evaluate the receiver first to obtain its value and type.
        var (recvVal, recvType) = EmitValue(m.Receiver);

        // Array instance methods.
        // Two cases:
        //   Regular Suru arrays — data is i64[], element type tracked in _arrayElementTypes.
        //   argv Seq (args in suru_main) — data is char**; element access via EmitArgAt.
        // The argv Seq is never registered in _arrayElementTypes, so the absence of
        // metadata is the signal to fall through to EmitArgAt.
        if (recvType == SuruType.Array)
        {
            if (m.Receiver is VariableReferenceExpression { Name: var arrName }
                && _arrayElementTypes.TryGetValue(arrName, out var elemType))
                return m.MethodName switch
                {
                    "len"   => EmitArrayLen(recvVal),
                    "at"    => EmitArrayAt(recvVal, elemType, m.Args[0]),
                    "set"   => EmitArraySet(recvVal, elemType, m.Args[0], m.Args[1]),
                    "add"   => EmitArrayAdd(recvVal, elemType, m.Args[0]),
                    "slice" => EmitArraySlice(recvVal, m.Receiver, elemType, m.Args[0], m.Args[1]),
                    _ => throw new NotSupportedException($"IR codegen: unsupported Array method '{m.MethodName}'"),
                };

            // argv Seq: data is char** (not i64[]); only .at(i) is supported.
            return m.MethodName switch
            {
                "at" => EmitArgAt(recvVal, m.Args[0]),
                _ => throw new NotSupportedException($"IR codegen: unsupported argv Array method '{m.MethodName}'"),
            };
        }

        // String instance methods — all delegated to IRStringCodeGenerator.cs.
        // These are checked before the numeric dispatch so string `equals` uses strcmp
        // rather than the pointer-equality icmp that the numeric path would emit.
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

        // toString() on numeric types — converts the value to a String Seq.
        // Checked before the general switch because it returns String, not the receiver type.
        if (m.MethodName == "toString" && recvType == SuruType.Int64)
            return EmitInt64ToString(recvVal);

        // invert() is zero-argument (unary negation) — dispatch before the binary-op path.
        if (m.MethodName == "invert")
            return EmitInvert(recvVal, recvType);

        // compare() returns -1/0/1 (Int64) — dispatched before the generic comparison path
        // because it returns Int64, not Bool, and has its own emit shape.
        if (m.MethodName == "compare")
            return EmitCompare(recvVal, recvType, m.Args[0]);

        return m.MethodName switch
        {
            "add"      => EmitBinOp("add",  "fadd", recvVal, recvType, m.Args[0]),
            "take"     => EmitBinOp("sub",  "fsub", recvVal, recvType, m.Args[0]),
            "multiply" => EmitBinOp("mul",  "fmul", recvVal, recvType, m.Args[0]),
            "split"    => EmitBinOp("sdiv", "fdiv", recvVal, recvType, m.Args[0]),
            // Comparison methods: icmp for integers, fcmp for floats — all return i1 (Bool).
            "lt"     => EmitCmp("icmp slt", "fcmp olt", recvVal, recvType, m.Args[0]),
            "gt"     => EmitCmp("icmp sgt", "fcmp ogt", recvVal, recvType, m.Args[0]),
            "lte"    => EmitCmp("icmp sle", "fcmp ole", recvVal, recvType, m.Args[0]),
            "gte"    => EmitCmp("icmp sge", "fcmp oge", recvVal, recvType, m.Args[0]),
            "equals" => EmitCmp("icmp eq",  "fcmp oeq", recvVal, recvType, m.Args[0]),
            _ => throw new NotSupportedException($"IR codegen: unsupported method '{m.MethodName}'"),
        };
    }

    // Handle Int32 static methods.
    private (string val, SuruType type) EmitInt32StaticMethod(
        string methodName, IReadOnlyList<Expression> args)
    {
        return methodName switch
        {
            "from" => EmitInt32From(args[0]),
            _ => throw new NotSupportedException($"IR codegen: unknown Int32 static method '{methodName}'"),
        };
    }

    // Emit a call to a user-defined function. The parameter LLVM types are read from
    // the pre-registered _userFunctions signature so the call instruction uses the
    // same types as the callee's definition. The Suru-level return type is passed
    // back to the caller so subsequent operations (e.g., .add() on the result)
    // choose the correct LLVM opcode.
    //
    // All user-defined functions are compiled as `i64`-returning LLVM functions;
    // the Suru type is a logical overlay that lives only in _userFunctions.
    //
    // Recursive self-calls work without forward declarations because LLVM IR
    // definitions are visible module-wide regardless of textual order.
    //
    // Namespace-prefixed calls (e.g., `lib.double`) are transparent here: include
    // resolution renames the FunctionDeclaration to `"lib.double"` before codegen
    // runs, so both the define site and the call site use the same name. LLVM IR
    // accepts dots in function identifiers (`@lib.double` is a valid symbol), so
    // no name mangling is required.
    private (string val, SuruType type) EmitUserFunctionCall(CallExpression call)
    {
        var (paramDefs, returnType) = _userFunctions[call.Name];
        var argParts = new List<string>(call.Args.Count);
        for (int i = 0; i < call.Args.Count; i++)
        {
            var (argVal, _) = EmitValue(call.Args[i]);
            var llvmT       = LlvmType(SuruTypeFromAnnotation(paramDefs[i].TypeAnnotation));
            argParts.Add($"{llvmT} {argVal}");
        }
        // Use the registered LLVM return type, not a hardcoded `i64` — String-returning
        // functions are defined as `ptr` and the call instruction must match the definition.
        var callTmp     = NextTmp();
        var retLlvmType = LlvmType(returnType);
        _funcs.AppendLine($"  {callTmp} = call {retLlvmType} @{call.Name}({string.Join(", ", argParts)})");
        return (callTmp, returnType);
    }

    // Int32.from(expr): convert an Int64 expression to i32.
    // Constant-folds when the argument is an IntLiteral — emits an i32 constant
    // directly with no trunc instruction. For non-literal arguments a `trunc` is emitted.
    private (string val, SuruType type) EmitInt32From(Expression arg)
    {
        if (arg is IntLiteral lit)
            return (lit.Value.ToString(CultureInfo.InvariantCulture), SuruType.Int32);

        var (val, _) = EmitValue(arg);
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = trunc i64 {val} to i32");
        return (tmp, SuruType.Int32);
    }

    // Emit a binary arithmetic instruction. Uses the int opcode for Int32/Int64,
    // the float opcode for Float64; result type equals the receiver type.
    private (string val, SuruType type) EmitBinOp(
        string intOp, string floatOp,
        string lval, SuruType ltype, Expression argExpr)
    {
        var (rval, _) = EmitValue(argExpr);
        var tmp = NextTmp();
        var op  = ltype == SuruType.Float64 ? floatOp : intOp;
        _funcs.AppendLine($"  {tmp} = {op} {LlvmType(ltype)} {lval}, {rval}");
        return (tmp, ltype);
    }

    // Emit unary negation for .invert().
    // Integer: `sub <T> 0, val` (LLVM has no unary neg instruction).
    // Float:   `fneg double val`.
    private (string val, SuruType type) EmitInvert(string recvVal, SuruType recvType)
    {
        var tmp = NextTmp();
        if (recvType == SuruType.Float64)
            _funcs.AppendLine($"  {tmp} = fneg double {recvVal}");
        else
            _funcs.AppendLine($"  {tmp} = sub {LlvmType(recvType)} 0, {recvVal}");
        return (tmp, recvType);
    }

    // Emit a comparison instruction. Uses the integer opcode for Bool/Int32/Int64,
    // the float opcode for Float64. The result is always i1 → SuruType.Bool.
    // LLVM ordered float predicates (olt/ogt/ole/oge/oeq) return false when either
    // operand is NaN, which matches the expected semantics for Suru numeric comparisons.
    private (string val, SuruType type) EmitCmp(
        string intOp, string floatOp,
        string lval, SuruType ltype, Expression argExpr)
    {
        var (rval, _) = EmitValue(argExpr);
        var tmp = NextTmp();
        var op  = ltype == SuruType.Float64 ? floatOp : intOp;
        _funcs.AppendLine($"  {tmp} = {op} {LlvmType(ltype)} {lval}, {rval}");
        return (tmp, SuruType.Bool);
    }

    // Emit .compare(other): returns -1 if receiver < other, 0 if equal, 1 if greater.
    // Uses a subtract-of-zero-extensions trick: zext(gt) - zext(lt) gives 1, 0, or -1.
    // This avoids branches and matches the CodeGenerator's LLVMSharp approach exactly.
    private (string val, SuruType type) EmitCompare(
        string lval, SuruType ltype, Expression argExpr)
    {
        var (rval, _) = EmitValue(argExpr);
        var llvmT = LlvmType(ltype);
        var gtTmp  = NextTmp();
        var ltTmp  = NextTmp();
        var gtExt  = NextTmp();
        var ltExt  = NextTmp();
        var result = NextTmp();
        if (ltype == SuruType.Float64)
        {
            _funcs.AppendLine($"  {gtTmp} = fcmp ogt {llvmT} {lval}, {rval}");
            _funcs.AppendLine($"  {ltTmp} = fcmp olt {llvmT} {lval}, {rval}");
        }
        else
        {
            _funcs.AppendLine($"  {gtTmp} = icmp sgt {llvmT} {lval}, {rval}");
            _funcs.AppendLine($"  {ltTmp} = icmp slt {llvmT} {lval}, {rval}");
        }
        // Zero-extend the i1 flags to i64 so we can subtract them.
        _funcs.AppendLine($"  {gtExt} = zext i1 {gtTmp} to i64");
        _funcs.AppendLine($"  {ltExt} = zext i1 {ltTmp} to i64");
        _funcs.AppendLine($"  {result} = sub i64 {gtExt}, {ltExt}");
        return (result, SuruType.Int64);
    }

    // ─── Match emission ───────────────────────────────────────────────────────

    // Emit a match used as a statement: each arm body is a side-effecting call
    // (typically printLn). All arms branch to the shared merge label afterward.
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

    // A match arm body used as a statement is expected to be a call expression
    // (printLn / printError). Any other expression is evaluated for its side effects.
    private void EmitMatchArmBodyAsStatement(Expression body)
    {
        switch (body)
        {
            case CallExpression { Name: "printLn", Args: [var arg] }:
                var (v, t) = EmitValue(arg);
                EmitPrintLn(v, t);
                break;
            default:
                EmitValue(body);
                break;
        }
    }

    // Emit a match used as an expression: all arms produce a value.
    // Rather than phi nodes (which are harder to emit in text form), we allocate
    // a result slot upfront, store from each arm, then load after the merge label.
    // This is semantically equivalent and avoids the phi-placement bookkeeping.
    //
    // The result alloca MUST be emitted before EmitMatchTestChain — the test chain
    // ends with a conditional branch, and any alloca emitted after that branch would
    // be inside an arm block that does not dominate the other arms. Using an SSA
    // value outside its dominator is undefined behaviour; clang silently miscompiles
    // it into a segfault when the non-allocating arm is taken. We use PeekType to
    // determine the result type without emitting any instructions, so the alloca can
    // be placed in the block that precedes all branches.
    private (string val, SuruType type) EmitMatchAsExpression(MatchExpression match)
    {
        // Determine result type before any IR is emitted so we can place the alloca
        // in the current (pre-branch) block — which dominates all arm blocks.
        var firstArm = match.Arms.FirstOrDefault(a => a.Pattern != null) ?? match.Arms[0];
        var resultType = PeekType(firstArm.Body);
        var resultPtr  = $"%match_result_{_matchCounter}";  // _matchCounter not yet incremented
        _funcs.AppendLine($"  {resultPtr} = alloca {LlvmType(resultType)}");

        var (patternArms, wildcardArm, n) = EmitMatchTestChain(match);

        for (int i = 0; i < patternArms.Count; i++)
        {
            _funcs.AppendLine($"match_arm_{n}_{i}:");
            var (armVal, armType) = EmitValue(patternArms[i].Body);
            _funcs.AppendLine($"  store {LlvmType(armType)} {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        if (wildcardArm != null)
        {
            _funcs.AppendLine($"match_wildcard_{n}:");
            var (armVal, armType) = EmitValue(wildcardArm.Body);
            _funcs.AppendLine($"  store {LlvmType(armType)} {armVal}, ptr {resultPtr}");
            _funcs.AppendLine($"  br label %match_merge_{n}");
        }

        _funcs.AppendLine($"match_merge_{n}:");
        var loadTmp = NextTmp();
        _funcs.AppendLine($"  {loadTmp} = load {LlvmType(resultType)}, ptr {resultPtr}");
        return (loadTmp, resultType);
    }

    // Return the SuruType of an expression without emitting any IR instructions.
    // Used by EmitMatchAsExpression to determine the result alloca type before
    // the test chain branches — the alloca must be in a dominating block.
    // Mirrors the type rules in EmitValue and EmitMethodCall exactly.
    private SuruType PeekType(Expression expr) => expr switch
    {
        BoolLiteral                   => SuruType.Bool,
        IntLiteral                    => SuruType.Int64,
        FloatLiteral                  => SuruType.Float64,
        StringLiteralExpression       => SuruType.String,
        ArrayLiteralExpression        => SuruType.Array,
        StructLiteralExpression       => SuruType.Struct,
        FieldAccessExpression fa      => fa.ResolvedType ?? SuruType.Struct,
        CallExpression { Name: "clone" } => SuruType.Struct,
        UnaryExpression                   => SuruType.Bool,
        BinaryExpression                  => SuruType.Bool,
        VariableReferenceExpression v =>
            _vars.TryGetValue(v.Name, out var vEntry) ? vEntry.type
            : _globalVars[v.Name].Type,
        MatchExpression match         => PeekMatchType(match),
        MethodCallExpression m        => PeekMethodType(m),
        CallExpression { Name: "readFile" } => SuruType.String,
        CallExpression c when _userFunctions.ContainsKey(c.Name)
                                      => _userFunctions[c.Name].ReturnType,
        _ => throw new NotSupportedException($"IR codegen: cannot peek type of {expr.GetType().Name}"),
    };

    // Peek the result type of a match expression — the type of its first arm body,
    // since all arms must agree (enforced by the semantic analyser).
    private SuruType PeekMatchType(MatchExpression match)
    {
        var first = match.Arms.FirstOrDefault(a => a.Pattern != null) ?? match.Arms[0];
        return PeekType(first.Body);
    }

    // Peek the return type of a method call without emitting IR.
    private SuruType PeekMethodType(MethodCallExpression m)
    {
        // Namespace calls: receiver is an include alias — return type from _userFunctions.
        if (m.Receiver is VariableReferenceExpression { Name: var nsName2 } &&
            _module.Namespaces.Contains(nsName2))
            return _userFunctions[$"{nsName2}.{m.MethodName}"].ReturnType;

        // Array methods: look up element type from _arrayElementTypes when available.
        // For the argv Seq (not in _arrayElementTypes), .at() returns String.
        if (m.Receiver is VariableReferenceExpression rv2 &&
            _vars.TryGetValue(rv2.Name, out var rv2Entry) && rv2Entry.type == SuruType.Array)
        {
            if (_arrayElementTypes.TryGetValue(rv2.Name, out var aet))
                return m.MethodName switch
                {
                    "len"   => SuruType.Int64,
                    "at"    => aet,
                    "set"   => SuruType.Bool,
                    "add"   => SuruType.Bool,
                    "slice" => SuruType.Array,
                    _ => throw new NotSupportedException($"IR codegen: cannot peek type for Array method '{m.MethodName}'"),
                };
            return m.MethodName == "at" ? SuruType.String  // argv path
                : throw new NotSupportedException($"IR codegen: cannot peek type for argv method '{m.MethodName}'");
        }

        // Static methods: receiver is a type name (Int32, Int64, …), not a variable.
        // Int32.from → Int32; Int64.from → Int64; etc.
        if (m.Receiver is VariableReferenceExpression { Name: var typeName }
            && typeName is "Int32" or "Int64" or "Float64" or "Bool" or "String")
            return SuruTypeFromAnnotation(new TypeAnnotation(typeName));

        return m.MethodName switch
        {
            // Arithmetic — result is same type as receiver.
            "add" or "take" or "multiply" or "split" or "invert" => PeekType(m.Receiver),
            // Comparisons — always Bool (i1). String `equals` also returns Bool even though
            // its implementation uses strcmp; PeekMethodType doesn't see the receiver type.
            "lt" or "gt" or "lte" or "gte" or "equals"           => SuruType.Bool,
            // Numeric utilities — always Int64.
            "compare" or "ord" or "len"                           => SuruType.Int64,
            // String-producing methods (instance and static).
            "toString" or "at" or "append" or "slice"             => SuruType.String,
            _ => throw new NotSupportedException($"IR codegen: cannot peek type for method '{m.MethodName}'"),
        };
    }

    // Build the test-chain: evaluate the match condition, separate pattern arms from
    // the wildcard arm, then emit a conditional branch for each pattern arm in order.
    // Each miss falls through to the next test label; the final miss goes to the
    // wildcard block (if present) or directly to the merge label.
    //
    // The caller receives the sorted arm lists and the unique counter `n` so it can
    // emit the arm bodies under the correct labels (match_arm_N_M / match_wildcard_N).
    //
    // String conditions use @strcmp (returns i32); numeric/bool conditions use icmp/fcmp.
    // IRCodeGenerator strings are raw null-terminated pointers — no Seq header to unpack.
    private (List<MatchArm> PatternArms, MatchArm? WildcardArm, int N) EmitMatchTestChain(
        MatchExpression match)
    {
        var (condVal, condType) = EmitValue(match.Condition);
        int n = _matchCounter++;

        var patternArms = match.Arms.Where(a => a.Pattern != null).ToList();
        var wildcardArm = match.Arms.FirstOrDefault(a => a.Pattern == null);

        // The final "else" target: wildcard block if there is one, otherwise merge.
        var missLabel = wildcardArm != null ? $"match_wildcard_{n}" : $"match_merge_{n}";

        for (int i = 0; i < patternArms.Count; i++)
        {
            var cmpTmp = NextTmp();

            if (condType == SuruType.String)
            {
                // String pattern matching: extract the data pointer from each Seq, then
                // call strcmp. Both the condition value and the pattern literal are Seq ptrs.
                _externals.AddStrcmp();
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                var condData    = EmitExtractStringData(condVal);
                var patternData = EmitExtractStringData(patternVal);
                var strcmpTmp   = NextTmp();
                _funcs.AppendLine($"  {strcmpTmp} = call i32 @strcmp(ptr {condData}, ptr {patternData})");
                _funcs.AppendLine($"  {cmpTmp} = icmp eq i32 {strcmpTmp}, 0");
            }
            else if (condType == SuruType.Float64)
            {
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                _funcs.AppendLine($"  {cmpTmp} = fcmp oeq double {condVal}, {patternVal}");
            }
            else
            {
                // Bool and Int64 both use icmp eq with their natural LLVM types.
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                _funcs.AppendLine($"  {cmpTmp} = icmp eq {LlvmType(condType)} {condVal}, {patternVal}");
            }

            // If this is not the last pattern arm, the "miss" goes to an intermediate
            // test label so we can chain the next comparison. Otherwise it goes to the
            // wildcard or merge label.
            var nextLabel = (i + 1 < patternArms.Count)
                ? $"match_test_{n}_{i + 1}"
                : missLabel;

            _funcs.AppendLine($"  br i1 {cmpTmp}, label %match_arm_{n}_{i}, label %{nextLabel}");

            // Open the next intermediate test block (if needed).
            if (i + 1 < patternArms.Count)
                _funcs.AppendLine($"match_test_{n}_{i + 1}:");
        }

        // Edge case: no pattern arms at all — jump straight to wildcard/merge.
        if (patternArms.Count == 0)
            _funcs.AppendLine($"  br label %{missLabel}");

        return (patternArms, wildcardArm, n);
    }

    // ─── printLn dispatch ────────────────────────────────────────────────────

    private void EmitPrintLn(string val, SuruType type)
    {
        switch (type)
        {
            case SuruType.Bool:
                // Bool is an i1; select between the two string constants, then printf %s.
                _boolStringGlobals.AddFmtS();
                _boolStringGlobals.AddStrTrue();
                _boolStringGlobals.AddStrFalse();
                var sel = NextTmp();
                _funcs.AppendLine($"  {sel} = select i1 {val}, ptr @.str_true, ptr @.str_false");
                _funcs.AppendLine($"  call i32 (ptr, ...) @printf(ptr @.fmt_s, ptr {sel})");
                break;

            case SuruType.Int32:
                // i32 matches printf's `%d` directly — no extension needed.
                _boolStringGlobals.AddFmtInt32();
                _funcs.AppendLine($"  call i32 (ptr, ...) @printf(ptr @.fmt_int32, i32 {val})");
                break;

            case SuruType.Int64:
                _boolStringGlobals.AddFmtInt();
                _funcs.AppendLine($"  call i32 (ptr, ...) @printf(ptr @.fmt_int, i64 {val})");
                break;

            case SuruType.Float64:
                _boolStringGlobals.AddFmtFloat();
                _funcs.AppendLine($"  call i32 (ptr, ...) @printf(ptr @.fmt_float, double {val})");
                break;

            case SuruType.String:
                // Strings are %suru.Seq structs — extract the data pointer before printf.
                _boolStringGlobals.AddFmtS();
                var strDataPtr = EmitExtractStringData(val);
                _funcs.AppendLine($"  call i32 (ptr, ...) @printf(ptr @.fmt_s, ptr {strDataPtr})");
                break;

            default:
                throw new NotSupportedException($"IR codegen: printLn unsupported type {type}");
        }

        _externals.AddPrintf();
    }

    // ─── @main wrapper ───────────────────────────────────────────────────────

    // Emits a C-ABI `int main(int argc, char** argv)` that builds a %suru.Seq
    // wrapping argv, then calls the user's `suru_main` and returns its exit code.
    //
    // `suru_main` is `internal` so the linker does not export it as a public C symbol —
    // only this @main wrapper is the true entry point. The argv array is not converted
    // element-by-element: the raw `char**` pointer is stored directly in the Seq's data
    // field and argc is stored as len. Individual elements are accessed at runtime via the
    // `args.at(i)` built-in, which GEPs into the char** and wraps each char* in a Seq.
    private void EmitMainWrapper(StringBuilder sb)
    {
        sb.AppendLine("define i32 @main(i32 %argc, ptr %argv) {");
        sb.AppendLine("entry:");
        sb.AppendLine("  %seq      = call ptr @malloc(i64 16)");
        sb.AppendLine("  %len_gep  = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0");
        sb.AppendLine("  %argc64   = sext i32 %argc to i64");
        sb.AppendLine("  store i64 %argc64, ptr %len_gep");
        sb.AppendLine("  %data_gep = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1");
        sb.AppendLine("  store ptr %argv, ptr %data_gep");
        sb.AppendLine("  %suru_ret = call i64 @suru_main(ptr %seq)");
        sb.AppendLine("  %ret32    = trunc i64 %suru_ret to i32");
        sb.AppendLine("  ret i32 %ret32");
        sb.AppendLine("}");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Map a TypeAnnotation to the corresponding SuruType enum value.
    // The generic type parameter (e.g. the Struct in Array<Struct>) is handled
    // separately by the callers that need the element type.
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

    // Map a Suru type to its LLVM IR type keyword.
    // Both String and Array map to `ptr` — they share the same %suru.Seq = { i64 len, ptr data }
    // header layout. The distinction is only in how the data pointer is interpreted at runtime.
    private static string LlvmType(SuruType type) => type switch
    {
        SuruType.Bool    => "i1",
        SuruType.Int32   => "i32",
        SuruType.Int64   => "i64",
        SuruType.Float64 => "double",
        SuruType.String  => "ptr",
        SuruType.Array   => "ptr",
        SuruType.Struct  => "ptr",
        _ => throw new NotSupportedException($"IR: no LLVM type for {type}"),
    };

    // Convert a Suru source string literal to LLVM IR `c"..."` byte-escape form.
    // Suru escape sequences (\n \t \\ \") are mapped to their two-digit hex form
    // (\0A \09 \\ \22) as required by the LLVM IR string syntax.
    private static string EscapeStringForIR(string source)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '\\' && i + 1 < source.Length)
            {
                switch (source[i + 1])
                {
                    case 'n':  sb.Append("\\0A"); i++; break;
                    case 't':  sb.Append("\\09"); i++; break;
                    case '\\': sb.Append("\\\\"); i++; break;
                    case '"':  sb.Append("\\22"); i++; break;
                    default:   sb.Append(c); break;
                }
            }
            else if (c >= 32 && c < 127 && c != '"' && c != '\\')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append($"\\{(int)c:X2}");
            }
        }
        return sb.ToString();
    }

    // Count the number of actual bytes that a source string will occupy (excluding
    // null terminator). Suru two-character escape sequences count as one byte each.
    private static int CountStringBytes(string source)
    {
        int count = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\\' && i + 1 < source.Length)
            {
                char next = source[i + 1];
                if (next is 'n' or 't' or '\\' or '"') i++;
            }
            count++;
        }
        return count;
    }

    // ─── Array.at for argv ───────────────────────────────────────────────────

    // args.at(i) — extract the i-th element from the argv Seq built by @main.
    //
    // The argv Seq's data field is a raw char** (the C argv pointer), not an i64 array.
    // Element access therefore GEPs into a ptr array rather than an i64 array:
    //
    //   %data  = load ptr from Seq.data           → char**
    //   %slot  = getelementptr ptr, ptr %data, i64 %i   → pointer to char*
    //   %cstr  = load ptr, ptr %slot              → char* for argv[i]
    //   %len   = call i64 @strlen(ptr %cstr)      → character count
    //   return EmitCreateStringSeq(%cstr, %len)   → String Seq wrapping the C string
    //
    // The resulting String Seq holds the original char* without copying — the data
    // lifetime is tied to the process argv, which outlives suru_main.
    // This is NOT a general Array.at implementation; it works only for the argv Seq.
    private (string val, SuruType type) EmitArgAt(string seqVal, Expression idxExpr)
    {
        var data    = EmitExtractStringData(seqVal);   // char**
        var (idx, _) = EmitValue(idxExpr);

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

    // readFile(path String) → String
    //
    // Opens the file at `path`, reads its full content into a heap buffer, and wraps
    // the buffer in a %suru.Seq header. Uses the fseek(SEEK_END) + ftell pattern to
    // determine file size without a second pass.
    //
    // Steps:
    //   fopen(path_data, "r")             → file ptr
    //   fseek(file, 0, SEEK_END=2)        → move to end
    //   size = ftell(file)                → byte count
    //   rewind(file)                      → reset to start
    //   buf = malloc(size + 1)            → exact-size buffer (+1 for null terminator)
    //   fread(buf, 1, size, file)         → fill buffer
    //   buf[size] = '\0'                  → null-terminate
    //   fclose(file)
    //   EmitCreateStringSeq(buf, size)    → return String Seq
    //
    // The null terminator is needed so the buffer is a valid C string when passed to
    // fopen in a subsequent readFile call or to strcmp in string comparisons.
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

        // Null-terminate so the buffer is a valid C string.
        var nullSlot = NextTmp();
        _funcs.AppendLine($"  {nullSlot} = getelementptr i8, ptr {buf}, i64 {size}");
        _funcs.AppendLine($"  store i8 0, ptr {nullSlot}");

        _externals.AddFclose();
        _funcs.AppendLine($"  call i32 @fclose(ptr {file})");

        return EmitCreateStringSeq(buf, size);
    }

    // writeFile(path String, content String) — write content to a file (statement only).
    //
    // Opens the file at `path` in write mode (truncates existing content), writes the
    // full content buffer in one fwrite call, then closes the file.
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

    // Return the Suru return type of a function declaration.
    // `void` and absent return types are treated as Int64 — void functions are called
    // for side effects and any notional return value is discarded.
    private static SuruType FnReturnSuruType(FunctionDeclaration fn)
        => fn.ReturnType.Name is "void" ? SuruType.Int64 : SuruTypeFromAnnotation(fn.ReturnType);

    private string NextTmp() => $"%t{_tmp++}";
}
