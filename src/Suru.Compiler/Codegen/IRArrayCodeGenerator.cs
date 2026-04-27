// Covered fixtures: arrays
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Array IR emission for IRCodeGenerator.
//
// ── Representation ───────────────────────────────────────────────────────────
//
// Every Suru Array value is a `ptr` to a heap-allocated
//   %suru.Array = { i64 len, i64 cap, ptr data }   (24 bytes)
//
// `data` is a flat i64[] buffer (8 bytes per element regardless of element type).
// Elements of any scalar type are stored as 64-bit integers; pointer types (String,
// Struct, Array) are stored via ptrtoint/inttoptr.
//
// ── Runtime module ────────────────────────────────────────────────────────────
//
// All non-trivial array operations are implemented in suru_array.ll:
//   at, set, add (with amortised growth), slice,
//   clone_scalar/string/struct, drop_scalar/string/struct.
//
// The user's .ll only emits `declare` stubs via _runtimeDecls. The caller is
// responsible for applying ToI64/FromI64 before/after calling set/add/at.
//
// ── What stays inline ─────────────────────────────────────────────────────────
//
//   EmitExtractArrayLen/Cap/Data — simple GEP+load helpers used by EmitArrayLiteral.
//   EmitArrayLiteral             — constructs an array from inline literal values.
//   EmitToI64 / EmitFromI64      — element-type-specific bit conversions.
//   EmitArrayLen                 — .len() is a 2-instruction GEP+load; not worth a call.
//
// ── argv vs regular arrays ─────────────────────────────────────────────────────
//
// The `args` parameter of suru_main holds a %suru.Seq (not %suru.Array) where
// data = argv (char**). It is never registered in _arrayElementTypes, so Array
// dispatch in EmitMethodCall falls through to EmitArgAt.
partial class IRCodeGenerator
{
    // ─── Low-level %suru.Array GEP helpers ──────────────────────────────────
    //
    // These remain inline because they are used during array literal construction,
    // where inlining avoids unnecessary function call overhead for simple field loads.

    // Load the `len` field (field index 0) from a %suru.Array header.
    private string EmitExtractArrayLen(string arrVal)
    {
        var gep = NextTmp();
        var len = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 0");
        _funcs.AppendLine($"  {len} = load i64, ptr {gep}");
        return len;
    }

    // Load the `cap` field (field index 1) from a %suru.Array header.
    private string EmitExtractArrayCap(string arrVal)
    {
        var gep = NextTmp();
        var cap = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 1");
        _funcs.AppendLine($"  {cap} = load i64, ptr {gep}");
        return cap;
    }

    // Load the `data` pointer (field index 2) from a %suru.Array header.
    private string EmitExtractArrayData(string arrVal)
    {
        var gep  = NextTmp();
        var data = NextTmp();
        _funcs.AppendLine($"  {gep}  = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 2");
        _funcs.AppendLine($"  {data} = load ptr, ptr {gep}");
        return data;
    }

    // ─── Array literal ───────────────────────────────────────────────────────

    // Emit a `[e1, e2, ...]` array literal.
    //
    // Allocates a 24-byte %suru.Array header. For empty literals the data ptr is
    // stored as null and cap=0; for non-empty literals: malloc count*8 bytes, emit
    // each element via EmitToI64, store at GEP-indexed slots, build the header.
    //
    // Sets _pendingArrayElemType so the enclosing LetStatement can record the element
    // type in _arrayElementTypes.
    private (string val, SuruType type) EmitArrayLiteral(ArrayLiteralExpression lit)
    {
        var hdrPtr = NextTmp();
        _funcs.AppendLine($"  {hdrPtr} = call ptr @malloc(i64 24)");

        string dataPtr;
        SuruType elemType = SuruType.Int64;
        var count = lit.Elements.Count;

        if (count == 0)
        {
            dataPtr = "null";
        }
        else
        {
            var (firstVal, firstType) = EmitValue(lit.Elements[0]);
            elemType = firstType;

            var byteCount = (count * 8).ToString();
            var rawData = NextTmp();
            _funcs.AppendLine($"  {rawData} = call ptr @malloc(i64 {byteCount})");
            dataPtr = rawData;

            var slot0 = NextTmp();
            _funcs.AppendLine($"  {slot0} = getelementptr i64, ptr {dataPtr}, i64 0");
            var as64_0 = EmitToI64(firstVal, firstType);
            _funcs.AppendLine($"  store i64 {as64_0}, ptr {slot0}");

            for (int i = 1; i < count; i++)
            {
                var (elemVal, elemValType) = EmitValue(lit.Elements[i]);
                var slot = NextTmp();
                _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {dataPtr}, i64 {i}");
                var as64 = EmitToI64(elemVal, elemValType);
                _funcs.AppendLine($"  store i64 {as64}, ptr {slot}");
            }
        }

        // Build %suru.Array header: { len, cap, data }.
        var lenGep  = NextTmp();
        var capGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {count}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {count}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 2");
        _funcs.AppendLine($"  store ptr {dataPtr}, ptr {dataGep}");

        _pendingArrayElemType = elemType;
        return (hdrPtr, SuruType.Array);
    }

