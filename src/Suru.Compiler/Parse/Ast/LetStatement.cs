namespace Suru.Compiler.Parse.Ast;

public sealed class LetStatement(string name, Expression value, TypeAnnotation typeAnnotation) : Statement
{
    public string Name { get; } = name;
    public Expression Value { get; } = value;
    // Explicit type: `let x Int32: 5` sets TypeAnnotation = TypeAnnotation("Int32").
    // `let tokens Array<Struct>: []` sets TypeAnnotation = TypeAnnotation("Array", TypeAnnotation("Struct")).
    public TypeAnnotation TypeAnnotation { get; } = typeAnnotation;
}
