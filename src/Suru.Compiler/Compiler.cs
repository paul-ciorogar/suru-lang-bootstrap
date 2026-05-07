using System.Diagnostics;
using System.Linq;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Semantic;
using Suru.Compiler.Codegen;

namespace Suru.Compiler;

/// <summary>
/// Orchestrates the full Suru compilation pipeline for a single source file:
/// Lex → Parse → Include resolution → Semantic analysis → IR codegen → Clang → Link.
///
/// Each public method exposes a prefix of the pipeline so that individual stages can
/// be invoked independently (e.g. for the <c>lex</c>, <c>parse</c>, and <c>ir</c> CLI commands).
/// </summary>
public class Compiler
{
    private readonly string _sourcePath;

    public Compiler(string sourcePath)
    {
        _sourcePath = sourcePath;
    }

    // ─── Stage: lex ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the source file and returns every token produced by the lexer,
    /// including the final <see cref="TokenKind.Eof"/> sentinel.
    /// Returns a failure result if the file does not exist or contains a bad character.
    /// </summary>
    public CompilationResult<IReadOnlyList<Token>> LexFile()
    {
        if (!File.Exists(_sourcePath))
            return CompilationResult<IReadOnlyList<Token>>.Fail($"File not found: {_sourcePath}");
        try
        {
            var source = File.ReadAllText(_sourcePath);
            var tokens = Lexer.Tokenize(source).ToList();
            return CompilationResult<IReadOnlyList<Token>>.Ok(tokens);
        }
        catch (Exception ex)
        {
            return CompilationResult<IReadOnlyList<Token>>.Fail(ex.Message);
        }
    }

    // ─── Stage: parse ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the lexer, parser, and include resolution on the source file and returns
    /// the fully merged <see cref="Module"/> that the semantic analyzer would receive.
    /// Include directives are expanded so the caller sees all imported functions.
    /// Returns a failure result on any lex, parse, or include error.
    /// </summary>
    public CompilationResult<Module> ParseFile()
    {
        var (module, errors) = ParseAndResolve();
        if (errors.Count > 0)
            return CompilationResult<Module>.Fail(errors);
        return CompilationResult<Module>.Ok(module!);
    }

    // ─── Stage: ir ───────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full front-end pipeline (lex → parse → include resolution → semantic analysis)
    /// and returns the LLVM IR text that would be passed to <c>clang</c>.
    /// Returns a failure result on any error at any stage.
    /// No files are written and no external tools are invoked.
    /// </summary>
    public CompilationResult<string> GenerateIr()
    {
        var (module, parseErrors) = ParseAndResolve();
        if (parseErrors.Count > 0)
            return CompilationResult<string>.Fail(parseErrors);

        var ir = IRCodeGenerator.Generate(module!, Path.GetFileName(_sourcePath));
        return CompilationResult<string>.Ok(ir);
    }

    // ─── Stage: build ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the complete pipeline through to a native executable:
    /// lex → parse → include resolution → semantic → IR codegen → clang → link.
    /// Writes <c>&lt;buildDir&gt;/&lt;name&gt;.ll</c>, <c>.o</c>, and the final binary.
    /// </summary>
    // Runs the complete pipeline to a native executable using the LLVM module model:
    //
    //   1. Compile the main source to its own .ll and .o.
    //   2. For every file listed in module.IncludedSourcePaths, generate its standalone
    //      IR (extern declarations for any of *its* includes) and compile to .o.
    //   3. Generate the three Suru runtime modules (suru_string.ll, suru_array.ll,
    //      suru_struct.ll) into the build directory and compile each to .o.
    //   4. Link all .o files together with cc.
    //
    // Each .suru file is an independent compilation unit — included files are not merged
    // into the main IR. Instead, cross-module calls compile to `call @fn` (original name,
    // no namespace prefix) and the linker resolves them against the included modules' .o.
    // The runtime .o files are always linked unconditionally so that any declare stub
    // emitted by the user module resolves at link time.
    public CompilationResult CompileIR(string buildDir)
    {
        Directory.CreateDirectory(buildDir);

        var (module, parseErrors) = ParseAndResolve();
        if (parseErrors.Count > 0)
            return CompilationResult.Fail(parseErrors);

        var objectPaths = new List<string>();

        // Step 1: compile the main source.
        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);
        var mainIr   = IRCodeGenerator.Generate(module!, Path.GetFileName(_sourcePath));
        var mainIrPath  = Path.Combine(buildDir, baseName + ".ll");
        var mainObjPath = Path.Combine(buildDir, baseName + ".o");
        File.WriteAllText(mainIrPath, mainIr);
        var clangError = RunClang(mainIrPath, mainObjPath);
        if (clangError is not null)
            return CompilationResult.Fail($"IR compile failed: {clangError}");
        objectPaths.Add(mainObjPath);

