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
    private readonly Dictionary<string, (LLVMValueRef Alloca, SuruType Type)> _vars = new();
    private readonly Dictionary<string, (LLVMValueRef Fn, LLVMTypeRef FnType, SuruType? ReturnType)> _userFunctions = new();
    // Struct field metadata: variable name → ordered list of (fieldName, fieldType)
    private readonly Dictionary<string, List<(string Name, SuruType Type)>> _varStructMeta = new();
    // Array element type metadata: variable name → element SuruType
    private readonly Dictionary<string, SuruType> _varArrayMeta = new();

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

        // %suru.Seq = type { i64, ptr } — shared layout for Array and String headers
        gen._seqNodeType = context.CreateNamedStruct("suru.Seq");
        gen._seqNodeType.StructSetBody([LLVMTypeRef.Int64, ptrType], false);

        // Pass 1: declare all user-defined functions (enables forward references and recursion).
        foreach (var stmt in module.Statements)
            if (stmt is FunctionDeclaration fn) gen.DeclareFunction(fn);

        // Pass 2: emit bodies of user-defined functions.
        foreach (var stmt in module.Statements)
            if (stmt is FunctionDeclaration fn) gen.EmitFunctionBody(fn);

        // Pass 3: emit main() for top-level statements.
        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        var entry = mainFn.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);

        foreach (var stmt in module.Statements)
            if (stmt is not FunctionDeclaration) gen.EmitStmt(stmt);

        builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
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

        var fnType = LLVMTypeRef.CreateFunction(llvmReturnType, paramLlvmTypes);
        var llvmFn = _llvmModule.AddFunction(fn.Name, fnType);
        llvmFn.Linkage = LLVMLinkage.LLVMInternalLinkage;

        _userFunctions[fn.Name] = (llvmFn, fnType, returnSuruType);
    }

    private void EmitFunctionBody(FunctionDeclaration fn)
    {
        var (llvmFn, _, _) = _userFunctions[fn.Name];

        var entry = llvmFn.AppendBasicBlock("entry");
        _builder.PositionAtEnd(entry);

        var outerVars = new Dictionary<string, (LLVMValueRef Alloca, SuruType Type)>(_vars);
        var outerStructMeta = new Dictionary<string, List<(string Name, SuruType Type)>>(_varStructMeta);
        var outerArrayMeta = new Dictionary<string, SuruType>(_varArrayMeta);
        _vars.Clear();
        _varStructMeta.Clear();
        _varArrayMeta.Clear();

        for (int i = 0; i < fn.Parameters.Count; i++)
        {
            var p = fn.Parameters[i];
            var paramType = ResolveTypeName(p.TypeName)!.Value;
            var alloca = _builder.BuildAlloca(LlvmTypeFor(paramType), p.Name);
            _builder.BuildStore(llvmFn.GetParam((uint)i), alloca);
            _vars[p.Name] = (alloca, paramType);
        }

        foreach (var stmt in fn.Body)
            EmitStmt(stmt);

        if (_builder.InsertBlock.Terminator.Handle == IntPtr.Zero && fn.ReturnTypeName == "void")
            _builder.BuildRetVoid();

        _vars.Clear();
        _varStructMeta.Clear();
        _varArrayMeta.Clear();
        foreach (var kv in outerVars) _vars[kv.Key] = kv.Value;
        foreach (var kv in outerStructMeta) _varStructMeta[kv.Key] = kv.Value;
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
                if (letType == SuruType.Struct)
                    PropagateStructMeta(let.Name, let.Value);
                if (letType == SuruType.Array)
                    PropagateArrayMeta(let.Name, let.Value);
                break;

            case FieldAssignmentStatement fieldAssign:
            {
                var receiverPtr = LoadStructPtr(fieldAssign.Receiver);
                var fieldIdx = GetFieldIndex(fieldAssign.Receiver, fieldAssign.FieldName);
                var fieldType = GetFieldType(fieldAssign.Receiver, fieldAssign.FieldName);
                var fieldNode = NavigateToNode(receiverPtr, fieldIdx);
                var valSlot = _builder.BuildStructGEP2(_fieldNodeType, fieldNode, 2, "val_slot");
                var (assignedVal, _) = EmitValue(fieldAssign.Value);
                var rawVal = ToI64(assignedVal, fieldType);
                _builder.BuildStore(rawVal, valSlot);
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
                var nodePtr = LoadStructPtr(fa.Receiver);
                var fieldIndex = GetFieldIndex(fa.Receiver, fa.FieldName);
                var fieldType = GetFieldType(fa.Receiver, fa.FieldName);
                var targetNode = NavigateToNode(nodePtr, fieldIndex);
                var valSlot = _builder.BuildStructGEP2(_fieldNodeType, targetNode, 2, "val_slot");
                var rawVal = _builder.BuildLoad2(LLVMTypeRef.Int64, valSlot, "raw_val");
                var typedVal = FromI64(rawVal, fieldType);
                return (typedVal, fieldType);
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
            return EmitClone(cloneSrc.Name);
        }

        if (call.Name == "drop" && call.Args.Count == 1 &&
            call.Args[0] is VariableReferenceExpression dropSrc)
        {
            if (_varArrayMeta.ContainsKey(dropSrc.Name))
                EmitDropArray(dropSrc.Name);
            else
                EmitDrop(dropSrc.Name);
            return (LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false), SuruType.Bool);
        }

        if (_userFunctions.TryGetValue(call.Name, out var fnEntry))
        {
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

        var (arg, _) = EmitValue(method.Args[0]);

        var isBoolResult = method.MethodName is "equals" or "lessThan";

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
            ("equals",   SuruType.Bool)    => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, receiver, arg, ""),
            ("equals",   SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, receiver, arg, ""),
            ("equals",   SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, receiver, arg, ""),
            ("lessThan", SuruType.Int64)   => _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, receiver, arg, ""),
            ("lessThan", SuruType.Float64) => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, receiver, arg, ""),
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
                var slot = _builder.BuildGEP2(LLVMTypeRef.Int8, data, new[] { idxVal }, "char_slot");
                var ch = _builder.BuildLoad2(LLVMTypeRef.Int8, slot, "char");
                var extended = _builder.BuildZExt(ch, LLVMTypeRef.Int64, "char_i64");
                return (extended, SuruType.Int64);
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
            var (patternVal, _) = EmitValue(patternArms[i].Pattern!);

            LLVMValueRef cmp = condType switch
            {
                SuruType.Float64 => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, condVal, patternVal, ""),
                _                => _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,     condVal, patternVal, ""),
            };

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

    private void PropagateStructMeta(string varName, Expression value)
    {
        switch (value)
        {
            case StructLiteralExpression when _pendingStructMeta != null:
                _varStructMeta[varName] = _pendingStructMeta;
                _pendingStructMeta = null;
                break;
            case VariableReferenceExpression v when _varStructMeta.TryGetValue(v.Name, out var meta):
                _varStructMeta[varName] = new List<(string, SuruType)>(meta);
                break;
            case CallExpression { Name: "clone" } when _pendingStructMeta != null:
                _varStructMeta[varName] = _pendingStructMeta;
                _pendingStructMeta = null;
                break;
        }
    }

    private List<(string Name, SuruType Type)>? _pendingStructMeta;

    private (LLVMValueRef Value, SuruType Type) EmitStructLiteral(StructLiteralExpression lit)
    {
        LLVMValueRef prevNodePtr = LLVMValueRef.CreateConstNull(_ptrType);
        var fieldMeta = new List<(string Name, SuruType Type)>();

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
            fieldMeta.Insert(0, (fieldName, fieldType));
        }

        _pendingStructMeta = fieldMeta;
        return (prevNodePtr, SuruType.Struct);
    }

    private LLVMValueRef LoadStructPtr(Expression receiver)
    {
        var (ptr, _) = EmitValue(receiver);
        return ptr;
    }

    private int GetFieldIndex(Expression receiver, string fieldName)
    {
        if (receiver is VariableReferenceExpression v && _varStructMeta.TryGetValue(v.Name, out var meta))
            return meta.FindIndex(f => f.Name == fieldName);
        throw new InvalidOperationException($"No struct metadata for field '{fieldName}'");
    }

    private SuruType GetFieldType(Expression receiver, string fieldName)
    {
        if (receiver is VariableReferenceExpression v && _varStructMeta.TryGetValue(v.Name, out var meta))
            return meta.First(f => f.Name == fieldName).Type;
        throw new InvalidOperationException($"No struct metadata for field '{fieldName}'");
    }

    private LLVMValueRef NavigateToNode(LLVMValueRef headPtr, int fieldIndex)
    {
        var nodePtr = headPtr;
        for (int i = 0; i < fieldIndex; i++)
        {
            var nextSlot = _builder.BuildStructGEP2(_fieldNodeType, nodePtr, 3, "next_slot");
            nodePtr = _builder.BuildLoad2(_ptrType, nextSlot, "next");
        }
        return nodePtr;
    }

    private LLVMValueRef ToI64(LLVMValueRef val, SuruType type) => type switch
    {
        SuruType.Bool    => _builder.BuildZExt(val, LLVMTypeRef.Int64, ""),
        SuruType.Int64   => val,
        SuruType.Float64 => _builder.BuildBitCast(val, LLVMTypeRef.Int64, ""),
        _                => val,
    };

    private LLVMValueRef FromI64(LLVMValueRef raw, SuruType type) => type switch
    {
        SuruType.Bool    => _builder.BuildTrunc(raw, LLVMTypeRef.Int1, ""),
        SuruType.Int64   => raw,
        SuruType.Float64 => _builder.BuildBitCast(raw, LLVMTypeRef.Double, ""),
        _                => raw,
    };

    private (LLVMValueRef Value, SuruType Type) EmitClone(string srcVarName)
    {
        if (!_varStructMeta.TryGetValue(srcVarName, out var fields))
            throw new InvalidOperationException($"No struct metadata for '{srcVarName}'");

        var (srcHeadPtr, _) = EmitValue(new VariableReferenceExpression(srcVarName));

        LLVMValueRef prevNewNode = LLVMValueRef.CreateConstNull(_ptrType);
        LLVMValueRef? newHead = null;

        for (int i = 0; i < fields.Count; i++)
        {
            var oldNode = NavigateToNode(srcHeadPtr, i);

            var size = FieldNodeSize();
            var newNode = _builder.BuildCall2(_mallocFnType, _mallocFn, new LLVMValueRef[] { size }, $"clone_{fields[i].Name}");

            // Copy all slots
            CopyFieldNode(oldNode, newNode);

            // Set next to null (will be wired below)
            var newNextSlot = _builder.BuildStructGEP2(_fieldNodeType, newNode, 3, "");
            _builder.BuildStore(LLVMValueRef.CreateConstNull(_ptrType), newNextSlot);

            if (i == 0)
            {
                newHead = newNode;
            }
            else
            {
                var prevNextSlot = _builder.BuildStructGEP2(_fieldNodeType, prevNewNode, 3, "");
                _builder.BuildStore(newNode, prevNextSlot);
            }

            prevNewNode = newNode;
        }

        _pendingStructMeta = new List<(string, SuruType)>(fields);
        return (newHead!.Value, SuruType.Struct);
    }

    private void CopyFieldNode(LLVMValueRef src, LLVMValueRef dst)
    {
        for (uint slot = 0; slot <= 2; slot++)
        {
            var srcSlot = _builder.BuildStructGEP2(_fieldNodeType, src, slot, "");
            var dstSlot = _builder.BuildStructGEP2(_fieldNodeType, dst, slot, "");
            LLVMTypeRef slotType = slot switch { 0 => _ptrType, 1 => LLVMTypeRef.Int32, _ => LLVMTypeRef.Int64 };
            var val = _builder.BuildLoad2(slotType, srcSlot, "");
            _builder.BuildStore(val, dstSlot);
        }
    }

    private void EmitDrop(string varName)
    {
        if (!_varStructMeta.TryGetValue(varName, out var fields))
            throw new InvalidOperationException($"No struct metadata for '{varName}'");

        var (headPtr, _) = EmitValue(new VariableReferenceExpression(varName));

        for (int i = 0; i < fields.Count; i++)
        {
            var nodePtr = NavigateToNode(headPtr, i);
            _builder.BuildCall2(_freeFnType, _freeFn, new LLVMValueRef[] { nodePtr }, "");
        }
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
}
