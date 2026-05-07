using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler;

/// <summary>
/// Resolves <c>include "path" as ns</c> directives by merging declarations from
/// included files into a <see cref="Module"/> so that semantic analysis can
/// type-check cross-module calls without a separate declaration step.
///
/// Responsibilities are split across four focused helpers:
///   <see cref="ValidatePath"/>     — path existence check, returns absolute path.
///   <see cref="LoadModule"/>       — reads, parses, and recursively resolves one file.
///   <see cref="CollectPaths"/>     — appends a file and its transitive includes to the path list.
///   <see cref="MergeModule"/>      — folds one resolved module's declarations into the accumulator.
/// </summary>
internal static class IncludeResolver
{
    /// <summary>
    /// Resolves all <c>include</c> directives in <paramref name="module"/> and returns
    /// a new <see cref="Module"/> with the merged declaration set.
    /// </summary>
    /// <param name="module">The module whose include directives should be expanded.</param>
    /// <param name="baseDir">Directory of <paramref name="module"/>'s source file;
    ///     used to resolve relative include paths.</param>
    /// <param name="graph">Tracks visited/active paths for circular and diamond detection.</param>
    internal static Module Resolve(Module module, string baseDir, IncludeGraph graph)
    {
        var directives = module.Statements.OfType<IncludeDirective>().ToList();
        if (directives.Count == 0) return module;

        // Start the merged statement list with every non-include statement from this module.
        var mergedStatements = module.Statements
            .Where(s => s is not IncludeDirective)
            .ToList();

        var namespaces  = new HashSet<string>(module.Namespaces);
        var externalFns = new Dictionary<string, string>();
        var seenPaths   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths       = new List<string>();

        // Seed deduplication sets from the main module so merged content cannot
        // shadow declarations the user already wrote.
        var seenConstants = new HashSet<string>(
            module.Statements.OfType<LetStatement>().Select(ls => ls.Name));
        var seenTypes = new HashSet<string>(
            module.Statements.OfType<TypeDeclaration>().Select(td => td.Name));

        // Constants and types are prepended so they precede function declarations —
        // SemanticAnalyzer's first pass registers types before it enters any function body.
        var pendingConstants = new List<Statement>();
        var pendingTypes     = new List<Statement>();

        foreach (var directive in directives)
        {
            var fullPath = ValidatePath(directive, baseDir);

            // Circular: this file is currently on the resolution call stack.
            if (graph.IsActive(fullPath))
                throw new Exception($"Circular include detected: {fullPath}");

            // Diamond: fully resolved by a sibling branch — register alias, skip content.
            if (graph.IsResolved(fullPath))
            {
                namespaces.Add(directive.NamespaceName);
                continue;
            }

            graph.Enter(fullPath);
            var included = LoadModule(fullPath, graph);
            graph.Exit(fullPath);

            CollectPaths(fullPath, included, seenPaths, paths);
            MergeModule(included, directive.NamespaceName,
                mergedStatements, externalFns, namespaces,
                seenConstants, seenTypes, pendingConstants, pendingTypes);
        }

        // Prepend types then constants so both appear before all function declarations.
        mergedStatements.InsertRange(0, pendingTypes.Concat(pendingConstants));

        return new Module
        {
            SourcePath          = module.SourcePath,
            Statements          = mergedStatements,
            Namespaces          = namespaces,
            ExternalFunctions   = externalFns,
            IncludedSourcePaths = paths,
            TypeDeclarations    = BuildTypeDict(mergedStatements),
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves <paramref name="directive"/>'s relative path against <paramref name="baseDir"/>
    /// and verifies the file exists.
    /// </summary>
    private static string ValidatePath(IncludeDirective directive, string baseDir)
    {
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, directive.Path));
        if (!File.Exists(fullPath))
            throw new Exception($"Include file not found: {fullPath}");
        return fullPath;
    }