        // Step 2: compile each included source file into its own object.
        // GenerateIr() on each included Compiler instance runs the full front-end for that
        // file (including its own include resolution) and produces standalone IR where any
        // *further* includes appear as extern declarations.
        foreach (var includedPath in module!.IncludedSourcePaths)
        {
            var inclName    = Path.GetFileNameWithoutExtension(includedPath);
            var inclIrPath  = Path.Combine(buildDir, inclName + ".ll");
            var inclObjPath = Path.Combine(buildDir, inclName + ".o");

            var inclResult = new Compiler(includedPath).GenerateIr();
            if (!inclResult.Success)
                return CompilationResult.Fail($"Failed to compile included file '{includedPath}':\n" +
                                              string.Join("\n", inclResult.Errors));

            File.WriteAllText(inclIrPath, inclResult.Require());
            var inclClangError = RunClang(inclIrPath, inclObjPath);
            if (inclClangError is not null)
                return CompilationResult.Fail($"IR compile of '{inclName}' failed: {inclClangError}");

            objectPaths.Add(inclObjPath);
        }

        // Step 3: generate and compile the four Suru runtime modules.
        var runtimes = new[]
        {
            ("suru_box",    SuruRuntime.GenerateBoxRuntime()),
            ("suru_string", SuruRuntime.GenerateStringRuntime()),
            ("suru_array",  SuruRuntime.GenerateArrayRuntime()),
            ("suru_struct", SuruRuntime.GenerateStructRuntime()),
        };
        foreach (var (rtName, rtIr) in runtimes)
        {
            var rtIrPath  = Path.Combine(buildDir, rtName + ".ll");
            var rtObjPath = Path.Combine(buildDir, rtName + ".o");
            File.WriteAllText(rtIrPath, rtIr);
            var rtClangError = RunClang(rtIrPath, rtObjPath);
            if (rtClangError is not null)
                return CompilationResult.Fail($"IR compile of runtime '{rtName}' failed: {rtClangError}");
            objectPaths.Add(rtObjPath);
        }

        // Step 4: link all objects into the final executable.
        var executablePath = Path.Combine(buildDir, baseName);
        var linkError = Link(objectPaths, executablePath);
        if (linkError is not null)
            return CompilationResult.Fail($"Link failed: {linkError}");

        return CompilationResult.Ok(executablePath);
    }

    // ─── Shared: lex + parse + include resolution ────────────────────────────

    /// <summary>
    /// Reads, lexes, parses, and resolves include directives for <see cref="_sourcePath"/>.
    /// Returns <c>(module, [])</c> on success or <c>(null, errors)</c> on failure.
    /// Extracted so each public stage method (ParseFile, GenerateIr, CompileIR) shares
    /// identical front-end behaviour without duplication.
    /// </summary>
    private (Module? Module, IReadOnlyList<string> Errors) ParseAndResolve()
    {
        if (!File.Exists(_sourcePath))
            return (null, [$"File not found: {_sourcePath}"]);

        var source = File.ReadAllText(_sourcePath);

        var parseResult = Parser.Parse(new Tokens(new Lexer(source), _sourcePath));
        if (!parseResult.Success)
            return (null, parseResult.Errors);
        var module = parseResult.Require();

        try
        {
            var rootPath  = Path.GetFullPath(_sourcePath);
            var sourceDir = Path.GetDirectoryName(rootPath);
            if (sourceDir is null)
                return (null, [$"Cannot determine directory for source path: {_sourcePath}"]);
            module = IncludeResolver.Resolve(module, sourceDir, new IncludeGraph(rootPath));
        }
        catch (Exception ex)
        {
            return (null, [$"Include resolution failed: {ex.Message}"]);
        }

        var semanticErrors = SemanticAnalyzer.Analyze(module);
        if (semanticErrors.Count > 0)
            return (null, semanticErrors);

        return (module, []);
    }

    private static string? RunClang(string irPath, string objectPath)
    {
        var clang = FindClang();
        if (clang is null)
            return "clang not found (tried clang, clang-20..clang-15)";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = clang,
            Arguments = $"-c \"{irPath}\" -o \"{objectPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? null : stderr.Trim();
    }

    private static string? FindClang()
    {
        foreach (var name in new[] { "clang", "clang-20", "clang-19", "clang-18", "clang-17", "clang-16", "clang-15" })
        {
            using var which = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = name,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            which.WaitForExit();
            if (which.ExitCode == 0) return name;
        }
        return null;
    }

    // Links one or more object files into a native executable using cc.
    // All objects are passed on a single command line so the linker resolves cross-module
    // symbol references (e.g. a call @tokenize in parser.o resolved against lexer.o).
    private static string? Link(IReadOnlyList<string> objectPaths, string executablePath)
    {
        var quotedObjs = string.Join(" ", objectPaths.Select(p => $"\"{p}\""));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cc",
            Arguments = $"{quotedObjs} -o \"{executablePath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 ? null : stderr.Trim();
    }
}
