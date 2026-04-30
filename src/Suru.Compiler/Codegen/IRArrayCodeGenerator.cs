// Covered fixtures: arrays
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Array IR emission for IRCodeGenerator.
//
// ── Representation ───────────────────────────────────────────────────────────
//
// Every Suru Array value is a `ptr` to a heap-allocated
//   %suru.Array = { i64 type_tag=5, i64 elem_tag, i64 len, i64 cap, ptr data }  (40 bytes)
//
// type_tag=5 at field 0: any heap ptr can be identified as Array at runtime.
// elem_tag holds the full SuruType ordinal (0-6) for the element type.
// data is a flat i64[] buffer; each element is a ptr-as-i64 (Box ptr for scalars,
// direct heap ptr for String/Array/Struct — all converted via ptrtoint/inttoptr).
//
// ── Runtime module ────────────────────────────────────────────────────────────
//
// All non-trivial array operations are implemented in suru_array.ll:
//   at (returns ptr), set/add (take ptr), slice,
//   clone_dyn (dispatch on element type_tag), drop_dyn.
//
// The user's .ll only emits `declare` stubs via _runtimeDecls. Callers pass Box ptrs
// for scalars and direct ptrs for heap types — no EmitToI64/EmitFromI64 dispatch needed.
//
// ── What stays inline ─────────────────────────────────────────────────────────
//
//   EmitExtractArrayLen/Cap/Data — simple GEP+load helpers used by EmitArrayLiteral.
//   EmitArrayLiteral             — constructs an array from inline literal values.
//   EmitToI64 / EmitFromI64      — trivial ptrtoint/inttoptr (all values are ptr).
//   EmitArrayLen                 — .len() is a GEP+load+BoxInt64; no call needed.
//
// ── argv vs regular arrays ─────────────────────────────────────────────────────
//
// The `args` parameter of suru_main holds a %suru.String (not %suru.Array) where
// data = argv (char**). It is tracked in _argvVars, so Array dispatch in EmitMethodCall
// falls through to EmitArgAt instead of the regular array methods.
partial class IRCodeGenerator
{
    // ─── Low-level %suru.Array GEP helpers ──────────────────────────────────
    //
    // GEP indices shifted by 1 compared to the old layout because type_tag is
    // now at field 0. New layout: { type_tag@0, elem_tag@1, len@2, cap@3, data@4 }

    private string EmitExtractArrayLen(string arrVal)
    {
        var gep = NextTmp();
        var len = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 2");
        _funcs.AppendLine($"  {len} = load i64, ptr {gep}");
        return len;
    }

    private string EmitExtractArrayCap(string arrVal)
    {
        var gep = NextTmp();
        var cap = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 3");
        _funcs.AppendLine($"  {cap} = load i64, ptr {gep}");
        return cap;
    }

    private string EmitExtractArrayData(string arrVal)
    {
        var gep  = NextTmp();
        var data = NextTmp();
        _funcs.AppendLine($"  {gep}  = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 4");
        _funcs.AppendLine($"  {data} = load ptr, ptr {gep}");
        return data;
    }

    // ─── Array literal ───────────────────────────────────────────────────────

