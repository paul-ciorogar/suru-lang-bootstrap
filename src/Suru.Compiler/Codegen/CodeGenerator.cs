using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{
    public static LLVMModuleRef Generate(Module module)
    {
        var llvmModule = LLVMModuleRef.CreateWithName("suru");
        var context = llvmModule.Context;

        // Declare printf: i32 (ptr, ...)
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        var printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        var printfFn = llvmModule.AddFunction("printf", printfType);

        // Define main
        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        var builder = LLVMBuilderRef.Create(context);
        var entry = mainFn.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);

        foreach (var stmt in module.Statements)
            EmitStmt(builder, printfFn, printfType, stmt);

        builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        builder.Dispose();

        return llvmModule;
    }

    private static void EmitStmt(LLVMBuilderRef builder, LLVMValueRef printfFn, LLVMTypeRef printfType, Statement statement)
    {
        if (statement is ExpressionStatement exprStmt)
            EmitExpr(builder, printfFn, printfType, exprStmt.Expression);
    }

    private static void EmitExpr(LLVMBuilderRef builder, LLVMValueRef printfFn, LLVMTypeRef printfType, Expression expression)
    {
        if (expression is CallExpression { Name: "printLn", Args.Count: 1 } call)
            EmitPrintLn(builder, printfFn, printfType, call.Args[0]);
    }

    private static void EmitPrintLn(LLVMBuilderRef builder, LLVMValueRef printfFn, LLVMTypeRef printfType, Expression arg)
    {
        switch (arg)
        {
            case BoolLiteral b:
            {
                var fmt = builder.BuildGlobalStringPtr("%s\n", "");
                var str = builder.BuildGlobalStringPtr(b.Value ? "true" : "false", "");
                builder.BuildCall2(printfType, printfFn, new LLVMValueRef[] { fmt, str }, "");
                break;
            }
            case IntLiteral i:
            {
                var fmt = builder.BuildGlobalStringPtr("%lld\n", "");
                var val = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i.Value, true);
                builder.BuildCall2(printfType, printfFn, new LLVMValueRef[] { fmt, val }, "");
                break;
            }
            case FloatLiteral f:
            {
                var fmt = builder.BuildGlobalStringPtr("%g\n", "");
                var val = LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, f.Value);
                builder.BuildCall2(printfType, printfFn, new LLVMValueRef[] { fmt, val }, "");
                break;
            }
        }
    }
}
