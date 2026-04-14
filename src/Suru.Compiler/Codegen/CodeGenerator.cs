using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

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
            _ => throw new InvalidOperationException($"Unknown method '{method.MethodName}' on {receiverType}"),
        };

        return (value, receiverType);
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
