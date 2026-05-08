namespace Suru.Compiler.Parse.Ast;

// A named sum type declaration at module scope.
//
// Syntax: type Shape: Circle, Square
//
// Each variant name must refer to a previously declared struct type (TypeDeclaration).
// The declaration itself generates no LLVM IR — it is purely compile-time metadata
// consumed by the semantic analyzer and (from Stage 13h onward) the codegen.
public sealed class SumTypeDeclaration(
    string name,
    IReadOnlyList<string> variants) : Statement
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Variants { get; } = variants;
}
