namespace Suru.Compiler.Types;

// Type hierarchy for the Suru type system.
//
// TypeTag ordinals at offset 0 of every heap object must stay in sync with the
// unified enum in the four suru_*.ll runtime modules: 0=Bool 1=Int32 2=Int64
// 3=Float64 4=Struct/Named 5=Array 6=String.
//
// All types are fully resolved at compile time — no unknown/sentinel variants.
public abstract class SuruType
{
    public abstract int TypeTag { get; }

    // ── Primitive singletons ─────────────────────────────────────────────────
    public static readonly BoolType    Bool    = new();
    public static readonly Int32Type   Int32   = new();
    public static readonly Int64Type   Int64   = new();
    public static readonly Float64Type Float64 = new();
    public static readonly StringType  String  = new();
    // Void has no TypeTag — void functions emit `ret ptr null` as a codegen convention.
    public static readonly VoidType    Void    = new();

    // ── Subclasses ────────────────────────────────────────────────────────────

    public sealed class BoolType : SuruType
    {
        public const int Tag = 0;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) => obj is BoolType;
        public override int GetHashCode() => Tag;
        public override string ToString() => "Bool";
    }

    public sealed class Int32Type : SuruType
    {
        public const int Tag = 1;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) => obj is Int32Type;
        public override int GetHashCode() => Tag;
        public override string ToString() => "Int32";
    }

    public sealed class Int64Type : SuruType
    {
        public const int Tag = 2;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) => obj is Int64Type;
        public override int GetHashCode() => Tag;
        public override string ToString() => "Int64";
    }

    public sealed class Float64Type : SuruType
    {
        public const int Tag = 3;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) => obj is Float64Type;
        public override int GetHashCode() => Tag;
        public override string ToString() => "Float64";
    }

    // Named struct type — declared via `type Foo: { ... }`.
    // Every NamedType carries a non-empty declared name.
    public sealed class NamedType : SuruType
    {
        public const int Tag = 4;
        public string Name { get; }
        public NamedType(string name) => Name = name;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) => obj is NamedType nt && Name == nt.Name;
        public override int GetHashCode() => HashCode.Combine(Tag, Name);
        public override string ToString() => Name;
    }

    // Array type — carries the statically-known element type.
    public sealed class ArrayType : SuruType
    {
        public const int Tag = 5;
        public SuruType Element { get; }
        public ArrayType(SuruType element) => Element = element;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) =>
            obj is ArrayType at && Equals(Element, at.Element);
        public override int GetHashCode() => HashCode.Combine(Tag, Element);
        public override string ToString() => $"Array<{Element}>";
    }

    public sealed class StringType : SuruType
    {
        public const int Tag = 6;
        public override int TypeTag => Tag;
        public override bool Equals(object? obj) => obj is StringType;
        public override int GetHashCode() => Tag;
        public override string ToString() => "String";
    }

    // Represents the absence of a return value (void functions).
    // VoidType has no TypeTag — void values are never heap-allocated.
    // In LLVM IR, void Suru functions emit `ret ptr null` as a codegen convention.
    public sealed class VoidType : SuruType
    {
        public override int TypeTag =>
            throw new InvalidOperationException("VoidType has no type_tag");
        public override bool Equals(object? obj) => obj is VoidType;
        public override int GetHashCode() => 7;
        public override string ToString() => "void";
    }
}
