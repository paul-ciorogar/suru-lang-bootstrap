using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;
using System.Linq;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{
    private readonly LLVMModuleRef _llvmModule;
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMValueRef _printfFn;
    private readonly LLVMTypeRef _printfType;
    private readonly LLVMTypeRef _ptrType;
    private LLVMTypeRef _fieldNodeType;
    private LLVMTypeRef _seqNodeType;  // %suru.Seq = { i64, ptr } — shared layout for Array and String headers
    private LLVMValueRef _mallocFn;
    private LLVMTypeRef _mallocFnType;
    private LLVMValueRef _freeFn;
    private LLVMTypeRef _freeFnType;
    private LLVMValueRef _reallocFn;
    private LLVMTypeRef _reallocFnType;
    private LLVMValueRef _memcpyFn;
    private LLVMTypeRef _memcpyFnType;
    private LLVMValueRef _strcmpFn;
    private LLVMTypeRef _strcmpFnType;
    private LLVMValueRef _strtolFn;
    private LLVMTypeRef _strtolFnType;
    private LLVMValueRef _strtodFn;
    private LLVMTypeRef _strtodFnType;
    private LLVMValueRef _sprintfFn;
    private LLVMTypeRef _sprintfFnType;
    private LLVMValueRef _strlenFn;
    private LLVMTypeRef _strlenFnType;
    private LLVMValueRef _fopenFn;
    private LLVMTypeRef _fopenFnType;
    private LLVMValueRef _fcloseFn;
    private LLVMTypeRef _fcloseFnType;
    private LLVMValueRef _fseekFn;
    private LLVMTypeRef _fseekFnType;
    private LLVMValueRef _ftellFn;
    private LLVMTypeRef _ftellFnType;
    private LLVMValueRef _rewindFn;
    private LLVMTypeRef _rewindFnType;
    private LLVMValueRef _freadFn;
    private LLVMTypeRef _freadFnType;
    private LLVMValueRef _fwriteFn;
    private LLVMTypeRef _fwriteFnType;
    private LLVMValueRef _exitFn;
    private LLVMTypeRef _exitFnType;
    private readonly Dictionary<string, (LLVMValueRef Alloca, SuruType Type)> _vars = new();
    private int _whileCounter;
    // Array element types for function array parameters: function name → param index → element type.
    private readonly Dictionary<string, Dictionary<int, SuruType>> _functionArrayParamMeta = new();
    // Element type of the Array returned by a function: function name → element type.
    private readonly Dictionary<string, SuruType> _functionReturnArrayMeta = new();
    private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef FnType, SuruType? ReturnType)> _userFunctions = new();
    // Array element type metadata: variable name → element SuruType
    private readonly Dictionary<string, SuruType> _varArrayMeta = new();
    // suru_find_field(ptr head, ptr name) -> ptr: runtime linked-list field search
    private LLVMValueRef _findFieldFn;
    private LLVMTypeRef _findFieldFnType;

    private CodeGenerator(LLVMModuleRef llvmModule, LLVMBuilderRef builder, LLVMValueRef printfFn, LLVMTypeRef printfType, LLVMTypeRef ptrType)
    {
        _llvmModule = llvmModule;
        _builder = builder;
        _printfFn = printfFn;
        _printfType = printfType;
        _ptrType = ptrType;
    }

    public static LLVMModuleRef Generate(Module module)
    {
        var llvmModule = LLVMModuleRef.CreateWithName("suru");
        var context = llvmModule.Context;

        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        var printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        var printfFn = llvmModule.AddFunction("printf", printfType);

        var builder = LLVMBuilderRef.Create(context);
        var gen = new CodeGenerator(llvmModule, builder, printfFn, printfType, ptrType);

        // %suru.Field = type { ptr, i32, i64, ptr }
        gen._fieldNodeType = context.CreateNamedStruct("suru.Field");
        gen._fieldNodeType.StructSetBody([ptrType, LLVMTypeRef.Int32, LLVMTypeRef.Int64, ptrType], false);

        gen._mallocFnType = LLVMTypeRef.CreateFunction(ptrType, [LLVMTypeRef.Int64]);
        gen._mallocFn = llvmModule.AddFunction("malloc", gen._mallocFnType);

        gen._freeFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [ptrType]);
        gen._freeFn = llvmModule.AddFunction("free", gen._freeFnType);

        gen._reallocFnType = LLVMTypeRef.CreateFunction(ptrType, [ptrType, LLVMTypeRef.Int64]);
        gen._reallocFn = llvmModule.AddFunction("realloc", gen._reallocFnType);

        gen._memcpyFnType = LLVMTypeRef.CreateFunction(ptrType, [ptrType, ptrType, LLVMTypeRef.Int64]);
        gen._memcpyFn = llvmModule.AddFunction("memcpy", gen._memcpyFnType);

        gen._strcmpFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType, ptrType]);
        gen._strcmpFn = llvmModule.AddFunction("strcmp", gen._strcmpFnType);

        gen._strtolFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int64, [ptrType, ptrType, LLVMTypeRef.Int32]);
        gen._strtolFn = llvmModule.AddFunction("strtol", gen._strtolFnType);

        gen._strtodFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Double, [ptrType, ptrType]);
        gen._strtodFn = llvmModule.AddFunction("strtod", gen._strtodFnType);

        gen._sprintfFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType, ptrType], true);
        gen._sprintfFn = llvmModule.AddFunction("sprintf", gen._sprintfFnType);

        gen._strlenFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int64, [ptrType]);
        gen._strlenFn = llvmModule.AddFunction("strlen", gen._strlenFnType);

        // fopen(path, mode) -> ptr
        gen._fopenFnType = LLVMTypeRef.CreateFunction(ptrType, [ptrType, ptrType]);
        gen._fopenFn = llvmModule.AddFunction("fopen", gen._fopenFnType);

        // fclose(file) -> i32
        gen._fcloseFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType]);
        gen._fcloseFn = llvmModule.AddFunction("fclose", gen._fcloseFnType);

        // fseek(file, offset, whence) -> i32
        gen._fseekFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType, LLVMTypeRef.Int64, LLVMTypeRef.Int32]);
        gen._fseekFn = llvmModule.AddFunction("fseek", gen._fseekFnType);

        // ftell(file) -> i64
        gen._ftellFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int64, [ptrType]);
        gen._ftellFn = llvmModule.AddFunction("ftell", gen._ftellFnType);

        // rewind(file) -> void
        gen._rewindFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [ptrType]);
        gen._rewindFn = llvmModule.AddFunction("rewind", gen._rewindFnType);

        // fread(buf, size, nmemb, file) -> i64
        gen._freadFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int64, [ptrType, LLVMTypeRef.Int64, LLVMTypeRef.Int64, ptrType]);
        gen._freadFn = llvmModule.AddFunction("fread", gen._freadFnType);

        // fwrite(buf, size, nmemb, file) -> i64
        gen._fwriteFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int64, [ptrType, LLVMTypeRef.Int64, LLVMTypeRef.Int64, ptrType]);
        gen._fwriteFn = llvmModule.AddFunction("fwrite", gen._fwriteFnType);

        // exit(code) -> void
        gen._exitFnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [LLVMTypeRef.Int32]);
        gen._exitFn = llvmModule.AddFunction("exit", gen._exitFnType);

        // %suru.Seq = type { i64, ptr } — shared layout for Array and String headers
        gen._seqNodeType = context.CreateNamedStruct("suru.Seq");
        gen._seqNodeType.StructSetBody([LLVMTypeRef.Int64, ptrType], false);

        // Emit runtime helper: suru_find_field(ptr head, ptr name) -> ptr
        gen.EmitFindFieldHelper();

        // Pass 1: declare all user-defined functions (enables forward references and recursion).
        foreach (var stmt in module.Statements)
            if (stmt is FunctionDeclaration fn) gen.DeclareFunction(fn);

        // Pass 2: emit bodies of user-defined functions.
        foreach (var stmt in module.Statements)
            if (stmt is FunctionDeclaration fn) gen.EmitFunctionBody(fn);

        // Pass 3: emit main(). If the user defined fn main, wrap it; otherwise emit implicit main.
        var hasExplicitMain = module.Statements.OfType<FunctionDeclaration>().Any(f => f.Name == "main");
        if (hasExplicitMain)
        {
            gen.EmitMainWrapper();
        }
        else
        {
            var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
            var mainFn = llvmModule.AddFunction("main", mainType);
            mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

            var entry = mainFn.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);

            foreach (var stmt in module.Statements)
                if (stmt is not FunctionDeclaration) gen.EmitStmt(stmt);

            builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        }
        builder.Dispose();

        return llvmModule;
    }

    private void DeclareFunction(FunctionDeclaration fn)
    {
        var paramLlvmTypes = fn.Parameters
            .Select(p => LlvmTypeFor(ResolveTypeName(p.TypeName)!.Value))
            .ToArray();

        SuruType? returnSuruType = fn.ReturnTypeName == "void" ? null : ResolveTypeName(fn.ReturnTypeName);
        var llvmReturnType = returnSuruType.HasValue ? LlvmTypeFor(returnSuruType.Value) : LLVMTypeRef.Void;

        // Rename user-defined 'main' to 'suru_main' to avoid collision with C main wrapper
        var llvmName = fn.Name == "main" ? "suru_main" : fn.Name;

        var fnType = LLVMTypeRef.CreateFunction(llvmReturnType, paramLlvmTypes);
        var llvmFn = _llvmModule.AddFunction(llvmName, fnType);
        llvmFn.Linkage = LLVMLinkage.LLVMInternalLinkage;

        _userFunctions[fn.Name] = (llvmFn, fnType, returnSuruType);
    }

    private void EmitFunctionBody(FunctionDeclaration fn)
    {
        var (llvmFn, _, _) = _userFunctions[fn.Name];

        var entry = llvmFn.AppendBasicBlock("entry");
        _builder.PositionAtEnd(entry);

        var outerVars      = new Dictionary<string, (LLVMValueRef Alloca, SuruType Type)>(_vars);
        var outerArrayMeta = new Dictionary<string, SuruType>(_varArrayMeta);
        _vars.Clear();
        _varArrayMeta.Clear();

        for (int i = 0; i < fn.Parameters.Count; i++)
        {
            var p = fn.Parameters[i];
            var paramType = ResolveTypeName(p.TypeName)!.Value;
            var alloca = _builder.BuildAlloca(LlvmTypeFor(paramType), p.Name);
            _builder.BuildStore(llvmFn.GetParam((uint)i), alloca);
            _vars[p.Name] = (alloca, paramType);
        }

        // fn main(args Array): args is an Array of String (CLI argv)
        if (fn.Name == "main")
            foreach (var p in fn.Parameters)
                if (ResolveTypeName(p.TypeName) == SuruType.Array)
                    _varArrayMeta[p.Name] = SuruType.String;

        foreach (var stmt in fn.Body)
            EmitStmt(stmt);

        if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero && fn.ReturnTypeName == "void")
            _builder.BuildRetVoid();

        // Capture array parameter element types discovered during body analysis.
        for (int pi = 0; pi < fn.Parameters.Count; pi++)
        {
            var pType = ResolveTypeName(fn.Parameters[pi].TypeName);
            if (pType == SuruType.Array && _varArrayMeta.TryGetValue(fn.Parameters[pi].Name, out var pEt))
            {
                if (!_functionArrayParamMeta.ContainsKey(fn.Name))
                    _functionArrayParamMeta[fn.Name] = new Dictionary<int, SuruType>();
                _functionArrayParamMeta[fn.Name][pi] = pEt;
            }
        }

        // Capture return array element type.
        if (_pendingArrayMeta.HasValue)
        {
            _functionReturnArrayMeta[fn.Name] = _pendingArrayMeta.Value;
            _pendingArrayMeta = null;
        }

        _vars.Clear();
        _varArrayMeta.Clear();
        foreach (var kv in outerVars)      _vars[kv.Key]      = kv.Value;
        foreach (var kv in outerArrayMeta) _varArrayMeta[kv.Key] = kv.Value;
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

    private void EmitStmt(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement { Expression: CallExpression { Name: "printLn", Args.Count: 1 } call }:
                var (val, type) = EmitValue(call.Args[0]);
                EmitPrintLn(val, type);
                break;

            case ExpressionStatement { Expression: MatchExpression matchStmt }:
                EmitMatchAsStatement(matchStmt);
                break;

            case ExpressionStatement exprStmt:
                EmitValue(exprStmt.Expression);
                break;

            case LetStatement let:
                var (letVal, letType) = EmitValue(let.Value);
                var alloca = _builder.BuildAlloca(LlvmTypeFor(letType), let.Name);
                _builder.BuildStore(letVal, alloca);
                _vars[let.Name] = (alloca, letType);
                if (letType == SuruType.Array)
                    PropagateArrayMeta(let.Name, let.Value);
                break;

            case FieldAssignmentStatement fieldAssign:
            {
                var headPtr = LoadStructPtr(fieldAssign.Receiver);
                var (assignedVal, assignedType) = EmitValue(fieldAssign.Value);
                var targetNode = FindFieldNode(headPtr, fieldAssign.FieldName);
                var valSlot = _builder.BuildStructGEP2(_fieldNodeType, targetNode, 2, "val_slot");
                _builder.BuildStore(ToI64(assignedVal, assignedType), valSlot);
                break;
            }

            case AssignmentStatement assign:
                var (newVal, _) = EmitValue(assign.Value);
                _builder.BuildStore(newVal, _vars[assign.Name].Alloca);
                break;

            case ReturnStatement ret:
                if (ret.Value is null)
                    _builder.BuildRetVoid();
                else
                {
                    var (retVal, _) = EmitValue(ret.Value);
                    _builder.BuildRet(retVal);
                }
                break;

            case WhileStatement whileStmt:
            {
                var whileFn = _builder.InsertBlock.Parent;
                int n = _whileCounter++;
                var condBlock  = whileFn.AppendBasicBlock($"while_cond_{n}");
                var bodyBlock  = whileFn.AppendBasicBlock($"while_body_{n}");
                var afterBlock = whileFn.AppendBasicBlock($"while_after_{n}");

                _builder.BuildBr(condBlock);

                _builder.PositionAtEnd(condBlock);
                var (condVal, _) = EmitValue(whileStmt.Condition);
                _builder.BuildCondBr(condVal, bodyBlock, afterBlock);

                _builder.PositionAtEnd(bodyBlock);
                foreach (var bodyStmt in whileStmt.Body)
                    EmitStmt(bodyStmt);
                if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
                    _builder.BuildBr(condBlock);

                _builder.PositionAtEnd(afterBlock);
                break;
            }

            case FunctionDeclaration:
                // Handled in the two-pass approach in Generate().
                break;
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitValue(Expression expr)
    {
        switch (expr)
        {
            case BoolLiteral b:
                return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, b.Value ? 1UL : 0UL, false), SuruType.Bool);

            case IntLiteral i:
                return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i.Value, true), SuruType.Int64);

            case FloatLiteral f:
                return (LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, f.Value), SuruType.Float64);

            case VariableReferenceExpression varRef:
            {
                var (alloca, varType) = _vars[varRef.Name];
                return (_builder.BuildLoad2(LlvmTypeFor(varType), alloca, varRef.Name), varType);
            }

            case StructLiteralExpression structLit:
                return EmitStructLiteral(structLit);

            case ArrayLiteralExpression arrLit:
                return EmitArrayLiteral(arrLit);

            case StringLiteralExpression strLit:
                return EmitStringLiteral(strLit);

            case FieldAccessExpression fa:
            {
                var headPtr    = LoadStructPtr(fa.Receiver);
                var targetNode = FindFieldNode(headPtr, fa.FieldName);
                var valSlot    = _builder.BuildStructGEP2(_fieldNodeType, targetNode, 2, "val_slot");
                var rawVal     = _builder.BuildLoad2(LLVMTypeRef.Int64, valSlot, "raw_val");

                if (fa.ResolvedType is { } fieldType)
                {
                    var typedVal = FromI64(rawVal, fieldType);
                    return (typedVal, fieldType);
                }

                // ResolvedType not known at compile time — read the tag from the node at runtime.
                // Node layout: { ptr name [0], i32 tag [1], i64 val [2], ptr next [3] }
                // Tag: 0=Bool, 1=Int64, 2=Float64, 3=pointer (String/Array/Struct)
                var tagSlot = _builder.BuildStructGEP2(_fieldNodeType, targetNode, 1, "tag_slot");
                var tag     = _builder.BuildLoad2(LLVMTypeRef.Int32, tagSlot, "tag");

                // Branch: tag > 2 means pointer type, otherwise scalar (treat as Int64)
                var isPtr   = _builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, tag,
                                  LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 2), "is_ptr");

                var fn       = _builder.InsertBlock.Parent;
                var ptrBB    = fn.AppendBasicBlock("fa_ptr");
                var scalarBB = fn.AppendBasicBlock("fa_scalar");
                var mergeBB  = fn.AppendBasicBlock("fa_merge");

                _builder.BuildCondBr(isPtr, ptrBB, scalarBB);

                _builder.PositionAtEnd(ptrBB);
                var asPtr = _builder.BuildIntToPtr(rawVal, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "fa_as_ptr");
                _builder.BuildBr(mergeBB);

                _builder.PositionAtEnd(scalarBB);
                // rawVal is already i64 — leave it as is; the phi will unify via inttoptr
                var asI64Ptr = _builder.BuildIntToPtr(rawVal, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "fa_scalar_ptr");
                _builder.BuildBr(mergeBB);

                _builder.PositionAtEnd(mergeBB);
                var ptrTy  = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                var phi    = _builder.BuildPhi(ptrTy, "fa_val");
                phi.AddIncoming([asPtr, asI64Ptr], [ptrBB, scalarBB], 2);
                // Return as opaque ptr with SuruType.Struct — callers using this value
                // for further field access or as a Struct argument will work correctly.
                return (phi, SuruType.Struct);
            }

            case MethodCallExpression method:
                return EmitMethodCall(method);

            case UnaryExpression { Op: UnaryOp.Not } unary:
            {
                var (operand, _) = EmitValue(unary.Operand);
                return (_builder.BuildNot(operand, ""), SuruType.Bool);
            }

            case BinaryExpression binary:
            {
                var (left, _) = EmitValue(binary.Left);
                var (right, _) = EmitValue(binary.Right);
                var result = binary.Op switch
                {
                    BinaryOp.And => _builder.BuildAnd(left, right, ""),
                    BinaryOp.Or  => _builder.BuildOr(left, right, ""),
                    _ => throw new InvalidOperationException($"Unknown binary op {binary.Op}"),
                };
                return (result, SuruType.Bool);
            }

            case MatchExpression match:
                return EmitMatchAsExpression(match);

            case CallExpression call:
                return EmitCallExpression(call);

            default:
                throw new InvalidOperationException($"Unsupported expression type {expr.GetType().Name}");
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitCallExpression(CallExpression call)
    {
        if (call.Name == "clone" && call.Args.Count == 1 &&
            call.Args[0] is VariableReferenceExpression cloneSrc)
        {
            if (_varArrayMeta.ContainsKey(cloneSrc.Name))
                return EmitCloneArray(cloneSrc.Name);
            var (cloneHeadPtr, _) = EmitValue(call.Args[0]);
            return EmitCloneStruct(cloneHeadPtr);
        }

        if (call.Name == "drop" && call.Args.Count == 1 &&
            call.Args[0] is VariableReferenceExpression dropSrc)
        {
            if (_varArrayMeta.ContainsKey(dropSrc.Name))
                EmitDropArray(dropSrc.Name);
            else
            {
                var (dropHeadPtr, _) = EmitValue(call.Args[0]);
                EmitDropStruct(dropHeadPtr);
            }
            return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), SuruType.Bool);
        }

        if (call.Name == "exit" && call.Args.Count == 1)
        {
            var (codeVal, _) = EmitValue(call.Args[0]);
            var code32 = _builder.BuildTrunc(codeVal, LLVMTypeRef.Int32, "exit_code");
            _builder.BuildCall2(_exitFnType, _exitFn, new[] { code32 }, "");
            return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), SuruType.Bool);
        }

        if (call.Name == "readFile" && call.Args.Count == 1)
            return EmitReadFile(call.Args[0]);

        if (call.Name == "writeFile" && call.Args.Count == 2)
        {
            EmitWriteFile(call.Args[0], call.Args[1]);
            return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), SuruType.Bool);
        }

        if (_userFunctions.TryGetValue(call.Name, out var fnEntry))
        {
            // Propagate array element types from function parameter metadata.
            if (_functionArrayParamMeta.TryGetValue(call.Name, out var callParamMeta))
            {
                foreach (var (paramIdx, elemType) in callParamMeta)
                {
                    if (paramIdx < call.Args.Count &&
                        call.Args[paramIdx] is VariableReferenceExpression argRef &&
                        !_varArrayMeta.ContainsKey(argRef.Name))
                    {
                        _varArrayMeta[argRef.Name] = elemType;
                    }
                }
            }
            var argVals = call.Args.Select(a => EmitValue(a).Value).ToArray();
            var callResult = _builder.BuildCall2(fnEntry.FnType, fnEntry.Fn, argVals, "");
            if (fnEntry.ReturnType.HasValue)
                return (callResult, fnEntry.ReturnType.Value);
            return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), SuruType.Bool);
        }

        throw new InvalidOperationException($"Unknown function '{call.Name}'");
    }

    private (LLVMValueRef Value, SuruType Type) EmitMethodCall(MethodCallExpression method)
    {
        // Static-method calls on type names: Int64.from(str), Float64.from(str)
        if (method.Receiver is VariableReferenceExpression typeRef &&
            typeRef.Name is "Int64" or "Float64" or "Bool")
        {
            return EmitTypeStaticMethod(typeRef.Name, method.MethodName, method.Args);
        }

        var (receiver, receiverType) = EmitValue(method.Receiver);

        // Array methods
        if (receiverType == SuruType.Array)
            return EmitArrayMethod(receiver, method);

        // String methods
        if (receiverType == SuruType.String)
            return EmitStringMethod(receiver, method);

        // Primitive toString()
        if (method.MethodName == "toString" && method.Args.Count == 0)
            return EmitToString(receiver, receiverType);

        if (method.MethodName == "invert" && method.Args.Count == 0)
        {
            var result = receiverType == SuruType.Float64
                ? _builder.BuildFNeg(receiver, "")
                : _builder.BuildNeg(receiver, "");
            return (result, receiverType);
        }

        if (method.MethodName == "compare")
        {
            var (argVal, _) = EmitValue(method.Args[0]);
            LLVMValueRef gt, lt;
            if (receiverType == SuruType.Float64)
            {
                gt = _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, receiver, argVal, "cmp_gt");
                lt = _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, receiver, argVal, "cmp_lt");
            }
            else
            {
                gt = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, receiver, argVal, "cmp_gt");
                lt = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, receiver, argVal, "cmp_lt");
            }
            var gtExt = _builder.BuildZExt(gt, LLVMTypeRef.Int64, "cmp_gt_ext");
            var ltExt = _builder.BuildZExt(lt, LLVMTypeRef.Int64, "cmp_lt_ext");
            var result = _builder.BuildSub(gtExt, ltExt, "cmp_result");
            return (result, SuruType.Int64);
        }

        var (arg, _) = EmitValue(method.Args[0]);

        var isBoolResult = method.MethodName is "equals" or "lt" or "gt" or "lte" or "gte";

        var value = (method.MethodName, receiverType) switch
        {
            ("add",      SuruType.Int64)   => _builder.BuildAdd(receiver, arg, ""),
            ("add",      SuruType.Float64) => _builder.BuildFAdd(receiver, arg, ""),
            ("take",     SuruType.Int64)   => _builder.BuildSub(receiver, arg, ""),
            ("take",     SuruType.Float64) => _builder.BuildFSub(receiver, arg, ""),
            ("multiply", SuruType.Int64)   => _builder.BuildMul(receiver, arg, ""),
            ("multiply", SuruType.Float64) => _builder.BuildFMul(receiver, arg, ""),
            ("split",    SuruType.Int64)   => _builder.BuildSDiv(receiver, arg, ""),
            ("split",    SuruType.Float64) => _builder.BuildFDiv(receiver, arg, ""),
            ("equals",   SuruType.Bool)    => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,  receiver, arg, ""),
            ("equals",   SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,  receiver, arg, ""),
            ("equals",   SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, receiver, arg, ""),
            ("lt",       SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, receiver, arg, ""),
            ("lt",       SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, receiver, arg, ""),
            ("gt",       SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, receiver, arg, ""),
            ("gt",       SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, receiver, arg, ""),
            ("lte",      SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, receiver, arg, ""),
            ("lte",      SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, receiver, arg, ""),
            ("gte",      SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, receiver, arg, ""),
            ("gte",      SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, receiver, arg, ""),
            _ => throw new InvalidOperationException($"Unknown method '{method.MethodName}' on {receiverType}"),
        };

        return (value, isBoolResult ? SuruType.Bool : receiverType);
    }

    private (LLVMValueRef Value, SuruType Type) EmitArrayMethod(LLVMValueRef headerPtr, MethodCallExpression method)
    {
        var (len, data) = LoadSeqHeader(headerPtr);
        var falseVal = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false);

        switch (method.MethodName)
        {
            case "len":
                return (len, SuruType.Int64);

            case "at":
            {
                var (idxVal, _) = EmitValue(method.Args[0]);
                var slot = _builder.BuildGEP2(LLVMTypeRef.Int64, data, new[] { idxVal }, "arr_slot");
                var raw = _builder.BuildLoad2(LLVMTypeRef.Int64, slot, "arr_raw");
                // Determine element type from receiver variable if available
                SuruType elemType = SuruType.Int64;
                if (method.Receiver is VariableReferenceExpression rv &&
                    _varArrayMeta.TryGetValue(rv.Name, out var et))
                    elemType = et;
                return (FromI64(raw, elemType), elemType);
            }

            case "set":
            {
                var (newVal, newValType) = EmitValue(method.Args[0]);
                var (idxVal, _) = EmitValue(method.Args[1]);
                var slot = _builder.BuildGEP2(LLVMTypeRef.Int64, data, new[] { idxVal }, "arr_slot");
                _builder.BuildStore(ToI64(newVal, newValType), slot);
                return (falseVal, SuruType.Bool);
            }

            case "add":
            {
                var (newElem, newElemType) = EmitValue(method.Args[0]);
                // Infer element type for arrays that were declared empty ([]).
                if (method.Receiver is VariableReferenceExpression addRv &&
                    !_varArrayMeta.ContainsKey(addRv.Name))
                    _varArrayMeta[addRv.Name] = newElemType;
                // realloc data to (len+1)*8
                var one64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
                var newLen = _builder.BuildAdd(len, one64, "new_len");
                var eight = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false);
                var newSize = _builder.BuildMul(newLen, eight, "new_size");
                var newData = _builder.BuildCall2(_reallocFnType, _reallocFn, new[] { data, newSize }, "new_data");
                // Store new element at [len]
                var lastSlot = _builder.BuildGEP2(LLVMTypeRef.Int64, newData, new[] { len }, "last_slot");
                _builder.BuildStore(ToI64(newElem, newElemType), lastSlot);
                // Update header: data ptr and len
                var dataPtrSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 1, "data_slot");
                _builder.BuildStore(newData, dataPtrSlot);
                var lenSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 0, "len_slot");
                _builder.BuildStore(newLen, lenSlot);
                return (falseVal, SuruType.Bool);
            }

            case "equals":
            {
                var (otherHdr, _) = EmitValue(method.Args[0]);
                var (otherLen, otherData) = LoadSeqHeader(otherHdr);
                // Check lengths equal
                var lenEq = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, len, otherLen, "len_eq");
                var fn = _builder.InsertBlock.Parent;
                var eqBlock = fn.AppendBasicBlock("arr_eq_check");
                var mergeBlock = fn.AppendBasicBlock("arr_eq_merge");
                _builder.BuildCondBr(lenEq, eqBlock, mergeBlock);
                // In eqBlock: compare data via memcmp
                _builder.PositionAtEnd(eqBlock);
                var eight = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false);
                var byteCount = _builder.BuildMul(len, eight, "byte_count");
                var cmpResult = _builder.BuildCall2(_strcmpFnType, _strcmpFn,
                    new[] { data, otherData }, "cmp"); // strcmp is wrong for binary data; use memcmp
                // Actually, we need memcmp. Let's build it using memcpy trick — emit memcmp inline.
                // Reuse _memcpyFn as memcmp won't work since _memcpyFn is memcpy.
                // We'll declare memcmp separately. For now, cast: since memcpy & memcmp have different signatures,
                // use a direct call via builder with memcmp.
                // Emit: memcmp returns i32; check == 0
                var memcmpType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [_ptrType, _ptrType, LLVMTypeRef.Int64]);
                var memcmpFn = _llvmModule.GetNamedFunction("memcmp");
                if (memcmpFn.Handle == IntPtr.Zero)
                    memcmpFn = _llvmModule.AddFunction("memcmp", memcmpType);
                var memcmpResult = _builder.BuildCall2(memcmpType, memcmpFn, new[] { data, otherData, byteCount }, "memcmp");
                var zero32 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
                var dataEq = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, memcmpResult, zero32, "data_eq");
                _builder.BuildBr(mergeBlock);
                // Merge: phi(false from lenNeq, dataEq from eqBlock)
                _builder.PositionAtEnd(mergeBlock);
                var phi = _builder.BuildPhi(LLVMTypeRef.Int1, "arr_eq");
                phi.AddIncoming(
                    new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), dataEq },
                    new[] { fn.GetBasicBlocks()[fn.BasicBlocksCount - 3], eqBlock },
                    2);
                return (phi, SuruType.Bool);
            }

            case "slice":
            {
                var (fromVal, _) = EmitValue(method.Args[0]);
                var (toVal, _)   = EmitValue(method.Args[1]);
                var newLen = _builder.BuildSub(toVal, fromVal, "slice_len");
                var eight = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false);
                var byteCount = _builder.BuildMul(newLen, eight, "slice_bytes");
                var srcPtr = _builder.BuildGEP2(LLVMTypeRef.Int64, data, new[] { fromVal }, "slice_src");
                var newData = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { byteCount }, "slice_data");
                _builder.BuildCall2(_memcpyFnType, _memcpyFn, new[] { newData, srcPtr, byteCount }, "");
                var hdrSize = SeqHeaderSize();
                var newHdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "slice_hdr");
                var lSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 0, "");
                _builder.BuildStore(newLen, lSlot);
                var dSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 1, "");
                _builder.BuildStore(newData, dSlot);
                // Propagate element type
                if (method.Receiver is VariableReferenceExpression rv &&
                    _varArrayMeta.TryGetValue(rv.Name, out var et))
                    _pendingArrayMeta = et;
                return (newHdr, SuruType.Array);
            }

            default:
                throw new InvalidOperationException($"Unknown array method '{method.MethodName}'");
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitStringMethod(LLVMValueRef headerPtr, MethodCallExpression method)
    {
        var (len, data) = LoadSeqHeader(headerPtr);

        switch (method.MethodName)
        {
            case "len":
                return (len, SuruType.Int64);

            case "at":
            {
                var (idxVal, _) = EmitValue(method.Args[0]);
                var srcSlot = _builder.BuildGEP2(LLVMTypeRef.Int8, data, new[] { idxVal }, "char_slot");
                var ch = _builder.BuildLoad2(LLVMTypeRef.Int8, srcSlot, "char");
                // Build a 2-byte buffer: [ch, '\0']
                var two = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 2, false);
                var buf = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { two }, "char_buf");
                var zero64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
                var one64  = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
                var buf0 = _builder.BuildGEP2(LLVMTypeRef.Int8, buf, new[] { zero64 }, "char_buf0");
                _builder.BuildStore(ch, buf0);
                var nullByte = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
                var buf1 = _builder.BuildGEP2(LLVMTypeRef.Int8, buf, new[] { one64 }, "char_buf1");
                _builder.BuildStore(nullByte, buf1);
                // Build %suru.Seq header: { len=1, data=buf }
                var hdrSize = SeqHeaderSize();
                var hdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "char_hdr");
                var lenSlot  = _builder.BuildStructGEP2(_seqNodeType, hdr, 0, "char_len_slot");
                _builder.BuildStore(one64, lenSlot);
                var dataSlot = _builder.BuildStructGEP2(_seqNodeType, hdr, 1, "char_data_slot");
                _builder.BuildStore(buf, dataSlot);
                return (hdr, SuruType.String);
            }

            case "equals":
            {
                var (otherHdr, _) = EmitValue(method.Args[0]);
                var (_, otherData) = LoadSeqHeader(otherHdr);
                var cmp = _builder.BuildCall2(_strcmpFnType, _strcmpFn, new[] { data, otherData }, "strcmp");
                var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
                var eq = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmp, zero, "str_eq");
                return (eq, SuruType.Bool);
            }

            case "append":
            {
                var (otherHdr, _) = EmitValue(method.Args[0]);
                var (otherLen, otherData) = LoadSeqHeader(otherHdr);
                var newLen = _builder.BuildAdd(len, otherLen, "app_len");
                var one64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
                var allocLen = _builder.BuildAdd(newLen, one64, "alloc_len"); // +1 for null
                var newBuf = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { allocLen }, "app_buf");
                _builder.BuildCall2(_memcpyFnType, _memcpyFn, new[] { newBuf, data, len }, "");
                var midPtr = _builder.BuildGEP2(LLVMTypeRef.Int8, newBuf, new[] { len }, "mid_ptr");
                _builder.BuildCall2(_memcpyFnType, _memcpyFn, new[] { midPtr, otherData, otherLen }, "");
                // null-terminate
                var nullTermSlot = _builder.BuildGEP2(LLVMTypeRef.Int8, newBuf, new[] { newLen }, "null_slot");
                _builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false), nullTermSlot);
                // Build header
                var hdrSize = SeqHeaderSize();
                var newHdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "app_hdr");
                var lSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 0, "");
                _builder.BuildStore(newLen, lSlot);
                var dSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 1, "");
                _builder.BuildStore(newBuf, dSlot);
                return (newHdr, SuruType.String);
            }

            case "slice":
            {
                var (fromVal, _) = EmitValue(method.Args[0]);
                var (toVal, _)   = EmitValue(method.Args[1]);
                var newLen = _builder.BuildSub(toVal, fromVal, "sslice_len");
                var one64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
                var allocLen = _builder.BuildAdd(newLen, one64, "sslice_alloc");
                var srcPtr = _builder.BuildGEP2(LLVMTypeRef.Int8, data, new[] { fromVal }, "sslice_src");
                var newBuf = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { allocLen }, "sslice_buf");
                _builder.BuildCall2(_memcpyFnType, _memcpyFn, new[] { newBuf, srcPtr, newLen }, "");
                var nullSlot = _builder.BuildGEP2(LLVMTypeRef.Int8, newBuf, new[] { newLen }, "sslice_null");
                _builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false), nullSlot);
                var hdrSize = SeqHeaderSize();
                var newHdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "sslice_hdr");
                var lSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 0, "");
                _builder.BuildStore(newLen, lSlot);
                var dSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 1, "");
                _builder.BuildStore(newBuf, dSlot);
                return (newHdr, SuruType.String);
            }

            case "ord":
            {
                var zero64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
                var firstSlot = _builder.BuildGEP2(LLVMTypeRef.Int8, data, new[] { zero64 }, "ord_slot");
                var byte0 = _builder.BuildLoad2(LLVMTypeRef.Int8, firstSlot, "ord_byte");
                var ord = _builder.BuildZExt(byte0, LLVMTypeRef.Int64, "ord");
                return (ord, SuruType.Int64);
            }

            case "toString":
                return (headerPtr, SuruType.String);

            default:
                throw new InvalidOperationException($"Unknown string method '{method.MethodName}'");
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitTypeStaticMethod(
        string typeName, string methodName, IReadOnlyList<Expression> args)
    {
        switch (typeName, methodName)
        {
            case ("Int64", "from"):
            {
                var (strHdr, _) = EmitValue(args[0]);
                var (_, data) = LoadSeqHeader(strHdr);
                var nullPtr = LLVMValueRef.CreateConstNull(_ptrType);
                var base10 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 10, false);
                var result = _builder.BuildCall2(_strtolFnType, _strtolFn, new[] { data, nullPtr, base10 }, "strtol");
                return (result, SuruType.Int64);
            }
            case ("Float64", "from"):
            {
                var (strHdr, _) = EmitValue(args[0]);
                var (_, data) = LoadSeqHeader(strHdr);
                var nullPtr = LLVMValueRef.CreateConstNull(_ptrType);
                var result = _builder.BuildCall2(_strtodFnType, _strtodFn, new[] { data, nullPtr }, "strtod");
                return (result, SuruType.Float64);
            }
            default:
                throw new InvalidOperationException($"Unknown static method '{typeName}.{methodName}'");
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitToString(LLVMValueRef val, SuruType type)
    {
        var bufSize = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 64, false);
        var buf = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { bufSize }, "tostr_buf");

        switch (type)
        {
            case SuruType.Int64:
            {
                var fmt = _builder.BuildGlobalStringPtr("%lld", "");
                _builder.BuildCall2(_sprintfFnType, _sprintfFn, new LLVMValueRef[] { buf, fmt, val }, "");
                break;
            }
            case SuruType.Float64:
            {
                var fmt = _builder.BuildGlobalStringPtr("%g", "");
                _builder.BuildCall2(_sprintfFnType, _sprintfFn, new LLVMValueRef[] { buf, fmt, val }, "");
                break;
            }
            case SuruType.Bool:
            {
                var trueStr  = _builder.BuildGlobalStringPtr("true", "");
                var falseStr = _builder.BuildGlobalStringPtr("false", "");
                var selected = _builder.BuildSelect(val, trueStr, falseStr, "");
                var fmtS = _builder.BuildGlobalStringPtr("%s", "");
                _builder.BuildCall2(_sprintfFnType, _sprintfFn, new LLVMValueRef[] { buf, fmtS, selected }, "");
                break;
            }
            default:
                throw new InvalidOperationException($"toString not supported for {type}");
        }

        // strlen(buf) to get actual length
        var strLen = _builder.BuildCall2(_strlenFnType, _strlenFn, new[] { buf }, "tostr_len");
        var hdrSize = SeqHeaderSize();
        var hdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "tostr_hdr");
        var lSlot = _builder.BuildStructGEP2(_seqNodeType, hdr, 0, "");
        _builder.BuildStore(strLen, lSlot);
        var dSlot = _builder.BuildStructGEP2(_seqNodeType, hdr, 1, "");
        _builder.BuildStore(buf, dSlot);
        return (hdr, SuruType.String);
    }

    private (LLVMValueRef Value, SuruType Type) EmitCloneArray(string srcVarName)
    {
        var (srcHdr, _) = EmitValue(new VariableReferenceExpression(srcVarName));
        var (len, data) = LoadSeqHeader(srcHdr);
        var eight = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false);
        var byteCount = _builder.BuildMul(len, eight, "clone_bytes");
        var newData = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { byteCount }, "clone_data");
        _builder.BuildCall2(_memcpyFnType, _memcpyFn, new[] { newData, data, byteCount }, "");
        var hdrSize = SeqHeaderSize();
        var newHdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "clone_hdr");
        var lSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 0, "");
        _builder.BuildStore(len, lSlot);
        var dSlot = _builder.BuildStructGEP2(_seqNodeType, newHdr, 1, "");
        _builder.BuildStore(newData, dSlot);
        if (_varArrayMeta.TryGetValue(srcVarName, out var et))
            _pendingArrayMeta = et;
        return (newHdr, SuruType.Array);
    }

    private void EmitDropArray(string varName)
    {
        var (hdr, _) = EmitValue(new VariableReferenceExpression(varName));
        var (_, data) = LoadSeqHeader(hdr);
        _builder.BuildCall2(_freeFnType, _freeFn, new[] { data }, "");
        _builder.BuildCall2(_freeFnType, _freeFn, new[] { hdr }, "");
    }

    // Emit a match used as a statement (arms may have side effects; no value produced).
    private void EmitMatchAsStatement(MatchExpression match)
    {
        var (condVal, condType, patternArms, wildcardArm, armBlocks, wildcardBlock, mergeBlock) =
            EmitMatchTestChain(match);

        for (int i = 0; i < patternArms.Count; i++)
        {
            _builder.PositionAtEnd(armBlocks[i]);
            EmitMatchArmBodyAsStatement(patternArms[i].Body);
            _builder.BuildBr(mergeBlock);
        }

        if (wildcardArm != null)
        {
            _builder.PositionAtEnd(wildcardBlock!.Value);
            EmitMatchArmBodyAsStatement(wildcardArm.Body);
            _builder.BuildBr(mergeBlock);
        }

        _builder.PositionAtEnd(mergeBlock);
    }

    private void EmitMatchArmBodyAsStatement(Expression body)
    {
        if (body is CallExpression { Name: "printLn", Args.Count: 1 } call)
        {
            var (v, t) = EmitValue(call.Args[0]);
            EmitPrintLn(v, t);
        }
        else
        {
            EmitValue(body);
        }
    }

    // Emit a match used as an expression (all arms produce a value; merged via phi).
    private (LLVMValueRef Value, SuruType Type) EmitMatchAsExpression(MatchExpression match)
    {
        var (condVal, condType, patternArms, wildcardArm, armBlocks, wildcardBlock, mergeBlock) =
            EmitMatchTestChain(match);

        var incoming = new List<(LLVMValueRef Value, LLVMBasicBlockRef Block)>();
        SuruType? resultType = null;

        for (int i = 0; i < patternArms.Count; i++)
        {
            _builder.PositionAtEnd(armBlocks[i]);
            var (armVal, armType) = EmitValue(patternArms[i].Body);
            resultType ??= armType;
            incoming.Add((armVal, _builder.InsertBlock));
            _builder.BuildBr(mergeBlock);
        }

        if (wildcardArm != null)
        {
            _builder.PositionAtEnd(wildcardBlock!.Value);
            var (armVal, armType) = EmitValue(wildcardArm.Body);
            resultType ??= armType;
            incoming.Add((armVal, _builder.InsertBlock));
            _builder.BuildBr(mergeBlock);
        }

        _builder.PositionAtEnd(mergeBlock);

        if (resultType.HasValue && incoming.Count > 0)
        {
            var phi = _builder.BuildPhi(LlvmTypeFor(resultType.Value), "match_result");
            phi.AddIncoming(
                incoming.Select(x => x.Value).ToArray(),
                incoming.Select(x => x.Block).ToArray(),
                (uint)incoming.Count);
            return (phi, resultType.Value);
        }

        return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), SuruType.Bool);
    }

    // Build the test-chain branching structure for a match expression.
    // Returns the evaluated condition, separated arm lists, and the pre-created LLVM blocks.
    // The builder is left with a terminator in the entry block; callers must PositionAtEnd each arm block.
    private (
        LLVMValueRef CondVal,
        SuruType CondType,
        List<MatchArm> PatternArms,
        MatchArm? WildcardArm,
        LLVMBasicBlockRef[] ArmBlocks,
        LLVMBasicBlockRef? WildcardBlock,
        LLVMBasicBlockRef MergeBlock
    ) EmitMatchTestChain(MatchExpression match)
    {
        var (condVal, condType) = EmitValue(match.Condition);
        var fn = _builder.InsertBlock.Parent;

        var patternArms = match.Arms.Where(a => a.Pattern != null).ToList();
        var wildcardArm = match.Arms.FirstOrDefault(a => a.Pattern == null);

        var armBlocks = patternArms.Select((_, i) => fn.AppendBasicBlock($"match_arm_{i}")).ToArray();
        LLVMBasicBlockRef? wildcardBlock = wildcardArm != null ? fn.AppendBasicBlock("match_wildcard") : null;
        var mergeBlock = fn.AppendBasicBlock("match_merge");

        var missBlock = wildcardBlock ?? mergeBlock;

        for (int i = 0; i < patternArms.Count; i++)
        {
            LLVMValueRef cmp;
            if (condType == SuruType.String)
            {
                var (patternHdr, _) = EmitValue(patternArms[i].Pattern!);
                var (_, condData)    = LoadSeqHeader(condVal);
                var (_, patternData) = LoadSeqHeader(patternHdr);
                var strcmpResult = _builder.BuildCall2(_strcmpFnType, _strcmpFn,
                    new[] { condData, patternData }, "match_strcmp");
                var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
                cmp = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, strcmpResult, zero, "match_str_eq");
            }
            else
            {
                var (patternVal, _) = EmitValue(patternArms[i].Pattern!);
                cmp = condType switch
                {
                    SuruType.Float64 => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, condVal, patternVal, ""),
                    _                => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,     condVal, patternVal, ""),
                };
            }

            LLVMBasicBlockRef elseBlock;
            if (i + 1 < patternArms.Count)
            {
                elseBlock = fn.AppendBasicBlock($"match_test_{i + 1}");
            }
            else
            {
                elseBlock = missBlock;
            }

            _builder.BuildCondBr(cmp, armBlocks[i], elseBlock);

            if (i + 1 < patternArms.Count)
                _builder.PositionAtEnd(elseBlock);
        }

        if (patternArms.Count == 0)
            _builder.BuildBr(missBlock);

        return (condVal, condType, patternArms, wildcardArm, armBlocks, wildcardBlock, mergeBlock);
    }

    private void EmitPrintLn(LLVMValueRef val, SuruType type)
    {
        switch (type)
        {
            case SuruType.Bool:
            {
                var fmt      = _builder.BuildGlobalStringPtr("%s\n", "");
                var trueStr  = _builder.BuildGlobalStringPtr("true", "");
                var falseStr = _builder.BuildGlobalStringPtr("false", "");
                var selected = _builder.BuildSelect(val, trueStr, falseStr, "");
                _builder.BuildCall2(_printfType, _printfFn, new LLVMValueRef[] { fmt, selected }, "");
                break;
            }
            case SuruType.Int64:
            {
                var fmt = _builder.BuildGlobalStringPtr("%lld\n", "");
                _builder.BuildCall2(_printfType, _printfFn, new LLVMValueRef[] { fmt, val }, "");
                break;
            }
            case SuruType.Float64:
            {
                var fmt = _builder.BuildGlobalStringPtr("%g\n", "");
                _builder.BuildCall2(_printfType, _printfFn, new LLVMValueRef[] { fmt, val }, "");
                break;
            }
            case SuruType.String:
            {
                var fmt = _builder.BuildGlobalStringPtr("%s\n", "");
                var dataPtr = _builder.BuildStructGEP2(_seqNodeType, val, 1, "str_data_slot");
                var data = _builder.BuildLoad2(_ptrType, dataPtr, "str_data");
                _builder.BuildCall2(_printfType, _printfFn, new LLVMValueRef[] { fmt, data }, "");
                break;
            }
        }
    }

    private LLVMTypeRef LlvmTypeFor(SuruType type) => type switch
    {
        SuruType.Bool    => LLVMTypeRef.Int1,
        SuruType.Int64   => LLVMTypeRef.Int64,
        SuruType.Float64 => LLVMTypeRef.Double,
        SuruType.Struct  => _ptrType,
        SuruType.Array   => _ptrType,
        SuruType.String  => _ptrType,
        _ => throw new InvalidOperationException($"No LLVM type for {type}"),
    };

    // ── Struct helpers ────────────────────────────────────────────────────────

    // Emit the suru_find_field helper once.  It traverses the linked list at
    // runtime comparing the stored name pointer via strcmp and returns the node.
    private void EmitFindFieldHelper()
    {
        _findFieldFnType = LLVMTypeRef.CreateFunction(_ptrType, [_ptrType, _ptrType]);
        _findFieldFn = _llvmModule.AddFunction("suru_find_field", _findFieldFnType);
        _findFieldFn.Linkage = LLVMLinkage.LLVMInternalLinkage;

        var entryBB    = _findFieldFn.AppendBasicBlock("entry");
        var loopBB     = _findFieldFn.AppendBasicBlock("loop");
        var continueBB = _findFieldFn.AppendBasicBlock("continue");
        var doneBB     = _findFieldFn.AppendBasicBlock("done");

        var head = _findFieldFn.GetParam(0);
        var name = _findFieldFn.GetParam(1);

        _builder.PositionAtEnd(entryBB);
        _builder.BuildBr(loopBB);

        _builder.PositionAtEnd(loopBB);
        var node = _builder.BuildPhi(_ptrType, "node");
        node.AddIncoming(new[] { head }, new[] { entryBB }, 1);
        var nameSlot   = _builder.BuildStructGEP2(_fieldNodeType, node, 0, "name_slot");
        var storedName = _builder.BuildLoad2(_ptrType, nameSlot, "stored_name");
        var cmp = _builder.BuildCall2(_strcmpFnType, _strcmpFn, new[] { storedName, name }, "ff_cmp");
        var zero32 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false);
        var found = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cmp, zero32, "found");
        _builder.BuildCondBr(found, doneBB, continueBB);

        _builder.PositionAtEnd(continueBB);
        var nextSlot = _builder.BuildStructGEP2(_fieldNodeType, node, 3, "next_slot");
        var next = _builder.BuildLoad2(_ptrType, nextSlot, "next");
        node.AddIncoming(new[] { next }, new[] { continueBB }, 1);
        _builder.BuildBr(loopBB);

        _builder.PositionAtEnd(doneBB);
        _builder.BuildRet(node);
    }

    // Emit a runtime call to suru_find_field for the named field.
    private LLVMValueRef FindFieldNode(LLVMValueRef headPtr, string fieldName)
    {
        var namePtr = _builder.BuildGlobalStringPtr(fieldName, $"fldname_{fieldName}");
        return _builder.BuildCall2(_findFieldFnType, _findFieldFn, new[] { headPtr, namePtr }, "fld_node");
    }

    private (LLVMValueRef Value, SuruType Type) EmitStructLiteral(StructLiteralExpression lit)
    {
        LLVMValueRef prevNodePtr = LLVMValueRef.CreateConstNull(_ptrType);

        // Build nodes in reverse so each node's 'next' points to the already-built tail.
        for (int i = lit.Fields.Count - 1; i >= 0; i--)
        {
            var (fieldName, fieldExpr) = lit.Fields[i];
            var (fieldVal, fieldType) = EmitValue(fieldExpr);

            var size = FieldNodeSize();
            var nodePtr = _builder.BuildCall2(_mallocFnType, _mallocFn, new LLVMValueRef[] { size }, $"field_{fieldName}");

            // [0] name ptr
            var namePtr = _builder.BuildGlobalStringPtr(fieldName, $"fname_{fieldName}");
            var nameSlot = _builder.BuildStructGEP2(_fieldNodeType, nodePtr, 0, "name_slot");
            _builder.BuildStore(namePtr, nameSlot);

            // [1] tag
            int tag = fieldType switch { SuruType.Bool => 0, SuruType.Int64 => 1, SuruType.Float64 => 2, _ => 3 };
            var tagSlot = _builder.BuildStructGEP2(_fieldNodeType, nodePtr, 1, "tag_slot");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)tag, false), tagSlot);

            // [2] value as i64
            var valSlot = _builder.BuildStructGEP2(_fieldNodeType, nodePtr, 2, "val_slot");
            _builder.BuildStore(ToI64(fieldVal, fieldType), valSlot);

            // [3] next
            var nextSlot = _builder.BuildStructGEP2(_fieldNodeType, nodePtr, 3, "next_slot");
            _builder.BuildStore(prevNodePtr, nextSlot);

            prevNodePtr = nodePtr;
        }

        return (prevNodePtr, SuruType.Struct);
    }

    private LLVMValueRef LoadStructPtr(Expression receiver)
    {
        var (ptr, _) = EmitValue(receiver);
        return ptr;
    }

    private LLVMValueRef ToI64(LLVMValueRef val, SuruType type) => type switch
    {
        SuruType.Bool    => _builder.BuildZExt(val, LLVMTypeRef.Int64, ""),
        SuruType.Int64   => val,
        SuruType.Float64 => _builder.BuildBitCast(val, LLVMTypeRef.Int64, ""),
        _                => _builder.BuildPtrToInt(val, LLVMTypeRef.Int64, ""),  // Struct, Array, String: store ptr as i64
    };

    private LLVMValueRef FromI64(LLVMValueRef raw, SuruType type) => type switch
    {
        SuruType.Bool    => _builder.BuildTrunc(raw, LLVMTypeRef.Int1, ""),
        SuruType.Int64   => raw,
        SuruType.Float64 => _builder.BuildBitCast(raw, LLVMTypeRef.Double, ""),
        _                => _builder.BuildIntToPtr(raw, _ptrType, ""),  // Struct, Array, String: restore ptr
    };

    // Clone a struct by traversing its linked list at runtime.
    private (LLVMValueRef Value, SuruType Type) EmitCloneStruct(LLVMValueRef headPtr)
    {
        var fn = _builder.InsertBlock.Parent;
        var srcAlloca     = _builder.BuildAlloca(_ptrType, "csrc");
        var prevAlloca    = _builder.BuildAlloca(_ptrType, "cprev");
        var newHeadAlloca = _builder.BuildAlloca(_ptrType, "chead");
        var nullPtr = LLVMValueRef.CreateConstNull(_ptrType);
        _builder.BuildStore(headPtr, srcAlloca);
        _builder.BuildStore(nullPtr, prevAlloca);
        _builder.BuildStore(nullPtr, newHeadAlloca);

        var condBB  = fn.AppendBasicBlock("clone_cond");
        var bodyBB  = fn.AppendBasicBlock("clone_body");
        var wireBB  = fn.AppendBasicBlock("clone_wire");
        var headBB  = fn.AppendBasicBlock("clone_sethead");
        var afterBB = fn.AppendBasicBlock("clone_after");
        var doneBB  = fn.AppendBasicBlock("clone_done");

        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(condBB);
        var src = _builder.BuildLoad2(_ptrType, srcAlloca, "csrc_v");
        var isNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, src, nullPtr, "cnull");
        _builder.BuildCondBr(isNull, doneBB, bodyBB);

        _builder.PositionAtEnd(bodyBB);
        var size = FieldNodeSize();
        var newNode = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { size }, "cnode");
        // Copy slots 0-2 (name, tag, val)
        for (uint slot = 0; slot <= 2; slot++)
        {
            var srcSlot = _builder.BuildStructGEP2(_fieldNodeType, src, slot, "");
            var dstSlot = _builder.BuildStructGEP2(_fieldNodeType, newNode, slot, "");
            LLVMTypeRef slotType = slot switch { 0 => _ptrType, 1 => LLVMTypeRef.Int32, _ => LLVMTypeRef.Int64 };
            _builder.BuildStore(_builder.BuildLoad2(slotType, srcSlot, ""), dstSlot);
        }
        var newNextSlot = _builder.BuildStructGEP2(_fieldNodeType, newNode, 3, "cnext");
        _builder.BuildStore(nullPtr, newNextSlot);
        var prev = _builder.BuildLoad2(_ptrType, prevAlloca, "cprev_v");
        var isFirst = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, prev, nullPtr, "cfirst");
        _builder.BuildCondBr(isFirst, headBB, wireBB);

        _builder.PositionAtEnd(headBB);
        _builder.BuildStore(newNode, newHeadAlloca);
        _builder.BuildBr(afterBB);

        _builder.PositionAtEnd(wireBB);
        var prevNextSlot = _builder.BuildStructGEP2(_fieldNodeType, prev, 3, "cprev_next");
        _builder.BuildStore(newNode, prevNextSlot);
        _builder.BuildBr(afterBB);

        _builder.PositionAtEnd(afterBB);
        _builder.BuildStore(newNode, prevAlloca);
        var srcNextSlot = _builder.BuildStructGEP2(_fieldNodeType, src, 3, "csrcnext");
        var srcNext = _builder.BuildLoad2(_ptrType, srcNextSlot, "csrcnext_v");
        _builder.BuildStore(srcNext, srcAlloca);
        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(doneBB);
        var newHead = _builder.BuildLoad2(_ptrType, newHeadAlloca, "clone_result");
        return (newHead, SuruType.Struct);
    }

    // Free all nodes of a struct linked list at runtime.
    private void EmitDropStruct(LLVMValueRef headPtr)
    {
        var fn = _builder.InsertBlock.Parent;
        var srcAlloca = _builder.BuildAlloca(_ptrType, "dsrc");
        _builder.BuildStore(headPtr, srcAlloca);

        var condBB = fn.AppendBasicBlock("drop_cond");
        var bodyBB = fn.AppendBasicBlock("drop_body");
        var doneBB = fn.AppendBasicBlock("drop_done");
        var nullPtr = LLVMValueRef.CreateConstNull(_ptrType);

        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(condBB);
        var src = _builder.BuildLoad2(_ptrType, srcAlloca, "dsrc_v");
        var isNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, src, nullPtr, "dnull");
        _builder.BuildCondBr(isNull, doneBB, bodyBB);

        _builder.PositionAtEnd(bodyBB);
        var nextSlot = _builder.BuildStructGEP2(_fieldNodeType, src, 3, "dnext");
        var next = _builder.BuildLoad2(_ptrType, nextSlot, "dnext_v");
        _builder.BuildStore(next, srcAlloca);
        _builder.BuildCall2(_freeFnType, _freeFn, new[] { src }, "");
        _builder.BuildBr(condBB);

        _builder.PositionAtEnd(doneBB);
    }

    // ── Array helpers ─────────────────────────────────────────────────────────

    private SuruType? _pendingArrayMeta;

    private void PropagateArrayMeta(string varName, Expression value)
    {
        switch (value)
        {
            case ArrayLiteralExpression when _pendingArrayMeta.HasValue:
                _varArrayMeta[varName] = _pendingArrayMeta.Value;
                _pendingArrayMeta = null;
                break;
            case VariableReferenceExpression v when _varArrayMeta.TryGetValue(v.Name, out var et):
                _varArrayMeta[varName] = et;
                break;
            case CallExpression { Name: "clone" } when _pendingArrayMeta.HasValue:
                _varArrayMeta[varName] = _pendingArrayMeta.Value;
                _pendingArrayMeta = null;
                break;
            case CallExpression call when _functionReturnArrayMeta.TryGetValue(call.Name, out var retEt):
                _varArrayMeta[varName] = retEt;
                break;
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitArrayLiteral(ArrayLiteralExpression lit)
    {
        var headerSize = SeqHeaderSize();
        var headerPtr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { headerSize }, "arr_hdr");

        SuruType elemType = SuruType.Int64; // default for empty array
        LLVMValueRef dataPtr;

        if (lit.Elements.Count == 0)
        {
            dataPtr = LLVMValueRef.CreateConstNull(_ptrType);
        }
        else
        {
            var (firstVal, firstType) = EmitValue(lit.Elements[0]);
            elemType = firstType;

            var count = (ulong)lit.Elements.Count;
            var dataSize = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, count * 8, false);
            dataPtr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { dataSize }, "arr_data");

            // Store first element
            var slot0 = _builder.BuildGEP2(LLVMTypeRef.Int64, dataPtr, new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false) }, "");
            _builder.BuildStore(ToI64(firstVal, firstType), slot0);

            // Store remaining elements
            for (int i = 1; i < lit.Elements.Count; i++)
            {
                var (elemVal, elemValType) = EmitValue(lit.Elements[i]);
                var idx = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i, false);
                var slot = _builder.BuildGEP2(LLVMTypeRef.Int64, dataPtr, new[] { idx }, "");
                _builder.BuildStore(ToI64(elemVal, elemValType), slot);
            }
        }

        // Store len
        var lenSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 0, "arr_len_slot");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)lit.Elements.Count, false), lenSlot);
        // Store data ptr
        var dataPtrSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 1, "arr_data_slot");
        _builder.BuildStore(dataPtr, dataPtrSlot);

        _pendingArrayMeta = elemType;
        return (headerPtr, SuruType.Array);
    }

    private (LLVMValueRef Value, SuruType Type) EmitStringLiteral(StringLiteralExpression lit)
    {
        var staticData = _builder.BuildGlobalStringPtr(lit.Value, "str_data");
        var headerSize = SeqHeaderSize();
        var headerPtr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { headerSize }, "str_hdr");

        var lenSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 0, "str_len_slot");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)lit.Value.Length, false), lenSlot);
        var dataPtrSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 1, "str_data_slot");
        _builder.BuildStore(staticData, dataPtrSlot);

        return (headerPtr, SuruType.String);
    }

    // Helper: load len and data ptr from a %suru.Seq header
    private (LLVMValueRef Len, LLVMValueRef Data) LoadSeqHeader(LLVMValueRef headerPtr)
    {
        var lenSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 0, "seq_len_slot");
        var len = _builder.BuildLoad2(LLVMTypeRef.Int64, lenSlot, "seq_len");
        var dataPtrSlot = _builder.BuildStructGEP2(_seqNodeType, headerPtr, 1, "seq_data_slot");
        var data = _builder.BuildLoad2(_ptrType, dataPtrSlot, "seq_data");
        return (len, data);
    }

    // sizeof(%suru.Seq) via GEP-from-null trick
    private LLVMValueRef SeqHeaderSize()
    {
        var nullPtr = LLVMValueRef.CreateConstNull(_ptrType);
        var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
        var gep = _builder.BuildGEP2(_seqNodeType, nullPtr, new[] { one }, "");
        return _builder.BuildPtrToInt(gep, LLVMTypeRef.Int64, "seq_size");
    }

    // ── sizeof(%suru.Field) via GEP-from-null trick: getelementptr(..., null, 1) → ptrtoint
    private LLVMValueRef FieldNodeSize()
    {
        var nullPtr = LLVMValueRef.CreateConstNull(_ptrType);
        var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
        var gep = _builder.BuildGEP2(_fieldNodeType, nullPtr, new LLVMValueRef[] { one }, "");
        return _builder.BuildPtrToInt(gep, LLVMTypeRef.Int64, "field_size");
    }

    // ── Built-in I/O ──────────────────────────────────────────────────────────

    private (LLVMValueRef Value, SuruType Type) EmitReadFile(Expression pathArg)
    {
        var (pathHdr, _) = EmitValue(pathArg);
        var (_, pathData) = LoadSeqHeader(pathHdr);

        var modeR = _builder.BuildGlobalStringPtr("r", "mode_r");
        var file = _builder.BuildCall2(_fopenFnType, _fopenFn, new[] { pathData, modeR }, "rf_file");

        // fseek(file, 0, SEEK_END=2)
        var zero64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
        var seekEnd = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 2, false);
        _builder.BuildCall2(_fseekFnType, _fseekFn, new[] { file, zero64, seekEnd }, "");

        // size = ftell(file)
        var size = _builder.BuildCall2(_ftellFnType, _ftellFn, new[] { file }, "rf_size");

        // rewind(file)
        _builder.BuildCall2(_rewindFnType, _rewindFn, new[] { file }, "");

        // buf = malloc(size + 1)
        var one64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
        var bufSize = _builder.BuildAdd(size, one64, "rf_bufsize");
        var buf = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { bufSize }, "rf_buf");

        // fread(buf, 1, size, file)
        _builder.BuildCall2(_freadFnType, _freadFn, new[] { buf, one64, size, file }, "");

        // buf[size] = '\0'
        var nullByte = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
        var nullSlot = _builder.BuildGEP2(LLVMTypeRef.Int8, buf, new[] { size }, "rf_null_slot");
        _builder.BuildStore(nullByte, nullSlot);

        // fclose(file)
        _builder.BuildCall2(_fcloseFnType, _fcloseFn, new[] { file }, "");

        // Build %suru.Seq header: { len=size, data=buf }
        var hdrSize = SeqHeaderSize();
        var hdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { hdrSize }, "rf_hdr");
        var lenSlot = _builder.BuildStructGEP2(_seqNodeType, hdr, 0, "rf_len_slot");
        _builder.BuildStore(size, lenSlot);
        var dataSlot = _builder.BuildStructGEP2(_seqNodeType, hdr, 1, "rf_data_slot");
        _builder.BuildStore(buf, dataSlot);

        return (hdr, SuruType.String);
    }

    private void EmitWriteFile(Expression pathArg, Expression contentArg)
    {
        var (pathHdr, _) = EmitValue(pathArg);
        var (_, pathData) = LoadSeqHeader(pathHdr);
        var (contentHdr, _) = EmitValue(contentArg);
        var (contentLen, contentData) = LoadSeqHeader(contentHdr);

        var modeW = _builder.BuildGlobalStringPtr("w", "mode_w");
        var file = _builder.BuildCall2(_fopenFnType, _fopenFn, new[] { pathData, modeW }, "wf_file");

        var one64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
        _builder.BuildCall2(_fwriteFnType, _fwriteFn, new[] { contentData, one64, contentLen, file }, "");

        _builder.BuildCall2(_fcloseFnType, _fcloseFn, new[] { file }, "");
    }

    // Emit C int main(int argc, char** argv) that builds a Suru Array<String> and calls suru_main.
    private void EmitMainWrapper()
    {
        var mainType = LLVMTypeRef.CreateFunction(
            LLVMTypeRef.Int32,
            new[] { LLVMTypeRef.Int32, _ptrType });
        var mainFn = _llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        var entryBlock  = mainFn.AppendBasicBlock("entry");
        var loopCond    = mainFn.AppendBasicBlock("loop_cond");
        var loopBody    = mainFn.AppendBasicBlock("loop_body");
        var loopExit    = mainFn.AppendBasicBlock("loop_exit");

        _builder.PositionAtEnd(entryBlock);

        var argc   = mainFn.GetParam(0);  // i32
        var argv   = mainFn.GetParam(1);  // ptr (char**)
        var argc64 = _builder.BuildZExt(argc, LLVMTypeRef.Int64, "argc64");

        // data = malloc(argc64 * 8)
        var eight = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 8, false);
        var dataBytes = _builder.BuildMul(argc64, eight, "data_bytes");
        var data = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { dataBytes }, "argv_data");

        _builder.BuildBr(loopCond);

        // Loop condition: i < argc64
        _builder.PositionAtEnd(loopCond);
        var i = _builder.BuildPhi(LLVMTypeRef.Int64, "i");
        var zero64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
        i.AddIncoming(new[] { zero64 }, new[] { entryBlock }, 1);
        var cond = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, i, argc64, "loop_cond");
        _builder.BuildCondBr(cond, loopBody, loopExit);

        // Loop body: build a suru String header for argv[i]
        _builder.PositionAtEnd(loopBody);
        var argvSlot = _builder.BuildGEP2(_ptrType, argv, new[] { i }, "argv_slot");
        var cstr = _builder.BuildLoad2(_ptrType, argvSlot, "cstr");
        var slen = _builder.BuildCall2(_strlenFnType, _strlenFn, new[] { cstr }, "slen");
        var shdrSize = SeqHeaderSize();
        var shdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { shdrSize }, "shdr");
        var slenSlot = _builder.BuildStructGEP2(_seqNodeType, shdr, 0, "slen_slot");
        _builder.BuildStore(slen, slenSlot);
        var sdataSlot = _builder.BuildStructGEP2(_seqNodeType, shdr, 1, "sdata_slot");
        _builder.BuildStore(cstr, sdataSlot);
        // Store header pointer (as i64) into data[i]
        var shdrInt = _builder.BuildPtrToInt(shdr, LLVMTypeRef.Int64, "shdr_int");
        var dataSlot = _builder.BuildGEP2(LLVMTypeRef.Int64, data, new[] { i }, "data_slot");
        _builder.BuildStore(shdrInt, dataSlot);
        var one64 = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 1, false);
        var iNext = _builder.BuildAdd(i, one64, "i_next");
        i.AddIncoming(new[] { iNext }, new[] { loopBody }, 1);
        _builder.BuildBr(loopCond);

        // After loop: build the Array<String> header and call suru_main
        _builder.PositionAtEnd(loopExit);
        var argsHdrSize = SeqHeaderSize();
        var argsHdr = _builder.BuildCall2(_mallocFnType, _mallocFn, new[] { argsHdrSize }, "args_hdr");
        var argsLenSlot = _builder.BuildStructGEP2(_seqNodeType, argsHdr, 0, "args_len_slot");
        _builder.BuildStore(argc64, argsLenSlot);
        var argsDataSlot = _builder.BuildStructGEP2(_seqNodeType, argsHdr, 1, "args_data_slot");
        _builder.BuildStore(data, argsDataSlot);

        var (suruMainFn, suruMainFnType, _) = _userFunctions["main"];
        var result = _builder.BuildCall2(suruMainFnType, suruMainFn, new[] { argsHdr }, "result");
        var result32 = _builder.BuildTrunc(result, LLVMTypeRef.Int32, "result32");
        _builder.BuildRet(result32);
    }
}
