namespace Suru.Compiler.Parse.Ast;

// Represents `name: expr` variable rebind.
// SemanticAnalyzer rejects reassignment of module-level constants (_constants set).
public sealed class AssignmentStatement(string name, Expression value) : Statement
{
    public string Name { get; } = name;
    public Expression Value { get; } = value;
}
