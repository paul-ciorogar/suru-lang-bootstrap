using System.Diagnostics;
using System.Linq;
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

        // 2b. Resolve include directives
        try
        {
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(_sourcePath)
            };
            module = ResolveIncludes(module, Path.GetDirectoryName(Path.GetFullPath(_sourcePath))!, visitedPaths);
        }
        catch (ParseException ex)
        {
            return CompilationResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return CompilationResult.Fail($"Include resolution failed: {ex.Message}");
        }

        // 3. Semantic analysis
        var semanticErrors =  SemanticAnalyzer.Analyze(module);
        if (semanticErrors.Count > 0)
            return CompilationResult.Fail(semanticErrors);

        // 4. Codegen → LLVM IR → object file
        using var llvmModule = CodeGenerator.Generate(module);
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

    private static Module ResolveIncludes(Module module, string baseDir, HashSet<string> visitedPaths)
    {
        var directives = module.Statements.OfType<IncludeDirective>().ToList();
        if (directives.Count == 0) return module;

        var mergedStatements = module.Statements
            .Where(s => s is not IncludeDirective)
            .ToList();
        var namespaces = new HashSet<string>(module.Namespaces);

        foreach (var directive in directives)
        {
            var fullPath = Path.GetFullPath(Path.Combine(baseDir, directive.Path));
            if (!File.Exists(fullPath))
                throw new Exception($"Include file not found: {fullPath}");
            if (visitedPaths.Contains(fullPath))
                throw new Exception($"Circular include detected: {fullPath}");

            visitedPaths.Add(fullPath);

            var source = File.ReadAllText(fullPath);
            var includedModule = Parser.Parse(new Tokens(new Lexer(source), fullPath));

            // Recursively resolve includes in the included file
            var includedDir = Path.GetDirectoryName(fullPath)!;
            includedModule = ResolveIncludes(includedModule, includedDir, visitedPaths);

            // Prefix all function declarations with the namespace alias
            var ns = directive.NamespaceName;
            namespaces.Add(ns);
            foreach (var stmt in includedModule.Statements)
            {
                if (stmt is FunctionDeclaration fn)
                {
                    var prefixed = new FunctionDeclaration(
                        ns + "." + fn.Name,
                        fn.Parameters,
                        fn.ReturnTypeName,
                        fn.Body);
                    mergedStatements.Add(prefixed);
                }
            }
        }

        return new Module
        {
            SourcePath = module.SourcePath,
            Statements = mergedStatements,
            Namespaces = namespaces,
        };
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
