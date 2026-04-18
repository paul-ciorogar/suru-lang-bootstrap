namespace Suru.Compiler.Parse.Ast;

public sealed class IncludeDirective(string path, string namespaceName) : Statement
{
    public string Path { get; } = path;
    public string NamespaceName { get; } = namespaceName;
}
