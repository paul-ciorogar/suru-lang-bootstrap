namespace Suru.Compiler.Parse.Ast;

public sealed class Module
{
    public string SourcePath { get; init; } = string.Empty;
    public IReadOnlyList<Statement> Statements { get; init; } = [];
    public IReadOnlySet<string> Namespaces { get; init; } = new HashSet<string>();

    // Maps each Suru-level qualified call name ("ns.fn") to the original LLVM symbol name
    // ("fn") that appears in the included module's own compiled IR.
    //
    // During codegen, EmitUserFunctionCall uses this table to emit `call @fn(...)` rather
    // than `call @ns.fn(...)` — the dot-prefixed Suru name is a language-level concept only.
    // EmitFunction uses it to skip emitting a `define` body for imported functions and
    // instead emit a `declare` that the linker resolves against the included module's .o.
    public IReadOnlyDictionary<string, string> ExternalFunctions { get; init; }
        = new Dictionary<string, string>();

    // Absolute paths of every source file pulled in via include directives, collected
    // transitively so CompileIR can compile each file into its own object and link them all.
    // Paths are deduplicated: each file appears at most once regardless of how many include
    // chains reach it.
    public IReadOnlyList<string> IncludedSourcePaths { get; init; } = [];
}
