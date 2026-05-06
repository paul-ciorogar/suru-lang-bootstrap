namespace Suru.Compiler.Types;

// Ordinal values are the canonical type_tag stored at offset 0 of every heap object
// (0=Bool … 6=String). Keep in sync with the unified enum in the four suru_*.ll runtime modules.
public enum SuruType
{
    Bool,
    Int32,
    Int64,
    Float64,
    // Covers every named type declared with `type Foo: { … }` as well as anonymous struct literals.
    // All named types share the same heap-allocated linked-list layout (suru_struct.ll).
    Struct,
    Array,
    String,
}