    /// <summary>
    /// Reads, lexes, parses, and recursively resolves <paramref name="fullPath"/>.
    /// </summary>
    private static Module LoadModule(string fullPath, IncludeGraph graph)
    {
        var source      = File.ReadAllText(fullPath);
        var parseResult = Parser.Parse(new Tokens(new Lexer(source), fullPath));
        if (!parseResult.Success)
            throw new Exception(string.Join("\n", parseResult.Errors));
        var included = parseResult.Require();

        var dir = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine directory for included path: {fullPath}");

        return Resolve(included, dir, graph);
    }

    /// <summary>
    /// Appends <paramref name="fullPath"/> and every path in
    /// <paramref name="included"/>.IncludedSourcePaths to <paramref name="paths"/>,
    /// skipping any already in <paramref name="seen"/>.
    /// </summary>
    private static void CollectPaths(
        string fullPath, Module included, HashSet<string> seen, List<string> paths)
    {
        if (seen.Add(fullPath))
            paths.Add(fullPath);
        foreach (var transPath in included.IncludedSourcePaths)
            if (seen.Add(transPath))
                paths.Add(transPath);
    }

    /// <summary>
    /// Folds all declarations from <paramref name="included"/> into the accumulator
    /// collections, applying the <paramref name="ns"/> prefix to functions that
    /// originate in <paramref name="included"/> itself (transitive functions keep
    /// their existing qualified name so they are not double-prefixed).
    /// </summary>
    private static void MergeModule(
        Module included, string ns,
        List<Statement> statements, Dictionary<string, string> externalFns,
        HashSet<string> namespaces,
        HashSet<string> seenConstants, HashSet<string> seenTypes,
        List<Statement> pendingConstants, List<Statement> pendingTypes)
    {
        namespaces.Add(ns);
        // Propagate transitive namespaces so that calls like `sem.foo()` inside an
        // included file's bodies remain valid in the importing module.
        foreach (var transitiveNs in included.Namespaces)
            namespaces.Add(transitiveNs);

        foreach (var stmt in included.Statements)
        {
            if (stmt is FunctionDeclaration fn)
            {
                if (included.ExternalFunctions.TryGetValue(fn.Name, out var llvmSymbol))
                {
                    // Transitive function — propagate as-is; TryAdd prevents duplicates
                    // when multiple siblings share the same transitive dependency.
                    if (externalFns.TryAdd(fn.Name, llvmSymbol))
                        statements.Add(fn);
                }
                else
                {
                    // Function declared in the included file itself — prefix with ns.
                    // Record the original LLVM symbol so codegen emits `declare @fn`
                    // and `call @fn` (not `@ns.fn`) at call sites.
                    var qualified = ns + "." + fn.Name;
                    externalFns[qualified] = fn.Name;
                    statements.Add(new FunctionDeclaration(
                        qualified, fn.Parameters, fn.ReturnType, fn.Body));
                }
            }
            else if (stmt is LetStatement constant && seenConstants.Add(constant.Name))
            {
                // Only scalar constants are merged: they become internal globals in both
                // translation units (no symbol conflict at link time). String constants
                // cannot be emitted as simple LLVM globals and are only needed in the
                // included file's own translation unit.
                if (constant.Value is BoolLiteral or IntLiteral or FloatLiteral)
                    pendingConstants.Add(stmt);
            }
            else if (stmt is TypeDeclaration td && seenTypes.Add(td.Name))
            {
                // Type declarations produce no LLVM IR — they are purely compile-time
                // metadata. Merging them lets the importing file use the type name in
                // annotations, struct literals, and function signatures.
                pendingTypes.Add(stmt);
            }
        }
    }

    private static Dictionary<string, TypeDeclaration> BuildTypeDict(IEnumerable<Statement> stmts)
    {
        // Duplicates are intentionally allowed; the semantic analyzer reports them.
        var dict = new Dictionary<string, TypeDeclaration>();
        foreach (var td in stmts.OfType<TypeDeclaration>())
            dict[td.Name] = td;
        return dict;
    }
}
