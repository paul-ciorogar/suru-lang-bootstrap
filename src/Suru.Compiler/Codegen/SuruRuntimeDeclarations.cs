using System.Text;

namespace Suru.Compiler.Codegen;

// Tracks Suru runtime function declarations needed by an emitted user module.
//
// The runtime functions live in suru_box.ll, suru_string.ll, suru_array.ll, and suru_struct.ll —
// each compiled to its own .o and linked with every Suru program. The user's .ll
// only needs `declare` stubs so the call instructions typecheck; the linker resolves
// the symbols at link time.
//
// Each Add* method is idempotent — callers invoke it whenever they emit a call
// instruction that references the symbol. ToString() returns the full declare block
// ready for insertion into the .ll file, after the C extern declarations.
internal sealed class SuruRuntimeDeclarations
{
    private readonly StringBuilder _sb = new();

    // ─── Box runtime (suru_box.ll) ────────────────────────────────────────────
    private bool _boxBool, _boxInt32, _boxInt64, _boxFloat64;
    private bool _unboxBool, _unboxInt32, _unboxInt64, _unboxFloat64;
    private bool _boxClone;
    private bool _suruPrintln, _suruPrintError;

    // ─── String runtime (suru_string.ll) ─────────────────────────────────────
    private bool _stringCreate, _stringClone, _stringDrop;
    private bool _stringAppend, _stringAt, _stringEquals, _stringSlice, _stringOrd;
    private bool _int64FromString, _int64ToString;

    // ─── Array runtime (suru_array.ll) ───────────────────────────────────────
    private bool _arrayAt, _arraySet, _arrayAdd, _arraySlice;
    private bool _arrayCloneDyn, _arrayDropDyn;

    // ─── Struct runtime (suru_struct.ll) ─────────────────────────────────────
    private bool _findField, _structClone, _structDrop;
    private bool _cloneDyn, _dropDyn;
    private bool _dynLen;

    // ─── Variant runtime (suru_variant.ll) ───────────────────────────────────
    private bool _variantCreate, _variantTag, _variantInner, _variantDrop;

    public override string ToString() => _sb.ToString();

    // ─── Box ─────────────────────────────────────────────────────────────────

    internal void AddBoxBool()
    {
        if (_boxBool) return;
        _sb.AppendLine("declare ptr  @suru_box_bool(i1)");
        _boxBool = true;
    }

    internal void AddBoxInt32()
    {
        if (_boxInt32) return;
        _sb.AppendLine("declare ptr  @suru_box_int32(i32)");
        _boxInt32 = true;
    }

    internal void AddBoxInt64()
    {
        if (_boxInt64) return;
        _sb.AppendLine("declare ptr  @suru_box_int64(i64)");
        _boxInt64 = true;
    }

    internal void AddBoxFloat64()
    {
        if (_boxFloat64) return;
        _sb.AppendLine("declare ptr  @suru_box_float64(double)");
        _boxFloat64 = true;
    }

    internal void AddUnboxBool()
    {
        if (_unboxBool) return;
        _sb.AppendLine("declare i1   @suru_unbox_bool(ptr)");
        _unboxBool = true;
    }

    internal void AddUnboxInt32()
    {
        if (_unboxInt32) return;
        _sb.AppendLine("declare i32  @suru_unbox_int32(ptr)");
        _unboxInt32 = true;
    }

    internal void AddUnboxInt64()
    {
        if (_unboxInt64) return;
        _sb.AppendLine("declare i64  @suru_unbox_int64(ptr)");
        _unboxInt64 = true;
    }

    internal void AddUnboxFloat64()
    {
        if (_unboxFloat64) return;
        _sb.AppendLine("declare double @suru_unbox_float64(ptr)");
        _unboxFloat64 = true;
    }

    internal void AddBoxClone()
    {
        if (_boxClone) return;
        _sb.AppendLine("declare ptr  @suru_box_clone(ptr)");
        _boxClone = true;
    }

    internal void AddSuruPrintln()
    {
        if (_suruPrintln) return;
        _sb.AppendLine("declare void @suru_println(ptr)");
        _suruPrintln = true;
    }

    internal void AddSuruPrintError()
    {
        if (_suruPrintError) return;
        _sb.AppendLine("declare void @suru_printerror(ptr)");
        _suruPrintError = true;
    }

    // ─── String ──────────────────────────────────────────────────────────────

    internal void AddStringCreate()
    {
        if (_stringCreate) return;
        _sb.AppendLine("declare ptr  @suru_string_create(ptr, i64)");
        _stringCreate = true;
    }

    internal void AddStringClone()
    {
        if (_stringClone) return;
        _sb.AppendLine("declare ptr  @suru_string_clone(ptr)");
        _stringClone = true;
    }

    internal void AddStringDrop()
    {
        if (_stringDrop) return;
        _sb.AppendLine("declare void @suru_string_drop(ptr)");
        _stringDrop = true;
    }

