using System.Diagnostics;
using System.Linq;
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
    }

    public CompilationResult CompileIR(string buildDir)
    {
        if (!File.Exists(_sourcePath))
            return CompilationResult.Fail($"File not found: {_sourcePath}");

        Directory.CreateDirectory(buildDir);

        var source = File.ReadAllText(_sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);

        var lexer = new Lexer(source);
        var tokens = new Tokens(lexer, _sourcePath);

        Module module;
        try
        {
            module = Parser.Parse(tokens);
        }
        catch (ParseException ex)
        {
            return CompilationResult.Fail(ex.Message);
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
            return CompilationResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return CompilationResult.Fail($"Include resolution failed: {ex.Message}");
        }

        var semanticErrors = SemanticAnalyzer.Analyze(module);
        if (semanticErrors.Count > 0)
            return CompilationResult.Fail(semanticErrors);

        var ir = IRCodeGenerator.Generate(module, Path.GetFileName(_sourcePath));
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
