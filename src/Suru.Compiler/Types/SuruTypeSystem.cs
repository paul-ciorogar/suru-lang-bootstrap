using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Types;

// Single source of truth for TypeAnnotation → SuruType resolution.
// Both the semantic analyzer (returns null on failure) and the codegen
// (throws on failure) delegate here so a new type only needs one edit.
internal static class SuruTypeSystem
{
    // Returns null for unrecognised types; callers decide how to report the error.
    // sumTypeDecls is optional — pass null when sum types are not in scope (e.g. codegen
    // paths that predate Stage 13g).
    public static SuruType? TryResolve(
        TypeAnnotation ann,
        IReadOnlyDictionary<string, TypeDeclaration> typeDecls,
        IReadOnlyDictionary<string, SumTypeDeclaration>? sumTypeDecls = null) => ann.Name switch
    {
        BuiltinNames.Bool    => SuruType.Bool,
        BuiltinNames.Int32   => SuruType.Int32,
        BuiltinNames.Int64   => SuruType.Int64,
        BuiltinNames.Float64 => SuruType.Float64,
        BuiltinNames.String  => SuruType.String,
        BuiltinNames.Array   => ann.TypeParam is { } tp
                        ? new SuruType.ArrayType(TryResolve(tp, typeDecls, sumTypeDecls) ?? SuruType.Int64)
                        : null,
        // Named struct types declared via `type Foo: { ... }` resolve to NamedType("Foo").
        // Named sum types declared via `type Bar: A, B` resolve to SumType("Bar", [...]).
        _ => typeDecls.ContainsKey(ann.Name)
                ? new SuruType.NamedType(ann.Name)
                : sumTypeDecls?.ContainsKey(ann.Name) == true
                    ? new SuruType.SumType(ann.Name, sumTypeDecls[ann.Name].Variants)
                    : null,
    };
}
