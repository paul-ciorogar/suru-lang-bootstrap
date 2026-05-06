namespace Suru.Compiler.Parse.Ast;

// Represents `include "rel/path.suru" as ns`.
// Consumed entirely by ResolveIncludes before semantic analysis — by the time the
// SemanticAnalyzer runs, all IncludeDirective nodes have been stripped from the
// statement list and their exported symbols merged under the `ns.` prefix.
public sealed class IncludeDirective(string path, string namespaceName) : Statement
{
    public string Path { get; } = path;
    public string NamespaceName { get; } = namespaceName;
}
