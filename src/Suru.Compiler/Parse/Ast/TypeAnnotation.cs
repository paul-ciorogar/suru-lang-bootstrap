namespace Suru.Compiler.Parse.Ast;

// Represents a Suru type annotation — either a plain scalar type or a
// parameterised generic type. Only one level of nesting is needed today
// (Array<T>), but the recursive TypeParam field supports deeper nesting.
//
// Examples:
//   Int64                 → TypeAnnotation("Int64")
//   Array<Struct>         → TypeAnnotation("Array", TypeAnnotation("Struct"))
//   Array<Array<Int64>>   → TypeAnnotation("Array", TypeAnnotation("Array", TypeAnnotation("Int64")))
public sealed record TypeAnnotation(string Name, TypeAnnotation? TypeParam = null)
{
    public override string ToString() => TypeParam is null ? Name : $"{Name}<{TypeParam}>";
}
