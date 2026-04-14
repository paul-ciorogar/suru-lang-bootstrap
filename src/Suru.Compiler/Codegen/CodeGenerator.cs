using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;
using System.Linq;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMValueRef _printfFn;
    private readonly LLVMTypeRef _printfType;
    private readonly Dictionary<string, (LLVMValueRef Alloca, SuruType Type)> _vars = new();

    private CodeGenerator(LLVMBuilderRef builder, LLVMValueRef printfFn, LLVMTypeRef printfType)
    {
        _builder = builder;
        _printfFn = printfFn;
        _printfType = printfType;
    }

    public static LLVMModuleRef Generate(Module module)
    {
        var llvmModule = LLVMModuleRef.CreateWithName("suru");
        var context = llvmModule.Context;

        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        var printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        var printfFn = llvmModule.AddFunction("printf", printfType);

        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        var builder = LLVMBuilderRef.Create(context);
        var entry = mainFn.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);

        var gen = new CodeGenerator(builder, printfFn, printfType);
        foreach (var stmt in module.Statements)
            gen.EmitStmt(stmt);

        builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        builder.Dispose();

        return llvmModule;
    }

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
                break;

            case AssignmentStatement assign:
                var (newVal, _) = EmitValue(assign.Value);
                _builder.BuildStore(newVal, _vars[assign.Name].Alloca);
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

            default:
                throw new InvalidOperationException($"Unsupported expression type {expr.GetType().Name}");
        }
    }

    private (LLVMValueRef Value, SuruType Type) EmitMethodCall(MethodCallExpression method)
    {
        var (receiver, receiverType) = EmitValue(method.Receiver);

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
        }
    }

    private static LLVMTypeRef LlvmTypeFor(SuruType type) => type switch
    {
        SuruType.Bool    => LLVMTypeRef.Int1,
        SuruType.Int64   => LLVMTypeRef.Int64,
        SuruType.Float64 => LLVMTypeRef.Double,
        _ => throw new InvalidOperationException($"No LLVM type for {type}"),
    };
}