    // Emit a `[e1, e2, ...]` array literal.
    //
    // Allocates a 40-byte %suru.Array header. For empty literals the data ptr is
    // stored as null and cap=0; for non-empty literals: malloc count*8 bytes, emit
    // each element as ptrtoint(ptr) and store at GEP-indexed slots, build the header.
    private (string val, SuruType type) EmitArrayLiteral(ArrayLiteralExpression lit)
    {
        var hdrPtr = NextTmp();
        _funcs.AppendLine($"  {hdrPtr} = call ptr @malloc(i64 40)");

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

        // Build %suru.Array header: { type_tag=5, elem_tag, len, cap, data }
        var ttagGep = NextTmp();
        var etagGep = NextTmp();
        var lenGep  = NextTmp();
        var capGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {ttagGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 5, ptr {ttagGep}");
        _funcs.AppendLine($"  {etagGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {ElemTag(elemType)}, ptr {etagGep}");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 2");
        _funcs.AppendLine($"  store i64 {count}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 3");
        _funcs.AppendLine($"  store i64 {count}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 4");
        _funcs.AppendLine($"  store ptr {dataPtr}, ptr {dataGep}");

        return (hdrPtr, SuruType.Array);
    }

    // Full SuruType ordinal as elem_tag (matches the unified type_tag enum: 0=Bool ... 6=String).
    private static int ElemTag(SuruType t) => (int)t;

    // ─── Array instance methods ──────────────────────────────────────────────

    // .len() → Box(Int64): GEP+load the len field, then box the raw i64.
    private (string val, SuruType type) EmitArrayLen(string arrVal)
    {
        var raw = EmitExtractArrayLen(arrVal);
        return (BoxInt64(raw), SuruType.Int64);
    }

    // .len() on a value of unknown compile-time kind (Struct default).
    // Calls @suru_dyn_len which reads type_tag at offset 0 and dispatches:
    //   tag=5 (Array) → Array.len field (offset 16)
    //   tag=6 (String) → String.len field (offset 8)
    private (string val, SuruType type) EmitDynLen(string val)
    {
        _runtimeDecls.AddDynLen();
        var raw = NextTmp();
        _funcs.AppendLine($"  {raw} = call i64 @suru_dyn_len(ptr {val})");
        return (BoxInt64(raw), SuruType.Int64);
    }

    // .at(i) → ptr: unbox idx Box(Int64), call @suru_array_at which returns ptr directly.
    // The returned ptr is a Box for scalars or a direct heap ptr for String/Array/Struct.
    private (string val, SuruType type) EmitArrayAt(string arrVal, Expression idxExpr)
    {
        var (idxBox, _) = EmitValue(idxExpr);
        var idx = UnboxInt64(idxBox);
        _runtimeDecls.AddArrayAt();
        var result = NextTmp();
        _funcs.AppendLine($"  {result} = call ptr @suru_array_at(ptr {arrVal}, i64 {idx})");
        return (result, SuruType.Struct);   // element type unknown at compile time
    }

    // .set(val, i) — unbox idx Box(Int64), pass val as ptr to @suru_array_set.
    private (string val, SuruType type) EmitArraySet(
        string arrVal, Expression valExpr, Expression idxExpr)
    {
        var (val, _)    = EmitValue(valExpr);
        var (idxBox, _) = EmitValue(idxExpr);
        var idx = UnboxInt64(idxBox);
        _runtimeDecls.AddArraySet();
        _funcs.AppendLine($"  call void @suru_array_set(ptr {arrVal}, i64 {idx}, ptr {val})");
        return ("null", SuruType.Bool);
    }

    // .add(v) — pass val as ptr to @suru_array_add which grows the header in-place.
    private (string val, SuruType type) EmitArrayAdd(string arrVal, Expression valExpr)
    {
        var (val, _) = EmitValue(valExpr);
        _runtimeDecls.AddArrayAdd();
        _funcs.AppendLine($"  call void @suru_array_add(ptr {arrVal}, ptr {val})");
        return ("null", SuruType.Bool);
    }

    // .slice(from, to) → Array: unbox from/to Box(Int64), call @suru_array_slice.
    private (string val, SuruType type) EmitArraySlice(
        string arrVal, Expression fromExpr, Expression toExpr)
    {
        var (fromBox, _) = EmitValue(fromExpr);
        var (toBox, _)   = EmitValue(toExpr);
        var from = UnboxInt64(fromBox);
        var to   = UnboxInt64(toBox);
        _runtimeDecls.AddArraySlice();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_array_slice(ptr {arrVal}, i64 {from}, i64 {to})");
        return (tmp, SuruType.Array);
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    // suru_array_clone_dyn reads each element's type_tag at runtime and dispatches.
    private (string val, SuruType type) EmitCloneArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        _runtimeDecls.AddArrayCloneDyn();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_array_clone_dyn(ptr {arrVal})");
        return (tmp, SuruType.Array);
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    // suru_array_drop_dyn reads each element's type_tag at runtime and dispatches.
    private (string val, SuruType type) EmitDropArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        _runtimeDecls.AddArrayDropDyn();
        _funcs.AppendLine($"  call void @suru_array_drop_dyn(ptr {arrVal})");
        return ("null", SuruType.Bool);
    }

    // ─── Element type conversion ─────────────────────────────────────────────
    //
    // With the universal tagged-pointer system, all Suru values are `ptr` (Box ptrs
    // for scalars, direct heap ptrs for String/Array/Struct). Storage in the i64[]
    // data buffer uses ptrtoint/inttoptr — no type-specific dispatch needed.

    // Store a ptr value as i64 in the data buffer (ptrtoint for any ptr type).
    private string EmitToI64(string val, SuruType type)
    {
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = ptrtoint ptr {val} to i64");
        return tmp;
    }

    // Restore a ptr value from the raw i64 stored in the data buffer (inttoptr).
    private string EmitFromI64(string raw, SuruType type)
    {
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = inttoptr i64 {raw} to ptr");
        return tmp;
    }
}
