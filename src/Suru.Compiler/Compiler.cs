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

            File.WriteAllText(inclIrPath, inclResult.Value!);
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

    // Resolves `include "path" as ns` directives in module by merging function signatures
    // from each included file into the statement list under the `ns.` prefix — enabling
    // semantic analysis to type-check cross-module calls without a separate declaration step.
    //
    // Two additional collections are populated and returned in the Module:
    //
    //   ExternalFunctions — maps every merged "ns.fn" Suru name back to the original
    //     LLVM symbol name "fn". IRCodeGenerator uses this to emit `declare @fn` instead
    //     of `define @ns.fn` and to call `@fn` (not `@ns.fn`) at call sites.
    //
    //   IncludedSourcePaths — absolute paths of all included source files, collected
    //     transitively so CompileIR can build each into its own object file and link
    //     everything together. Each path appears at most once (deduplicated via a set).
    //
    // visitedPaths guards against circular includes; it accumulates across the recursive
    // calls so a file can't be included twice anywhere in the include graph.
    private static Module ResolveIncludes(
        Module module, string baseDir, HashSet<string> visitedPaths, HashSet<string>? beingResolved = null)
    {
        beingResolved ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var directives = module.Statements.OfType<IncludeDirective>().ToList();
        if (directives.Count == 0) return module;

        var mergedStatements = module.Statements
            .Where(s => s is not IncludeDirective)
            .ToList();
        var namespaces       = new HashSet<string>(module.Namespaces);
        var externalFns      = new Dictionary<string, string>();
        var seenPaths        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedPaths    = new List<string>();

        // Track constant names already in the main module so we can deduplicate
        // constants from multiple included files that share the same names.
        var seenConstantNames = new HashSet<string>();
        foreach (var s in module.Statements)
            if (s is LetStatement ls) seenConstantNames.Add(ls.Name);

        // Collect constants from all included files (deduplicated) so that included
        // function bodies can reference them during semantic analysis of the merged module.
        // These are inserted before function declarations to ensure they're in _symbols
        // before AnalyzeFunctionDeclaration re-injects constants at function entry.
        var includedConstants = new List<Statement>();

        foreach (var directive in directives)
        {
            var fullPath = Path.GetFullPath(Path.Combine(baseDir, directive.Path));
            if (!File.Exists(fullPath))
                throw new Exception($"Include file not found: {fullPath}");

            // True circular include: currently on the resolution call stack.
            if (beingResolved.Contains(fullPath))
                throw new Exception($"Circular include detected: {fullPath}");

            // Diamond include: already fully resolved by a sibling branch.
            // Register the namespace alias but skip re-merging the content.
            if (visitedPaths.Contains(fullPath))
            {
                namespaces.Add(directive.NamespaceName);
                continue;
            }

            beingResolved.Add(fullPath);
            visitedPaths.Add(fullPath);

            var source         = File.ReadAllText(fullPath);
            var includedModule = Parser.Parse(new Tokens(new Lexer(source), fullPath));

            var includedDir = Path.GetDirectoryName(fullPath)!;
            includedModule  = ResolveIncludes(includedModule, includedDir, visitedPaths, beingResolved);
            beingResolved.Remove(fullPath);

            // Collect this file and its transitive includes as separate compilation units.
            if (seenPaths.Add(fullPath))
                includedPaths.Add(fullPath);
            foreach (var transPath in includedModule.IncludedSourcePaths)
                if (seenPaths.Add(transPath))
                    includedPaths.Add(transPath);

            var ns = directive.NamespaceName;
            namespaces.Add(ns);

            foreach (var stmt in includedModule.Statements)
            {
                if (stmt is FunctionDeclaration fn)
                {
                    // Register the qualified name ("ns.fn") and record the original LLVM
                    // symbol name ("fn") so codegen can emit `declare @fn` and `call @fn`.
                    var qualifiedName = ns + "." + fn.Name;
                    externalFns[qualifiedName] = fn.Name;
                    mergedStatements.Add(new FunctionDeclaration(
                        qualifiedName, fn.Parameters, fn.ReturnType, fn.Body));
                }
                else if (stmt is LetStatement constant && seenConstantNames.Add(constant.Name))
                {
                    // Only scalar (Bool/Int64/Float64) module-level constants are merged:
                    // they become internal globals in both the main .ll and the included .ll
                    // (internal linkage means no symbol conflict at link time). String
                    // constants are skipped — they can't be emitted as simple LLVM globals
                    // and are only needed in the included file's own translation unit.
                    if (constant.Value is BoolLiteral or IntLiteral or FloatLiteral)
                        includedConstants.Add(stmt);
                }
            }
        }

        // Prepend constants so they precede all function declarations in the merged list,
        // ensuring SemanticAnalyzer sees them before analyzing any function body.
        mergedStatements.InsertRange(0, includedConstants);

        return new Module
        {
            SourcePath           = module.SourcePath,
            Statements           = mergedStatements,
            Namespaces           = namespaces,
            ExternalFunctions    = externalFns,
            IncludedSourcePaths  = includedPaths,
            TypeDeclarations     = BuildTypeDeclarationsDict(mergedStatements),
        };
    }

    private static Dictionary<string, TypeDeclaration> BuildTypeDeclarationsDict(
        IEnumerable<Statement> stmts)
    {
        // Duplicates are intentionally allowed; the semantic analyzer reports them.
        var dict = new Dictionary<string, TypeDeclaration>();
        foreach (var td in stmts.OfType<TypeDeclaration>())
            dict[td.Name] = td;
        return dict;
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
