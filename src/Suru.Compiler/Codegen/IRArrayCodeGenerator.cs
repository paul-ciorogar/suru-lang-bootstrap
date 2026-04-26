// Covered fixtures: arrays
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Array IR emission for IRCodeGenerator.
//
// Every Suru Array value is a `ptr` to a heap-allocated %suru.Seq = { i64 len, ptr data }.
// This is the same header layout as String. The data field points to a flat i64 buffer —
// elements of any scalar type are stored as 64-bit integers regardless of their source type.
// Pointer types (String, Array, Struct) are stored via `ptrtoint ptr to i64` and restored
// via `inttoptr i64 to ptr`. This matches the LLVMSharp CodeGenerator's ToI64/FromI64 pattern.
//
// Layout for `let nums: [10, 20, 30]`:
//
//   malloc 16          → Seq header at %hdr
//   malloc 24          → i64[3] data buffer at %data  (3 × 8 bytes)
//   store i64 10 → data[0]
//   store i64 20 → data[1]
//   store i64 30 → data[2]
//   store i64 3  → hdr.len
//   store ptr %data → hdr.data
//
// Element type tracking:
//   _arrayElementTypes maps variable name → SuruType of its elements.
//   EmitArrayLiteral and EmitArraySlice set _pendingArrayElemType; the LetStatement
//   handler in EmitStmt consumes it and records the mapping. This indirection is needed
//   because EmitValue only returns the Seq ptr and its aggregate SuruType (Array) —
//   there is no channel to carry element type through the (string, SuruType) return tuple.
//
// The argv Seq (args parameter in suru_main) is a special case: its data field is a
// char** (C argv pointer), not an i64 buffer. It is never registered in _arrayElementTypes,
// so the Array dispatch in EmitMethodCall falls through to EmitArgAt for that variable.
partial class IRCodeGenerator
{
    // ─── Array literal ───────────────────────────────────────────────────────

