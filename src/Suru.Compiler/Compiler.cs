using System.Diagnostics;
using LLVMSharp.Interop;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Semantic;
using Suru.Compiler.Codegen;
using Suru.Compiler.Debug;
using Suru.Compiler.Testing;

namespace Suru.Compiler;

public class Compiler
{
    private readonly string _sourcePath;
    private readonly DumpOptions _dumps;

    public Compiler(string sourcePath) : this(sourcePath, DumpOptions.Off) { }

    public Compiler(string sourcePath, DumpOptions dumps)
    {
        _sourcePath = sourcePath;
        _dumps = dumps;
        LLVM.InitializeAllTargetInfos();
        LLVM.InitializeAllTargets();
        LLVM.InitializeAllTargetMCs();
        LLVM.InitializeAllAsmParsers();
        LLVM.InitializeAllAsmPrinters();

    }

    /// <summary>Builds for production: the <c>#</c> directives are never even lexed.</summary>
    public CompilationResult Compile(string buildDir) =>
        Build(buildDir, BuildMode.Production).Result;

    /// <summary>
    /// Builds with the <c>#</c> directives live, runs the result, and reports what they saw.
    /// Running is the point of the mode rather than an extra: <c>#view</c> and <c>#assert</c>
    /// observe values that exist only while the program is executing.
    /// <para>
    /// A successful run rewrites the source file, annotating each <c>#view</c> and
    /// <c>#assert</c> line with its result.
    /// </para>
    /// </summary>
    public TestResult Test(string buildDir)
    {
        var (result, module) = Build(buildDir, BuildMode.Test);
        if (!result.Success || module is null)
            return TestResult.Fail(result.Errors);

        try
        {
            var (stdout, exitCode) = Run(result.OutputPath!);
            return TestRun.Report(module, _sourcePath, stdout, exitCode);
        }
        catch (Exception ex)
        {
            return TestResult.Fail($"Could not run {result.OutputPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// The pipeline. The module comes back alongside the result because a test run needs it
    /// to map a record printed by the program to the directive that asked for it.
    /// </summary>
    private (CompilationResult Result, Module? Module) Build(string buildDir, BuildMode mode)
    {
        if (!File.Exists(_sourcePath))
            return (CompilationResult.Fail($"File not found: {_sourcePath}"), null);

        Directory.CreateDirectory(buildDir);

        var source = File.ReadAllText(_sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);

        var lexer = new Lexer(source, _sourcePath, mode);

        // 1. Lex — dumped by lexing a second time, leaving the parser's pull-based cursor alone.
        _dumps.Section(Dump.Tokens, "tokens", () => TokenPrinter.Print(source, _sourcePath, mode));

        // 2. Parse
        Module module;
        try
        {
            module = Parser.Parse(lexer);
        }
        catch (Exception ex) when (ex is ParseException or LexException)
        {
            return (CompilationResult.Fail(ex.Message), null);
        }

        _dumps.Section(Dump.Ast, "ast after parse", () => AstPrinter.Print(module));

        // 3. Semantic analysis
        var semanticErrors =  SemanticAnalyzer.Analyze(module);

        // Dumped before the error check: a failed analysis is exactly when you
        // want to see how far typing got.
        _dumps.Section(Dump.TypedAst, "ast after semantic analysis",
            () => AstPrinter.Print(module, withTypes: true));

        if (semanticErrors.Count > 0)
            return (CompilationResult.Fail(semanticErrors), module);

        // 4. Codegen → LLVM IR → object file
        LLVMModuleRef llvmModule;
        try
        {
            llvmModule = CodeGenerator.Generate(module);
        }
        catch (CodegenException ex)
        {
            return (CompilationResult.Fail($"Internal compiler error: {ex.Message}"), module);
        }

        using var ownedModule = llvmModule;

        // Dumped before verifying: a module that fails verification is precisely
        // the one worth reading.
        _dumps.Section(Dump.Llvm, "llvm ir", llvmModule.PrintToString);

        // Verification is an invariant check, not a dump: it runs unconditionally
        // so malformed IR is caught at the stage that produced it rather than as a
        // crash in the emitted executable.
        if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var verifyError))
            return (CompilationResult.Fail($"Internal compiler error: invalid LLVM module: {verifyError}"), module);

        var objectPath = Path.Combine(buildDir, baseName + ".o");

        try
        {
            EmitObjectFile(llvmModule, objectPath);
        }
        catch (Exception ex)
        {
            return (CompilationResult.Fail($"Codegen failed: {ex.Message}"), module);
        }

        // 5. Link → native executable
        var executablePath = Path.Combine(buildDir, baseName);
        var linkError = Link(objectPath, executablePath);
        if (linkError is not null)
            return (CompilationResult.Fail($"Link failed: {linkError}"), module);

        return (CompilationResult.Ok(executablePath), module);
    }

    /// <summary>
    /// Runs a test build, capturing stdout — which carries the program's own output and the
    /// records its directives printed, interleaved.
    /// </summary>
    private static (string Stdout, int ExitCode) Run(string executablePath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return (stdout, process.ExitCode);
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
