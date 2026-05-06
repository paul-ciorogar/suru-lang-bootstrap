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
// elem_tag holds the full SuruType TypeTag for the element type.
// data is a flat i64[] buffer; each element is a ptr-as-i64.
//
// ── Runtime module ────────────────────────────────────────────────────────────
//
// All non-trivial array operations are implemented in suru_array.ll.
partial class IRCodeGenerator
{
    // ─── Low-level %suru.Array GEP helpers ──────────────────────────────────
    // Layout: { type_tag@0, elem_tag@1, len@2, cap@3, data@4 }

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

        var ttagGep = NextTmp(); var etagGep = NextTmp();
        var lenGep  = NextTmp(); var capGep  = NextTmp(); var dataGep = NextTmp();
        _funcs.AppendLine($"  {ttagGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 5, ptr {ttagGep}");
        _funcs.AppendLine($"  {etagGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {elemType.TypeTag}, ptr {etagGep}");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 2");
        _funcs.AppendLine($"  store i64 {count}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 3");
        _funcs.AppendLine($"  store i64 {count}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 4");
        _funcs.AppendLine($"  store ptr {dataPtr}, ptr {dataGep}");

        return (hdrPtr, new SuruType.ArrayType(elemType));
    }

    // ─── Array instance methods ──────────────────────────────────────────────

    private (string val, SuruType type) EmitArrayLen(string arrVal)
    {
        var raw = EmitExtractArrayLen(arrVal);
        return (raw, SuruType.Int64);
    }

    private (string val, SuruType type) EmitDynLen(string val)
    {
        _runtimeDecls.AddDynLen();
        var raw = NextTmp();
        _funcs.AppendLine($"  {raw} = call i64 @suru_dyn_len(ptr {val})");
        return (raw, SuruType.Int64);
    }

    // .at(i) — element type known from the array declaration (ArrayType.Element).
    // Unboxes scalars at the boundary; returns ptr for heap types.
    private (string val, SuruType type) EmitArrayAt(string arrVal, Expression idxExpr,
        SuruType? elemType = null)
    {
        var (idxVal, idxType) = EmitValue(idxExpr);
        var idx = IsScalar(idxType) ? idxVal : UnboxInt64(idxVal);
        _runtimeDecls.AddArrayAt();
        var result = NextTmp();
        _funcs.AppendLine($"  {result} = call ptr @suru_array_at(ptr {arrVal}, i64 {idx})");
        if (elemType is { } et && IsScalar(et))
            return (UnboxScalar(result, et), et);
        if (elemType is { } et2)
            return (result, et2);
        return (result, new SuruType.NamedType(""));
    }

    private (string val, SuruType type) EmitArraySet(
        string arrVal, Expression valExpr, Expression idxExpr)
    {
        var (elemVal, elemType) = EmitValue(valExpr);
        var (idxVal, idxType)   = EmitValue(idxExpr);
        var boxedElem = IsScalar(elemType) ? BoxValue(elemVal, elemType) : elemVal;
        var idx       = IsScalar(idxType)  ? idxVal : UnboxInt64(idxVal);
        _runtimeDecls.AddArraySet();
        _funcs.AppendLine($"  call void @suru_array_set(ptr {arrVal}, i64 {idx}, ptr {boxedElem})");
        return ("0", SuruType.Bool);
    }

    private (string val, SuruType type) EmitArrayAdd(string arrVal, Expression valExpr)
    {
        var (elemVal, elemType) = EmitValue(valExpr);
        var boxedElem = IsScalar(elemType) ? BoxValue(elemVal, elemType) : elemVal;
        _runtimeDecls.AddArrayAdd();
        _funcs.AppendLine($"  call void @suru_array_add(ptr {arrVal}, ptr {boxedElem})");
        return ("0", SuruType.Bool);
    }

    private (string val, SuruType type) EmitArraySlice(
        string arrVal, Expression fromExpr, Expression toExpr, SuruType.ArrayType arrayType)
    {
        var (fromVal, fromType) = EmitValue(fromExpr);
        var (toVal, toType)     = EmitValue(toExpr);
        var from = IsScalar(fromType) ? fromVal : UnboxInt64(fromVal);
        var to   = IsScalar(toType)   ? toVal   : UnboxInt64(toVal);
        _runtimeDecls.AddArraySlice();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_array_slice(ptr {arrVal}, i64 {from}, i64 {to})");
        return (tmp, arrayType);
    }

    // ─── Clone / Drop ────────────────────────────────────────────────────────

    private (string val, SuruType type) EmitCloneArrayDispatch(Expression arg)
    {
        var (arrVal, arrType) = EmitValue(arg);
        _runtimeDecls.AddArrayCloneDyn();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_array_clone_dyn(ptr {arrVal})");
        return (tmp, arrType);
    }

    private (string val, SuruType type) EmitDropArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        _runtimeDecls.AddArrayDropDyn();
        _funcs.AppendLine($"  call void @suru_array_drop_dyn(ptr {arrVal})");
        return ("null", SuruType.Bool);
    }

    // ─── Element type conversion ─────────────────────────────────────────────

    private string EmitToI64(string val, SuruType type)
    {
        var ptrVal = IsScalar(type) ? BoxValue(val, type) : val;
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = ptrtoint ptr {ptrVal} to i64");
        return tmp;
    }

    private string EmitFromI64(string raw, SuruType type)
    {
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = inttoptr i64 {raw} to ptr");
        return tmp;
    }
}