    // Emit a `[e1, e2, ...]` array literal.
    //
    // For an empty literal the data pointer is stored as null — no buffer is allocated.
    // For a non-empty literal: malloc count*8 bytes, emit each element via EmitToI64,
    // store at GEP-indexed slots, then build the Seq header.
    //
    // Sets _pendingArrayElemType so the enclosing LetStatement can record the element
    // type in _arrayElementTypes.
    private (string val, SuruType type) EmitArrayLiteral(ArrayLiteralExpression lit)
    {
        var seqPtr = NextTmp();
        _funcs.AppendLine($"  {seqPtr} = call ptr @malloc(i64 16)");

        string dataPtr;
        SuruType elemType = SuruType.Int64;

        if (lit.Elements.Count == 0)
        {
            dataPtr = "null";
        }
        else
        {
            var (firstVal, firstType) = EmitValue(lit.Elements[0]);
            elemType = firstType;

            // Allocate the flat i64 data buffer.
            var byteCount = (lit.Elements.Count * 8).ToString();
            var rawData = NextTmp();
            _funcs.AppendLine($"  {rawData} = call ptr @malloc(i64 {byteCount})");
            dataPtr = rawData;

            // Store first element (already emitted above).
            var slot0 = NextTmp();
            _funcs.AppendLine($"  {slot0} = getelementptr i64, ptr {dataPtr}, i64 0");
            var as64_0 = EmitToI64(firstVal, firstType);
            _funcs.AppendLine($"  store i64 {as64_0}, ptr {slot0}");

            // Store remaining elements.
            for (int i = 1; i < lit.Elements.Count; i++)
            {
                var (elemVal, elemValType) = EmitValue(lit.Elements[i]);
                var slot = NextTmp();
                _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {dataPtr}, i64 {i}");
                var as64 = EmitToI64(elemVal, elemValType);
                _funcs.AppendLine($"  store i64 {as64}, ptr {slot}");
            }
        }

        // Build Seq header: { len, data }.
        var lenGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Seq, ptr {seqPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {lit.Elements.Count}, ptr {lenGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Seq, ptr {seqPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  store ptr {dataPtr}, ptr {dataGep}");

        _pendingArrayElemType = elemType;
        return (seqPtr, SuruType.Array);
    }

    // ─── Array instance methods ──────────────────────────────────────────────

    // .len() → Int64: load the len field directly from the Seq header.
    private (string val, SuruType type) EmitArrayLen(string seqVal)
        => (EmitExtractStringLen(seqVal), SuruType.Int64);

    // .at(i) → elemType: GEP into the i64 data buffer, load raw i64, apply FromI64.
    //
    // The data field is a flat i64[]; all elements are stored in their 64-bit
    // encoding regardless of type. EmitFromI64 restores the original type from
    // the raw bits (identity for Int64, inttoptr for pointers, trunc for Bool).
    private (string val, SuruType type) EmitArrayAt(
        string seqVal, SuruType elemType, Expression idxExpr)
    {
        var data    = EmitExtractStringData(seqVal);
        var (idx, _) = EmitValue(idxExpr);
        var slot    = NextTmp();
        var raw     = NextTmp();
        _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {data}, i64 {idx}");
        _funcs.AppendLine($"  {raw}  = load i64, ptr {slot}");
        var result = EmitFromI64(raw, elemType);
        return (result, elemType);
    }

    // .set(val, i) — store a value at index i in the data buffer.
    // Returns (0, Bool) as a side-effect-only method — the result is discarded
    // when used as a statement.
    private (string val, SuruType type) EmitArraySet(
        string seqVal, SuruType elemType, Expression valExpr, Expression idxExpr)
    {
        var data     = EmitExtractStringData(seqVal);
        var (val, _) = EmitValue(valExpr);
        var (idx, _) = EmitValue(idxExpr);
        var slot     = NextTmp();
        _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {data}, i64 {idx}");
        var as64 = EmitToI64(val, elemType);
        _funcs.AppendLine($"  store i64 {as64}, ptr {slot}");
        return ("0", SuruType.Bool);
    }

    // .add(v) — append an element, growing the data buffer via realloc.
    //
    // The Seq header is a stable heap object; only the data pointer inside it changes.
    // Strategy:
    //   old_len  = Seq.len
    //   new_len  = old_len + 1
    //   new_data = realloc(Seq.data, new_len * 8)
    //   new_data[old_len] = ToI64(v)
    //   Seq.data = new_data       ← update header via GEP + store
    //   Seq.len  = new_len        ← update header via GEP + store
    //
    // Returns (0, Bool) as a side-effect-only method.
    private (string val, SuruType type) EmitArrayAdd(
        string seqVal, SuruType elemType, Expression valExpr)
    {
        _externals.AddRealloc();

        var oldLen  = EmitExtractStringLen(seqVal);
        var oldData = EmitExtractStringData(seqVal);

        var newLen   = NextTmp();
        var newBytes = NextTmp();
        var newData  = NextTmp();
        _funcs.AppendLine($"  {newLen}   = add i64 {oldLen}, 1");
        _funcs.AppendLine($"  {newBytes} = mul i64 {newLen}, 8");
        _funcs.AppendLine($"  {newData}  = call ptr @realloc(ptr {oldData}, i64 {newBytes})");

        // Store new element at [old_len].
        var (val, _) = EmitValue(valExpr);
        var lastSlot = NextTmp();
        _funcs.AppendLine($"  {lastSlot} = getelementptr i64, ptr {newData}, i64 {oldLen}");
        var as64 = EmitToI64(val, elemType);
        _funcs.AppendLine($"  store i64 {as64}, ptr {lastSlot}");

        // Update Seq header — data ptr may have changed after realloc.
        var dataGep = NextTmp();
        var lenGep  = NextTmp();
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Seq, ptr {seqVal}, i32 0, i32 1");
        _funcs.AppendLine($"  store ptr {newData}, ptr {dataGep}");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Seq, ptr {seqVal}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {newLen}, ptr {lenGep}");

        return ("0", SuruType.Bool);
    }

    // .slice(from, to) → Array: copy elements [from, to) into a new Seq.
    //
    // sliceLen = to - from
    // Allocate sliceLen*8 bytes; memcpy from source data[from]; build new Seq header.
    // Sets _pendingArrayElemType so the LetStatement handler propagates the element type.
    private (string val, SuruType type) EmitArraySlice(
        string seqVal, Expression receiverExpr, SuruType elemType,
        Expression fromExpr, Expression toExpr)
    {
        var data     = EmitExtractStringData(seqVal);
        var (from, _) = EmitValue(fromExpr);
        var (to, _)   = EmitValue(toExpr);

        var sliceLen  = NextTmp();
        var byteCount = NextTmp();
        var srcPtr    = NextTmp();
        var newData   = NextTmp();
        _funcs.AppendLine($"  {sliceLen}  = sub i64 {to}, {from}");
        _funcs.AppendLine($"  {byteCount} = mul i64 {sliceLen}, 8");
        _funcs.AppendLine($"  {srcPtr}    = getelementptr i64, ptr {data}, i64 {from}");
        _funcs.AppendLine($"  {newData}   = call ptr @malloc(i64 {byteCount})");
        _externals.AddMemcpy();
        _funcs.AppendLine($"  call ptr @memcpy(ptr {newData}, ptr {srcPtr}, i64 {byteCount})");

        // Build new Seq header.
        var newSeq  = NextTmp();
        var lenGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {newSeq}  = call ptr @malloc(i64 16)");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Seq, ptr {newSeq}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {sliceLen}, ptr {lenGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Seq, ptr {newSeq}, i32 0, i32 1");
        _funcs.AppendLine($"  store ptr {newData}, ptr {dataGep}");

        _pendingArrayElemType = elemType;
        return (newSeq, SuruType.Array);
    }

    // ─── Element type conversion ─────────────────────────────────────────────

    // Convert a typed Suru value to i64 for storage in the i64[] data buffer.
    // Returns the SSA name of the i64 result (may be the same name if Int64 identity).
    //
    // Bool    → zext i1 to i64         (1 bit → 64 bits, zero-extended)
    // Int64   → identity               (already i64)
    // Float64 → bitcast double to i64  (reinterpret IEEE-754 bits, no rounding)
    // Ptr     → ptrtoint ptr to i64    (pointer address as integer; 64-bit platforms only)
    private string EmitToI64(string val, SuruType type)
    {
        if (type == SuruType.Int64) return val;   // identity — no instruction needed

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
        if (type == SuruType.Int64) return raw;   // identity

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
