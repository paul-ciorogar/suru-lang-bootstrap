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

        var seenPaths   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths       = new List<string>();
        var aliases     = new AliasMap();
        var registry    = new ExternalDeclarationRegistry();

        // Seed deduplication sets from the main module so merged content cannot
        // shadow declarations the user already wrote.
        var seenConstants = new HashSet<string>(
            module.Statements.OfType<LetStatement>().Select(ls => ls.Name));
        var seenTypes = new HashSet<string>(
            module.Statements.OfType<TypeDeclaration>().Select(td => td.Name));
        var seenSumTypes = new HashSet<string>(
            module.Statements.OfType<SumTypeDeclaration>().Select(std => std.Name));
        // (path, unqualifiedName) pairs — guards against duplicate function declarations
        // in the merged statement list when multiple includes share transitive dependencies.
        var seenFns = new HashSet<(string, string)>();

        // Constants, types, and sum types are prepended so they precede function declarations —
        // SemanticAnalyzer's first pass registers types before it enters any function body.
        var pendingConstants = new List<Statement>();
        var pendingTypes     = new List<Statement>();
        var pendingSumTypes  = new List<Statement>();

        foreach (var directive in directives)
        {
            var fullPath = ValidatePath(directive, baseDir);

            // Circular: this file is currently on the resolution call stack.
            if (graph.IsActive(fullPath))
                throw new Exception($"Circular include detected: {fullPath}");

            // Diamond: fully resolved by a sibling branch — register the new alias and
            // merge the cached module's transitive Aliases and ExternalDeclarationRegistry
            // so the current context is as complete as if the module had been included first.
            // MergeFrom uses TryAdd semantics throughout, so this is idempotent.
            if (graph.IsResolved(fullPath))
            {
                aliases.Register(directive.NamespaceName, fullPath);
                if (graph.GetCachedModule(fullPath) is { } cached)
                {
                    aliases.MergeFrom(cached.Aliases);
                    registry.MergeFrom(cached.ExternalDeclarationRegistry);
                }
                continue;
            }

            graph.Enter(fullPath);
            var included = LoadModule(fullPath, graph);
            graph.CacheModule(fullPath, included);
            graph.Exit(fullPath);

            aliases.Register(directive.NamespaceName, fullPath);
            aliases.MergeFrom(included.Aliases);
            registry.MergeFrom(included.ExternalDeclarationRegistry);

            CollectPaths(fullPath, included, seenPaths, paths);
            MergeModule(included, directive.NamespaceName, fullPath,
                mergedStatements, seenFns,
                seenConstants, seenTypes, seenSumTypes,
                pendingConstants, pendingTypes, pendingSumTypes,
                registry);
        }

        // Prepend sum types, then struct types, then constants — all before function declarations.
        // Sum types depend on struct types so struct types go first.
        mergedStatements.InsertRange(0, pendingSumTypes.Concat(pendingTypes).Concat(pendingConstants));

        return new Module
        {
            SourcePath           = module.SourcePath,
            Statements           = mergedStatements,
            IncludedSourcePaths  = paths,
            TypeDeclarations     = BuildTypeDict(mergedStatements),
            SumTypeDeclarations  = BuildSumTypeDict(mergedStatements),
            Aliases              = aliases,
            ExternalDeclarationRegistry = registry,
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
    /// collections. Functions from <paramref name="included"/> itself are merged with
    /// their original unqualified name and <see cref="FunctionDeclaration.SourcePath"/>
    /// set to <paramref name="fullPath"/>. Transitive functions (already carrying a
    /// non-null <see cref="FunctionDeclaration.SourcePath"/>) are forwarded as-is.
    /// Deduplication is handled by <paramref name="seenFns"/> (keyed by path + name).
    /// The canonical registry is already populated via
    /// <see cref="ExternalDeclarationRegistry.MergeFrom"/> before this method runs.
    /// </summary>
    private static void MergeModule(
        Module included, string ns, string fullPath,
        List<Statement> statements, HashSet<(string, string)> seenFns,
        HashSet<string> seenConstants, HashSet<string> seenTypes, HashSet<string> seenSumTypes,
        List<Statement> pendingConstants, List<Statement> pendingTypes, List<Statement> pendingSumTypes,
        ExternalDeclarationRegistry registry)
    {
        foreach (var stmt in included.Statements)
        {
            if (stmt is FunctionDeclaration fn)
            {
                if (fn.SourcePath != null)
                {
                    // Transitive function — already in registry via MergeFrom.
                    // Forward the declaration so codegen can emit a `declare` stub.
                    if (seenFns.Add((fn.SourcePath, fn.Name)))
                        statements.Add(fn);
                }
                else
                {
                    // Function declared in the included file itself.
                    // Use the original unqualified name; SourcePath identifies the origin.
                    if (seenFns.Add((fullPath, fn.Name)))
                    {
                        statements.Add(new FunctionDeclaration(
                            fn.Name, fn.Parameters, fn.ReturnType, fn.Body)
                            { SourcePath = fullPath });
                        registry.Register(fullPath, fn);
                    }
                }
            }
            else if (stmt is LetStatement constant && seenConstants.Add(constant.Name))
            {
                // Only scalar constants are merged: they become internal globals in both
                // translation units (no symbol conflict at link time). String constants
                // cannot be emitted as simple LLVM globals and are only needed in the
                // included file's own translation unit.
                if (constant.Value is BoolLiteral or IntLiteral or FloatLiteral)
                {
                    pendingConstants.Add(stmt);
                    registry.Register(fullPath, constant);
                }
            }
            else if (stmt is TypeDeclaration td && seenTypes.Add(td.Name))
            {
                // Struct type declarations produce no LLVM IR — they are purely compile-time
                // metadata. Merging them lets the importing file use the type name in
                // annotations, struct literals, and function signatures.
                pendingTypes.Add(stmt);
                registry.Register(fullPath, td);
            }
            else if (stmt is SumTypeDeclaration std && seenSumTypes.Add(std.Name))
            {
                // Sum type declarations are also purely compile-time metadata (codegen from 13h).
                // Merged so the importing file can use the sum type name in annotations.
                pendingSumTypes.Add(stmt);
                registry.Register(fullPath, std);
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

    private static Dictionary<string, SumTypeDeclaration> BuildSumTypeDict(IEnumerable<Statement> stmts)
    {
        // Duplicates are intentionally allowed; the semantic analyzer reports them.
        var dict = new Dictionary<string, SumTypeDeclaration>();
        foreach (var std in stmts.OfType<SumTypeDeclaration>())
            dict[std.Name] = std;
        return dict;
    }
}
