namespace Suru.Compiler.Parse.Ast;

public sealed class Module
{
    public string SourcePath { get; init; } = string.Empty;
    public IReadOnlyList<Statement> Statements { get; init; } = [];

    // Absolute paths of every source file pulled in via include directives, collected
    // transitively so CompileIR can compile each file into its own object and link them all.
    // Paths are deduplicated: each file appears at most once regardless of how many include
    // chains reach it.
    public IReadOnlyList<string> IncludedSourcePaths { get; init; } = [];

    // Named type declarations indexed by type name (e.g. "Point" → TypeDeclaration).
    // Populated by the parser from top-level `type` statements and preserved through
    // include resolution so the semantic analyzer and codegen can look up field layouts.
    public IReadOnlyDictionary<string, TypeDeclaration> TypeDeclarations { get; init; }
        = new Dictionary<string, TypeDeclaration>();

    // Alias-independent canonical maps populated by IncludeResolver.
    internal AliasMap                      Aliases                      { get; init; } = new();
    internal ExternalDeclarationRegistry   ExternalDeclarationRegistry  { get; init; } = new();
}