    // ─── Array instance methods ──────────────────────────────────────────────

    // .len() → Int64: load the len field inline (2 instructions; no runtime call needed).
    private (string val, SuruType type) EmitArrayLen(string arrVal)
        => (EmitExtractArrayLen(arrVal), SuruType.Int64);

    // .at(i) → elemType: call @suru_array_at to load the raw i64, then apply FromI64.
    private (string val, SuruType type) EmitArrayAt(
        string arrVal, SuruType elemType, Expression idxExpr)
    {
        var (idx, _) = EmitValue(idxExpr);
        _runtimeDecls.AddArrayAt();
        var raw = NextTmp();
        _funcs.AppendLine($"  {raw} = call i64 @suru_array_at(ptr {arrVal}, i64 {idx})");
        var result = EmitFromI64(raw, elemType);
        return (result, elemType);
    }

    // .set(val, i) — apply ToI64, then call @suru_array_set.
    // Returns (0, Bool) as a side-effect-only method.
    private (string val, SuruType type) EmitArraySet(
        string arrVal, SuruType elemType, Expression valExpr, Expression idxExpr)
    {
        var (val, _) = EmitValue(valExpr);
        var (idx, _) = EmitValue(idxExpr);
        var as64 = EmitToI64(val, elemType);
        _runtimeDecls.AddArraySet();
        _funcs.AppendLine($"  call void @suru_array_set(ptr {arrVal}, i64 {idx}, i64 {as64})");
        return ("0", SuruType.Bool);
    }

    // .add(v) — apply ToI64, then call @suru_array_add which grows the header in-place.
    // Returns (0, Bool) as a side-effect-only method.
    private (string val, SuruType type) EmitArrayAdd(
        string arrVal, SuruType elemType, Expression valExpr)
    {
        var (val, _) = EmitValue(valExpr);
        var as64 = EmitToI64(val, elemType);
        _runtimeDecls.AddArrayAdd();
        _funcs.AppendLine($"  call void @suru_array_add(ptr {arrVal}, i64 {as64})");
        return ("0", SuruType.Bool);
    }

