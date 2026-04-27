using System.Text;

namespace Suru.Compiler.Codegen;

// Tracks Suru runtime function declarations needed by an emitted user module.
//
// The runtime functions live in suru_string.ll, suru_array.ll, and suru_struct.ll —
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

    // ─── String runtime (suru_string.ll) ─────────────────────────────────────
    private bool _stringCreate, _stringClone, _stringDrop;
    private bool _stringAppend, _stringAt, _stringEquals, _stringSlice, _stringOrd;
    private bool _int64FromString, _int64ToString;

    // ─── Array runtime (suru_array.ll) ───────────────────────────────────────
    private bool _arrayAt, _arraySet, _arrayAdd, _arraySlice;
    private bool _arrayCloneScalar, _arrayCloneString, _arrayCloneStruct;
    private bool _arrayDropScalar, _arrayDropString, _arrayDropStruct;

    // ─── Struct runtime (suru_struct.ll) ─────────────────────────────────────
    private bool _findField, _structClone, _structDrop;

    public override string ToString() => _sb.ToString();

    // ─── String ──────────────────────────────────────────────────────────────

    // Allocate a new %suru.Seq header and store the given data ptr and length.
    internal void AddStringCreate()
    {
        if (_stringCreate) return;
        _sb.AppendLine("declare ptr  @suru_string_create(ptr, i64)");
        _stringCreate = true;
    }

    // Produce an independent copy of a String (new Seq header + new heap char buffer).
    internal void AddStringClone()
    {
        if (_stringClone) return;
        _sb.AppendLine("declare ptr  @suru_string_clone(ptr)");
        _stringClone = true;
    }

    // Free the char buffer and then the Seq header.
    internal void AddStringDrop()
    {
        if (_stringDrop) return;
        _sb.AppendLine("declare void @suru_string_drop(ptr)");
        _stringDrop = true;
    }

    // Concatenate two Strings into a new heap-allocated String.
    internal void AddStringAppend()
    {
        if (_stringAppend) return;
        _sb.AppendLine("declare ptr  @suru_string_append(ptr, ptr)");
        _stringAppend = true;
    }

    // Return a single-character String at byte index i.
    internal void AddStringAt()
    {
        if (_stringAt) return;
        _sb.AppendLine("declare ptr  @suru_string_at(ptr, i64)");
        _stringAt = true;
    }

    // Byte-exact comparison via strcmp; returns i1.
    internal void AddStringEquals()
    {
        if (_stringEquals) return;
        _sb.AppendLine("declare i1   @suru_string_equals(ptr, ptr)");
        _stringEquals = true;
    }

    // Return the substring covering bytes [from, to).
    internal void AddStringSlice()
    {
        if (_stringSlice) return;
        _sb.AppendLine("declare ptr  @suru_string_slice(ptr, i64, i64)");
        _stringSlice = true;
    }

    // Return the ASCII code of the first byte as i64.
    internal void AddStringOrd()
    {
        if (_stringOrd) return;
        _sb.AppendLine("declare i64  @suru_string_ord(ptr)");
        _stringOrd = true;
    }

    // Parse a decimal string to i64 via strtol.
    internal void AddInt64FromString()
    {
        if (_int64FromString) return;
        _sb.AppendLine("declare i64  @suru_int64_from_string(ptr)");
        _int64FromString = true;
    }

    // Format an i64 as a decimal String via snprintf.
    internal void AddInt64ToString()
    {
        if (_int64ToString) return;
        _sb.AppendLine("declare ptr  @suru_int64_to_string(i64)");
        _int64ToString = true;
    }

    // ─── Array ───────────────────────────────────────────────────────────────

    // Load the raw i64 at element index idx; caller applies FromI64 for the element type.
    internal void AddArrayAt()
    {
        if (_arrayAt) return;
        _sb.AppendLine("declare i64  @suru_array_at(ptr, i64)");
        _arrayAt = true;
    }

    // Store a raw i64 at element index idx; caller applies ToI64 before calling.
    internal void AddArraySet()
    {
        if (_arraySet) return;
        _sb.AppendLine("declare void @suru_array_set(ptr, i64, i64)");
        _arraySet = true;
    }

    // Append a raw i64 to the array, growing the data buffer when needed.
    internal void AddArrayAdd()
    {
        if (_arrayAdd) return;
        _sb.AppendLine("declare void @suru_array_add(ptr, i64)");
        _arrayAdd = true;
    }

    // Return a new array header containing a bitwise copy of elements [from, to).
    internal void AddArraySlice()
    {
        if (_arraySlice) return;
        _sb.AppendLine("declare ptr  @suru_array_slice(ptr, i64, i64)");
        _arraySlice = true;
    }

    // Bitwise memcpy of the data buffer — for scalar element types (Int64, Float64, Bool).
    internal void AddArrayCloneScalar()
    {
        if (_arrayCloneScalar) return;
        _sb.AppendLine("declare ptr  @suru_array_clone_scalar(ptr)");
        _arrayCloneScalar = true;
    }

    // Clone each String element via suru_string_clone.
    internal void AddArrayCloneString()
    {
        if (_arrayCloneString) return;
        _sb.AppendLine("declare ptr  @suru_array_clone_string(ptr)");
        _arrayCloneString = true;
    }

    // Clone each Struct element via suru_struct_clone.
    internal void AddArrayCloneStruct()
    {
        if (_arrayCloneStruct) return;
        _sb.AppendLine("declare ptr  @suru_array_clone_struct(ptr)");
        _arrayCloneStruct = true;
    }

    // Free the data buffer and the %suru.Array header — for scalar element types.
    internal void AddArrayDropScalar()
    {
        if (_arrayDropScalar) return;
        _sb.AppendLine("declare void @suru_array_drop_scalar(ptr)");
        _arrayDropScalar = true;
    }

    // Drop each String element via suru_string_drop, then free the buffer and header.
    internal void AddArrayDropString()
    {
        if (_arrayDropString) return;
        _sb.AppendLine("declare void @suru_array_drop_string(ptr)");
        _arrayDropString = true;
    }

    // Drop each Struct element via suru_struct_drop, then free the buffer and header.
    internal void AddArrayDropStruct()
    {
        if (_arrayDropStruct) return;
        _sb.AppendLine("declare void @suru_array_drop_struct(ptr)");
        _arrayDropStruct = true;
    }

    // ─── Struct ──────────────────────────────────────────────────────────────

    // Walk the linked list via strcmp and return the matching field node ptr.
    internal void AddFindField()
    {
        if (_findField) return;
        _sb.AppendLine("declare ptr  @suru_find_field(ptr, ptr)");
        _findField = true;
    }

    // Deep-copy a struct field-node linked list.
    internal void AddStructClone()
    {
        if (_structClone) return;
        _sb.AppendLine("declare ptr  @suru_struct_clone(ptr)");
        _structClone = true;
    }

    // Free all field nodes in a struct linked list.
    internal void AddStructDrop()
    {
        if (_structDrop) return;
        _sb.AppendLine("declare void @suru_struct_drop(ptr)");
        _structDrop = true;
    }
}