    internal void AddStringAppend()
    {
        if (_stringAppend) return;
        _sb.AppendLine("declare ptr  @suru_string_append(ptr, ptr)");
        _stringAppend = true;
    }

    internal void AddStringAt()
    {
        if (_stringAt) return;
        _sb.AppendLine("declare ptr  @suru_string_at(ptr, i64)");
        _stringAt = true;
    }

    internal void AddStringEquals()
    {
        if (_stringEquals) return;
        _sb.AppendLine("declare i1   @suru_string_equals(ptr, ptr)");
        _stringEquals = true;
    }

    internal void AddStringSlice()
    {
        if (_stringSlice) return;
        _sb.AppendLine("declare ptr  @suru_string_slice(ptr, i64, i64)");
        _stringSlice = true;
    }

    internal void AddStringOrd()
    {
        if (_stringOrd) return;
        _sb.AppendLine("declare i64  @suru_string_ord(ptr)");
        _stringOrd = true;
    }

    internal void AddInt64FromString()
    {
        if (_int64FromString) return;
        _sb.AppendLine("declare i64  @suru_int64_from_string(ptr)");
        _int64FromString = true;
    }

    internal void AddInt64ToString()
    {
        if (_int64ToString) return;
        _sb.AppendLine("declare ptr  @suru_int64_to_string(i64)");
        _int64ToString = true;
    }

    // ─── Array ───────────────────────────────────────────────────────────────

    // Load element at idx; returns ptr (Box for scalars, direct ptr for heap types).
    internal void AddArrayAt()
    {
        if (_arrayAt) return;
        _sb.AppendLine("declare ptr  @suru_array_at(ptr, i64)");
        _arrayAt = true;
    }

    // Store val (ptr) at element index idx.
    internal void AddArraySet()
    {
        if (_arraySet) return;
        _sb.AppendLine("declare void @suru_array_set(ptr, i64, ptr)");
        _arraySet = true;
    }

    // Append val (ptr) to the array, growing the data buffer when needed.
    internal void AddArrayAdd()
    {
        if (_arrayAdd) return;
        _sb.AppendLine("declare void @suru_array_add(ptr, ptr)");
        _arrayAdd = true;
    }

    // Return a new array header containing a bitwise copy of elements [from, to).
    internal void AddArraySlice()
    {
        if (_arraySlice) return;
        _sb.AppendLine("declare ptr  @suru_array_slice(ptr, i64, i64)");
        _arraySlice = true;
    }

    // Clone array with dynamic dispatch on each element's type_tag at offset 0.
    internal void AddArrayCloneDyn()
    {
        if (_arrayCloneDyn) return;
        _sb.AppendLine("declare ptr  @suru_array_clone_dyn(ptr)");
        _arrayCloneDyn = true;
    }

    // Drop array with dynamic dispatch on each element's type_tag at offset 0.
    internal void AddArrayDropDyn()
    {
        if (_arrayDropDyn) return;
        _sb.AppendLine("declare void @suru_array_drop_dyn(ptr)");
        _arrayDropDyn = true;
    }

    // ─── Struct ──────────────────────────────────────────────────────────────

    internal void AddFindField()
    {
        if (_findField) return;
        _sb.AppendLine("declare ptr  @suru_find_field(ptr, ptr)");
        _findField = true;
    }

    internal void AddStructClone()
    {
        if (_structClone) return;
        _sb.AppendLine("declare ptr  @suru_struct_clone(ptr)");
        _structClone = true;
    }

    internal void AddStructDrop()
    {
        if (_structDrop) return;
        _sb.AppendLine("declare void @suru_struct_drop(ptr)");
        _structDrop = true;
    }

    internal void AddCloneDyn()
    {
        if (_cloneDyn) return;
        _sb.AppendLine("declare ptr  @suru_clone_dyn(ptr)");
        _cloneDyn = true;
    }

    internal void AddDropDyn()
    {
        if (_dropDyn) return;
        _sb.AppendLine("declare void @suru_drop_dyn(ptr)");
        _dropDyn = true;
    }

    internal void AddDynLen()
    {
        if (_dynLen) return;
        _sb.AppendLine("declare i64  @suru_dyn_len(ptr)");
        _dynLen = true;
    }

    // ─── Variant ──────────────────────────────────────────────────────────────

    internal void AddVariantCreate()
    {
        if (_variantCreate) return;
        _sb.AppendLine("declare ptr  @suru_variant_create(i64, ptr)");
        _variantCreate = true;
    }

    internal void AddVariantTag()
    {
        if (_variantTag) return;
        _sb.AppendLine("declare i64  @suru_variant_tag(ptr)");
        _variantTag = true;
    }

    internal void AddVariantInner()
    {
        if (_variantInner) return;
        _sb.AppendLine("declare ptr  @suru_variant_inner(ptr)");
        _variantInner = true;
    }

    internal void AddVariantDrop()
    {
        if (_variantDrop) return;
        _sb.AppendLine("declare void @suru_variant_drop(ptr)");
        _variantDrop = true;
    }
}