    // .slice(from, to) → Array: bitwise copy of elements [from, to) via @suru_array_slice.
    // Sets _pendingArrayElemType so the LetStatement handler propagates the element type.
    private (string val, SuruType type) EmitArraySlice(
        string arrVal, Expression receiverExpr, SuruType elemType,
        Expression fromExpr, Expression toExpr)
    {
        var (from, _) = EmitValue(fromExpr);
        var (to, _)   = EmitValue(toExpr);
        _runtimeDecls.AddArraySlice();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_array_slice(ptr {arrVal}, i64 {from}, i64 {to})");
        _pendingArrayElemType = elemType;
        return (tmp, SuruType.Array);
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    // Dispatch helper: determine element type, then call the appropriate runtime variant.
    //
    //   Scalar (Int64, Float64, Bool, nested Array) → suru_array_clone_scalar
    //   String                                       → suru_array_clone_string
    //   Struct                                       → suru_array_clone_struct
    //
    // Nested Array elements are cloned shallowly (scalar variant) because the nested
    // element type is not tracked at the SSA-value level — only by variable name.
    private (string val, SuruType type) EmitCloneArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        var elemType = arg is VariableReferenceExpression { Name: var n }
                       && _arrayElementTypes.TryGetValue(n, out var et) ? et : SuruType.Int64;

        var tmp = NextTmp();
        switch (elemType)
        {
            case SuruType.String:
                _runtimeDecls.AddArrayCloneString();
                _funcs.AppendLine($"  {tmp} = call ptr @suru_array_clone_string(ptr {arrVal})");
                break;
            case SuruType.Struct:
                _runtimeDecls.AddArrayCloneStruct();
                _funcs.AppendLine($"  {tmp} = call ptr @suru_array_clone_struct(ptr {arrVal})");
                break;
            default: // Int64, Float64, Bool, Array (shallow)
                _runtimeDecls.AddArrayCloneScalar();
                _funcs.AppendLine($"  {tmp} = call ptr @suru_array_clone_scalar(ptr {arrVal})");
                break;
        }
        return (tmp, SuruType.Array);
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    // Dispatch helper: determine element type, then call the appropriate runtime variant.
    //
    //   Scalar (Int64, Float64, Bool, nested Array) → suru_array_drop_scalar
    //   String                                       → suru_array_drop_string
    //   Struct                                       → suru_array_drop_struct
    //
    // Returns ("0", Bool) so the call can appear in expression position.
    private (string val, SuruType type) EmitDropArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        var elemType = arg is VariableReferenceExpression { Name: var n }
                       && _arrayElementTypes.TryGetValue(n, out var et) ? et : SuruType.Int64;

        switch (elemType)
        {
            case SuruType.String:
                _runtimeDecls.AddArrayDropString();
                _funcs.AppendLine($"  call void @suru_array_drop_string(ptr {arrVal})");
                break;
            case SuruType.Struct:
                _runtimeDecls.AddArrayDropStruct();
                _funcs.AppendLine($"  call void @suru_array_drop_struct(ptr {arrVal})");
                break;
            default: // Int64, Float64, Bool, Array (shallow)
                _runtimeDecls.AddArrayDropScalar();
                _funcs.AppendLine($"  call void @suru_array_drop_scalar(ptr {arrVal})");
                break;
        }
        return ("0", SuruType.Bool);
    }

    // ─── Element type conversion ─────────────────────────────────────────────
    //
    // These helpers stay inline because the conversion instruction depends on the
    // element type, which is known at C# codegen time. Moving them to the runtime
    // would require runtime type dispatch (the tag field), which is both slower and
    // more complex.

    // Convert a typed Suru value to i64 for storage in the i64[] data buffer.
    // Returns the SSA name of the i64 result (may be the same SSA name if Int64).
    //
    // Bool    → zext i1 to i64         (1 bit → 64 bits, zero-extended)
    // Int64   → identity               (already i64)
    // Float64 → bitcast double to i64  (reinterpret IEEE-754 bits, no rounding)
    // Ptr     → ptrtoint ptr to i64    (pointer address as integer; 64-bit platforms only)
    private string EmitToI64(string val, SuruType type)
    {
        if (type == SuruType.Int64) return val;

        var tmp = NextTmp();
        var instr = type switch
        {
            SuruType.Bool    => $"zext i1 {val} to i64",
            SuruType.Float64 => $"bitcast double {val} to i64",
            _                => $"ptrtoint ptr {val} to i64",   // String, Array, Struct
        };
        _funcs.AppendLine($"  {tmp} = {instr}");
        return tmp;
    }

    // Restore a typed Suru value from the raw i64 stored in the data buffer.
    // Inverse of EmitToI64.
    //
    // Bool    → trunc i64 to i1        (keep low bit)
    // Int64   → identity
    // Float64 → bitcast i64 to double  (reinterpret bits back to IEEE-754)
    // Ptr     → inttoptr i64 to ptr    (integer → pointer; same address)
    private string EmitFromI64(string raw, SuruType type)
    {
        if (type == SuruType.Int64) return raw;

        var tmp = NextTmp();
        var instr = type switch
        {
            SuruType.Bool    => $"trunc i64 {raw} to i1",
            SuruType.Float64 => $"bitcast i64 {raw} to double",
            _                => $"inttoptr i64 {raw} to ptr",   // String, Array, Struct
        };
        _funcs.AppendLine($"  {tmp} = {instr}");
        return tmp;
    }
}
