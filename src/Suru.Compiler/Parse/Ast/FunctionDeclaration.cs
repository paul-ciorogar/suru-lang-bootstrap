using Suru.Compiler.Types;

namespace Suru.Compiler.Parse.Ast;

public sealed class FunctionParameter(string name, TypeAnnotation typeAnnotation)
{
    public string Name                   { get; } = name;
    public TypeAnnotation TypeAnnotation { get; } = typeAnnotation;
    public SuruType? ResolvedType        { get; set; }
}

public sealed class FunctionDeclaration(
    string name,
    IReadOnlyList<FunctionParameter> parameters,
    TypeAnnotation returnType,
    IReadOnlyList<Statement> body) : Statement
{
    public string Name                                  { get; } = name;
    public IReadOnlyList<FunctionParameter> Parameters { get; } = parameters;
    public TypeAnnotation ReturnType                    { get; } = returnType;
    public IReadOnlyList<Statement> Body               { get; } = body;
    // null = declared in the current file; absolute path for every function merged from an include.
    public string? SourcePath                           { get; init; }
}
