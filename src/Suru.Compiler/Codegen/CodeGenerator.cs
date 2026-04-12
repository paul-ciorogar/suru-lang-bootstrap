using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Codegen;

public sealed class CodeGenerator
{

    public static LLVMModuleRef Generate(Module module)
    {
        _ = module;

        var llvmModule = LLVMModuleRef.CreateWithName("suru");
        var context = llvmModule.Context;

        // An empty Suru file compiles to: int main() { return 0; }
        var mainType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []);
        var mainFn = llvmModule.AddFunction("main", mainType);
        mainFn.Linkage = LLVMLinkage.LLVMExternalLinkage;

        var builder = LLVMBuilderRef.Create(context);
        var entry = mainFn.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);
        builder.BuildRet(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false));
        builder.Dispose();

        return llvmModule;
    }
}
