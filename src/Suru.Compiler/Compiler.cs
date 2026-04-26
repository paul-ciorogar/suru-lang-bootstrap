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

        var semanticErrors = SemanticAnalyzer.Analyze(module!);
        if (semanticErrors.Count > 0)
            return CompilationResult<string>.Fail(semanticErrors);

        var ir = IRCodeGenerator.Generate(module!, Path.GetFileName(_sourcePath));
        return CompilationResult<string>.Ok(ir);
    }

    // ─── Stage: build ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the complete pipeline through to a native executable:
    /// lex → parse → include resolution → semantic → IR codegen → clang → link.
    /// Writes <c>&lt;buildDir&gt;/&lt;name&gt;.ll</c>, <c>.o</c>, and the final binary.
    /// </summary>
    public CompilationResult CompileIR(string buildDir)
    {
        Directory.CreateDirectory(buildDir);

        var (module, parseErrors) = ParseAndResolve();
        if (parseErrors.Count > 0)
            return CompilationResult.Fail(parseErrors);

        var semanticErrors = SemanticAnalyzer.Analyze(module!);
        if (semanticErrors.Count > 0)
            return CompilationResult.Fail(semanticErrors);

        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);
        var ir = IRCodeGenerator.Generate(module!, Path.GetFileName(_sourcePath));
        var irPath = Path.Combine(buildDir, baseName + ".ll");
        File.WriteAllText(irPath, ir);

        var objectPath = Path.Combine(buildDir, baseName + ".o");
        var clangError = RunClang(irPath, objectPath);
        if (clangError is not null)
            return CompilationResult.Fail($"IR compile failed: {clangError}");

        var executablePath = Path.Combine(buildDir, baseName);
        var linkError = Link(objectPath, executablePath);
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

        Module module;
        try
        {
            module = Parser.Parse(new Tokens(new Lexer(source), _sourcePath));
        }
        catch (ParseException ex)
        {
            return (null, [ex.Message]);
        }

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
            return (null, [ex.Message]);
        }
        catch (Exception ex)
        {
            return (null, [$"Include resolution failed: {ex.Message}"]);
        }

        return (module, []);
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

            var includedDir = Path.GetDirectoryName(fullPath)!;
            includedModule = ResolveIncludes(includedModule, includedDir, visitedPaths);

            var ns = directive.NamespaceName;
            namespaces.Add(ns);
            foreach (var stmt in includedModule.Statements)
            {
                if (stmt is FunctionDeclaration fn)
                {
                    var prefixed = new FunctionDeclaration(
                        ns + "." + fn.Name,
                        fn.Parameters,
                        fn.ReturnType,
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
