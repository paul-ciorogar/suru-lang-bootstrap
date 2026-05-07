using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Types;

// Single source of truth for TypeAnnotation → SuruType resolution.
// Both the semantic analyzer (returns null on failure) and the codegen
// (throws on failure) delegate here so a new type only needs one edit.
internal static class SuruTypeSystem
{
    // Returns null for unrecognised types; callers decide how to report the error.
    public static SuruType? TryResolve(
        TypeAnnotation ann,
        IReadOnlyDictionary<string, TypeDeclaration> typeDecls) => ann.Name switch
    {
        BuiltinNames.Bool    => SuruType.Bool,
        BuiltinNames.Int32   => SuruType.Int32,
        BuiltinNames.Int64   => SuruType.Int64,
        BuiltinNames.Float64 => SuruType.Float64,
        BuiltinNames.String  => SuruType.String,
        BuiltinNames.Array   => ann.TypeParam is { } tp
                        ? new SuruType.ArrayType(TryResolve(tp, typeDecls) ?? SuruType.Int64)
                        : null,
        // Named types declared via `type Foo: { ... }` resolve to NamedType("Foo").
        _ => typeDecls.ContainsKey(ann.Name) ? new SuruType.NamedType(ann.Name) : null,
    };
}
