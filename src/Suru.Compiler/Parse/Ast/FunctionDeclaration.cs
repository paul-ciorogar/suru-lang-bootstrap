namespace Suru.Compiler.Parse.Ast;

public sealed class FunctionParameter(string name, string typeName)
{
    public string Name     { get; } = name;
    public string TypeName { get; } = typeName;
}

public sealed class FunctionDeclaration(
    string name,
    IReadOnlyList<FunctionParameter> parameters,
    string returnTypeName,
    IReadOnlyList<Statement> body) : Statement
{
    public string Name                                  { get; } = name;
    public IReadOnlyList<FunctionParameter> Parameters { get; } = parameters;
    public string ReturnTypeName                        { get; } = returnTypeName;
    public IReadOnlyList<Statement> Body               { get; } = body;
}
