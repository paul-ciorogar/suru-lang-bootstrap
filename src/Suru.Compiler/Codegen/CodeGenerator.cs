using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{
    private readonly Module _module;
    private readonly LLVMModuleRef _llvmModule;
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMTypeRef _printfType;
    private readonly LLVMValueRef _printfFn;
    private readonly Dictionary<string, LLVMValueRef> _strings = [];

    private CodeGenerator(Module module)
    {
        _module = module;
        _llvmModule = LLVMModuleRef.CreateWithName("suru");
        _builder = LLVMBuilderRef.Create(_llvmModule.Context);

        // Declare printf: i32 (ptr, ...)
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        _printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
        _printfFn = _llvmModule.AddFunction("printf", _printfType);
    }

    public static LLVMModuleRef Generate(Module module)
    {
        var generator = new CodeGenerator(module);
        return generator._Generate();
    }

    private LLVMModuleRef _Generate()
    {
        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = _llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        _builder.PositionAtEnd(mainFn.AppendBasicBlock("entry"));

        foreach (var statement in _module.Statements)
            EmitStatement(statement);

        _builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        _builder.Dispose();

        return _llvmModule;
    }

    private void EmitStatement(Statement statement)
    {
        switch (statement)
        {
            case ExpressionStatement exprStmt:
                EmitExpr(exprStmt.Expression);
                break;
            default:
                throw new CodegenException($"cannot emit statement '{statement.GetType().Name}'");
        }
    }

    /// <summary>
    /// Emits an expression and returns its value, or a null value reference for
    /// expressions of type <see cref="SuruType.Void"/>.
    /// </summary>
    private LLVMValueRef EmitExpr(Expression expression)
    {
        switch (expression)
        {
            case BoolLiteral b:
                return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, b.Value ? 1ul : 0ul, false);
            case IntLiteral i:
                return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i.Value, true);
            case FloatLiteral f:
                return LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, f.Value);
            case CallExpression { Name: "printLn", Args.Count: 1 } call:
                EmitPrintLn(call.Args[0]);
                return default;
            default:
                throw new CodegenException($"cannot emit expression '{expression.GetType().Name}'");
        }
    }

    private void EmitPrintLn(Expression arg)
    {
        var value = EmitExpr(arg);
        var type = arg.Type
            ?? throw new CodegenException($"argument at {arg.Position} was never typed");

        if (type == SuruType.Bool)
        {
            // printf has no bool conversion, so pick the literal text at runtime.
            var text = _builder.BuildSelect(value, String("true"), String("false"));
            Printf("%s\n", text);
        }
        else if (type == SuruType.I64)
        {
            Printf("%lld\n", value);
        }
        else if (type == SuruType.F64)
        {
            Printf("%g\n", value);
        }
        else
        {
            throw new CodegenException($"cannot print a value of type '{type}'");
        }
    }

    private void Printf(string format, LLVMValueRef value) =>
        _builder.BuildCall2(_printfType, _printfFn, new LLVMValueRef[] { String(format), value }, "");

    /// <summary>Interns a global string constant so repeated literals share one global.</summary>
    private LLVMValueRef String(string value)
    {
        if (_strings.TryGetValue(value, out var existing))
            return existing;

        var global = _builder.BuildGlobalStringPtr(value, "");
        _strings[value] = global;
        return global;
    }
}
