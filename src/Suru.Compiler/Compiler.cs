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
    /// Running is the point of the mode: <c>#view</c> and <c>#assert</c> observe values that
    /// exist only at runtime.
    /// <para>
    /// A successful run rewrites the source file, annotating each <c>#view</c> and
    /// <c>#assert</c> line with its result.
    /// </para>
    /// </summary>
    public TestResult Test(string buildDir) => Test(buildDir, TestOptions.Default);

    /// <inheritdoc cref="Test(string)"/>
    /// <param name="options">The two deadlines the run answers to.</param>
    public TestResult Test(string buildDir, TestOptions options)
    {
        var (result, module) = Build(buildDir, BuildMode.Test);
        if (!result.Success || module is null)
            return TestResult.Fail(result.Errors);

        try
        {
            var run = TestChannel.Run(result.OutputPath!, options);
            return TestRun.Report(module, _sourcePath, run);
        }
        catch (Exception ex) when (ex is TestChannelException or FrameException)
        {
            // Verbatim: these sentences are written to be read, and a 'Could not run …:' prefix
            // would only get in the way.
            return TestResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return TestResult.Fail($"Could not run {result.OutputPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// The pipeline. The module comes back too, because a test run needs it to match each
    /// reported value to its directive.
    /// </summary>
    private (CompilationResult Result, Module? Module) Build(string buildDir, BuildMode mode)
    {
        if (!File.Exists(_sourcePath))
            return (CompilationResult.Fail($"File not found: {_sourcePath}"), null);

        Directory.CreateDirectory(buildDir);

        var source = File.ReadAllText(_sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);

        var lexer = new Lexer(source, _sourcePath, mode);

        // 1. Lex
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

        // Dumped before the error check: a failed analysis is when you want to see how far
        // typing got.
        _dumps.Section(Dump.TypedAst, "ast after semantic analysis",
            () => AstPrinter.Print(module, withTypes: true));

        if (semanticErrors.Count > 0)
            return (CompilationResult.Fail(semanticErrors), module);

        // 4. Codegen → LLVM IR → object file
        LLVMModuleRef llvmModule;
        try
        {
            llvmModule = CodeGenerator.Generate(module, mode);
        }
        catch (CodegenException ex)
        {
            return (CompilationResult.Fail($"Internal compiler error: {ex.Message}"), module);
        }

        using var ownedModule = llvmModule;

        // Dumped before verifying: a module that fails verification is the one worth reading.
        _dumps.Section(Dump.Llvm, "llvm ir", llvmModule.PrintToString);

        // Always run, not gated on a flag, so malformed IR is caught here rather than as a
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

        // 5. Link, plus the test channel's runtime in test mode. The shim is located here rather
        // than in Link so a missing one reads as the broken installation it is, not as a
        // 'Link failed:' quoting cc.
        string? runtimePath = null;
        if (mode == BuildMode.Test)
        {
            runtimePath = RuntimeShim.Locate();
            if (runtimePath is null)
                return (CompilationResult.Fail(RuntimeShim.MissingMessage), module);
        }

        var executablePath = Path.Combine(buildDir, baseName);
        var linkError = Link(objectPath, executablePath, runtimePath);
        if (linkError is not null)
            return (CompilationResult.Fail($"Link failed: {linkError}"), module);

        return (CompilationResult.Ok(executablePath), module);
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

    /// <summary>
    /// The test channel's runtime, when there is one, is handed to <c>cc</c> as source rather
    /// than as a prebuilt object: compiling it costs about 50 ms.
    /// </summary>
    private static string? Link(string objectPath, string executablePath, string? runtimePath)
    {
        var runtime = runtimePath is null ? "" : $" \"{runtimePath}\"";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cc",
            Arguments = $"\"{objectPath}\"{runtime} -o \"{executablePath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 ? null : stderr.Trim();
    }
}
