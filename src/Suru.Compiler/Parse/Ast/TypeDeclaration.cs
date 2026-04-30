namespace Suru.Compiler.Parse.Ast;

/// <summary>
/// A named struct type declaration at module scope.
///
/// Syntax (inline):   type Point: { x Int64, y Int64 }
/// Syntax (multiline):
///   type Point: {
///       x Int64
///       y Int64
///   }
///
/// Fields use only a name + type annotation — no value. Field type annotations
/// are simple identifiers (Int64, String, …) or generics (Array&lt;Int64&gt;).
/// The declaration resolves to <c>SuruType.Struct</c> at the semantic layer.
/// </summary>
public sealed class TypeDeclaration(
    string name,
    IReadOnlyList<(string Field, TypeAnnotation Type)> fields) : Statement
{
    /// <summary>The declared type name, e.g. <c>"Point"</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Ordered field list: (field name, field type annotation).</summary>
    public IReadOnlyList<(string Field, TypeAnnotation Type)> Fields { get; } = fields;
}
