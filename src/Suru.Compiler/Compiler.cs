using System.Diagnostics;
using LLVMSharp.Interop;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Semantic;
using Suru.Compiler.Codegen;

namespace Suru.Compiler;

public class Compiler
{
    private readonly string _sourcePath;

    public Compiler(string sourcePath)
    {
        _sourcePath = sourcePath;
        LLVM.InitializeAllTargetInfos();
        LLVM.InitializeAllTargets();
        LLVM.InitializeAllTargetMCs();
        LLVM.InitializeAllAsmParsers();
        LLVM.InitializeAllAsmPrinters();

    }

    public CompilationResult Compile(string buildDir)
    {
        if (!File.Exists(_sourcePath))
            return CompilationResult.Fail($"File not found: {_sourcePath}");

        Directory.CreateDirectory(buildDir);

        var source = File.ReadAllText(_sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);

        var lexer = new Lexer(source);
        var tokens = new Tokens(lexer, _sourcePath);

        // 2. Parse
        Module module;
        try
        {
            module = Parser.Parse(tokens);
        }
        catch (ParseException ex)
        {
            return CompilationResult.Fail(ex.Message);
        }

        // 3. Semantic analysis
        var semanticErrors =  SemanticAnalyzer.Analyze(module);
        if (semanticErrors.Count > 0)
            return CompilationResult.Fail(semanticErrors);

        // 4. Codegen → LLVM IR → object file
        LLVMModuleRef llvmModule;
        try
        {
            llvmModule = CodeGenerator.Generate(module);
        }
        catch (CodegenException ex)
        {
            return CompilationResult.Fail($"Internal compiler error: {ex.Message}");
        }

        using var ownedModule = llvmModule;
        var objectPath = Path.Combine(buildDir, baseName + ".o");

        try
        {
            EmitObjectFile(llvmModule, objectPath);
        }
        catch (Exception ex)
        {
            return CompilationResult.Fail($"Codegen failed: {ex.Message}");
        }

        // 5. Link → native executable
        var executablePath = Path.Combine(buildDir, baseName);
        var linkError = Link(objectPath, executablePath);
        if (linkError is not null)
            return CompilationResult.Fail($"Link failed: {linkError}");

        return CompilationResult.Ok(executablePath);
    }

    private static void EmitObjectFile(LLVMModuleRef module, string outputPath)
    {
        var triple = LLVMTargetRef.DefaultTriple;
        if (!LLVMTargetRef.TryGetTargetFromTriple(triple, out var target, out var targetError))
            throw new Exception(targetError);

        var machine = target.CreateTargetMachine(
            triple,
            cpu: "generic",
            features: "",
            level: LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            reloc: LLVMRelocMode.LLVMRelocPIC,
            codeModel: LLVMCodeModel.LLVMCodeModelDefault);

        unsafe { LLVM.SetModuleDataLayout(module, machine.CreateTargetDataLayout()); }
        module.Target = triple;

        if (!machine.TryEmitToFile(module, outputPath, LLVMCodeGenFileType.LLVMObjectFile, out var emitError))
            throw new Exception(emitError);
    }

    private static string? Link(string objectPath, string executablePath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cc",
            Arguments = $"\"{objectPath}\" -o \"{executablePath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 ? null : stderr.Trim();
    }
}
