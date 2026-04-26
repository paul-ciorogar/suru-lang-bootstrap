// Covered fixtures: print, exit_test, print-error, negative-literals, arithmetic, comparisons,
//                   control-flow, fibonacci, while-loop, strings, include-test, file_io, file_io_write, arrays
using System.Globalization;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// String IR emission for IRCodeGenerator.
//
// Every Suru String value is a `ptr` to a heap-allocated %suru.Seq = { i64 len, ptr data }.
// `data` points to a null-terminated i8 buffer; `len` holds the character count excluding
// the null terminator.
//
// String literals are interned as [N x i8] private constant globals (the raw bytes).  At
// runtime EmitStringLiteralValue mallocs a fresh char buffer, memcpy's the bytes from the
// global, and wraps both in a new 16-byte Seq header.  The heap-owned data buffer means every
// Seq can be safely freed by drop() — no special case for literal vs computed strings.
//
// Layout diagram for `let s String: "hi"`:
//
//   @.str_0 = [3 x i8] c"hi\00"        ← interned global, source bytes only
//
//   suru_main:
//     %buf  = malloc 3                  ← heap-owned copy of the 3 raw bytes
//     memcpy(%buf, @.str_0, 3)
//     %seq  = malloc 16                 ← Seq header
//     store i64 2 → %seq[0]            ← len field (excludes null terminator)
//     store ptr %buf → %seq[1]         ← data field (heap-owned, always free-able)
//     %s.addr = alloca ptr
//     store ptr %seq → %s.addr
//
// String mutations (append, slice, at) produce a NEW Seq; the old one is currently leaked.
// Every Seq's data pointer is heap-owned, so drop(arr) for Array<String> can free it safely.
partial class IRCodeGenerator
{
    // ─── String literal ──────────────────────────────────────────────────────

    // Intern the raw bytes as a global constant, then malloc-copy them into a fresh heap buffer
    // and wrap in a new 16-byte Seq header.  The global is deduped (identical strings share one
    // [N x i8] constant), but each call site gets its own heap buffer and Seq header.
    //
    // Heap-owning the data buffer ensures every Seq can be uniformly freed by drop() — there
    // is no need to distinguish "literal" strings from "computed" strings at the call site.
    private (string val, SuruType type) EmitStringLiteralValue(string text)
    {
        if (!_stringLiterals.TryGetValue(text, out var entry))
        {
            var globalName = $"@.str_{_strCount++}";
            var byteLen    = CountStringBytes(text) + 1; // +1 for null terminator
            entry = (globalName, byteLen);
            _stringLiterals[text] = entry;
        }
        // byteLen includes the null terminator; len stored in the Seq excludes it.
        var charLen   = (entry.byteLen - 1).ToString(CultureInfo.InvariantCulture);
        var byteCount = entry.byteLen.ToString(CultureInfo.InvariantCulture);

        // Heap-allocate a copy of the global bytes so the data ptr is always free-able.
        _externals.AddMemcpy();
        var buf = NextTmp();
        _funcs.AppendLine($"  {buf} = call ptr @malloc(i64 {byteCount})");
        _funcs.AppendLine($"  call ptr @memcpy(ptr {buf}, ptr {entry.name}, i64 {byteCount})");

        return EmitCreateStringSeq(buf, charLen);
    }

    // ─── Low-level Seq helpers ───────────────────────────────────────────────

    // Extract the `data` pointer from a %suru.Seq (field index 1).
    // Returns the SSA name of the loaded ptr value.
    private string EmitExtractStringData(string seqVal)
    {
        var gep  = NextTmp();
        var data = NextTmp();
        _funcs.AppendLine($"  {gep}  = getelementptr %suru.Seq, ptr {seqVal}, i32 0, i32 1");
        _funcs.AppendLine($"  {data} = load ptr, ptr {gep}");
        return data;
    }

    // Extract the `len` field from a %suru.Seq (field index 0).
    // Returns the SSA name of the loaded i64 value.
    private string EmitExtractStringLen(string seqVal)
    {
        var gep = NextTmp();
        var len = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Seq, ptr {seqVal}, i32 0, i32 0");
        _funcs.AppendLine($"  {len} = load i64, ptr {gep}");
        return len;
    }

    // Allocate a new %suru.Seq on the heap, populate its fields, and return a ptr to it.
    // `dataPtr`  — SSA name (or global name) of the i8* data buffer.
    // `lenVal`   — SSA name or integer literal string for the i64 character count.
    private (string val, SuruType type) EmitCreateStringSeq(string dataPtr, string lenVal)
    {
        _externals.AddMalloc();
        var seqPtr  = NextTmp();
        var lenGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {seqPtr}  = call ptr @malloc(i64 16)");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Seq, ptr {seqPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {lenVal}, ptr {lenGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Seq, ptr {seqPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  store ptr {dataPtr}, ptr {dataGep}");
        return (seqPtr, SuruType.String);
    }

    // ─── String instance methods ─────────────────────────────────────────────

    // .len() → Int64: load the len field directly from the Seq header.
    // No C runtime calls needed — the length was stored at construction time.
    private (string val, SuruType type) EmitStringLen(string seqVal)
        => (EmitExtractStringLen(seqVal), SuruType.Int64);

    // .append(other) → String: concatenate two strings into a new heap-allocated Seq.
    //
    // Strategy:
    //   1. Extract len and data from both Seqs.
    //   2. totalLen = len1 + len2; malloc totalLen+1 bytes for the new data buffer.
    //   3. memcpy the first string's bytes into the buffer.
    //   4. GEP to the midpoint (offset len1); memcpy the second string's bytes.
    //   5. Store a null terminator at offset totalLen.
    //   6. Wrap in a new Seq via EmitCreateStringSeq.
    //
    // The old Seqs are not freed — memory management is the caller's responsibility.
    private (string val, SuruType type) EmitStringAppend(string lhsSeqVal, Expression rhsExpr)
    {
        var (rhsSeqVal, _) = EmitValue(rhsExpr);

        var lhsLen  = EmitExtractStringLen(lhsSeqVal);
        var lhsData = EmitExtractStringData(lhsSeqVal);
        var rhsLen  = EmitExtractStringLen(rhsSeqVal);
        var rhsData = EmitExtractStringData(rhsSeqVal);

        // totalLen = len1 + len2; bufSize = totalLen + 1 (null terminator)
        var totalLen = NextTmp();
        var bufSize  = NextTmp();
        _funcs.AppendLine($"  {totalLen} = add i64 {lhsLen}, {rhsLen}");
        _funcs.AppendLine($"  {bufSize}  = add i64 {totalLen}, 1");

        var newData = NextTmp();
        _funcs.AppendLine($"  {newData} = call ptr @malloc(i64 {bufSize})");

        // Copy first half: memcpy(newData, lhsData, lhsLen)
        _externals.AddMemcpy();
        _funcs.AppendLine($"  call ptr @memcpy(ptr {newData}, ptr {lhsData}, i64 {lhsLen})");

        // Copy second half: GEP to newData + lhsLen, then memcpy(mid, rhsData, rhsLen)
        var midPtr = NextTmp();
        _funcs.AppendLine($"  {midPtr} = getelementptr i8, ptr {newData}, i64 {lhsLen}");
        _funcs.AppendLine($"  call ptr @memcpy(ptr {midPtr}, ptr {rhsData}, i64 {rhsLen})");

        // Null-terminate: newData[totalLen] = '\0'
        var nullPtr = NextTmp();
        _funcs.AppendLine($"  {nullPtr} = getelementptr i8, ptr {newData}, i64 {totalLen}");
        _funcs.AppendLine($"  store i8 0, ptr {nullPtr}");

        return EmitCreateStringSeq(newData, totalLen);
    }

    // .at(idx) → String: return a single-character string at the given index.
    //
    // Allocates a 2-byte buffer ([char, '\0']), copies the byte at `idx` from the
    // source data, null-terminates it, and wraps in a new Seq with len=1.
    private (string val, SuruType type) EmitStringAt(string seqVal, Expression idxExpr)
    {
        var (idxVal, _) = EmitValue(idxExpr);
        var dataPtr     = EmitExtractStringData(seqVal);

        // Point to the character at position idx
        var charPtr = NextTmp();
        _funcs.AppendLine($"  {charPtr} = getelementptr i8, ptr {dataPtr}, i64 {idxVal}");

        // Allocate [char, '\0'] and copy
        var buf     = NextTmp();
        var charVal = NextTmp();
        var nullPtr = NextTmp();
        _funcs.AppendLine($"  {buf}     = call ptr @malloc(i64 2)");
        _funcs.AppendLine($"  {charVal} = load i8, ptr {charPtr}");
        _funcs.AppendLine($"  store i8 {charVal}, ptr {buf}");
        _funcs.AppendLine($"  {nullPtr} = getelementptr i8, ptr {buf}, i64 1");
        _funcs.AppendLine($"  store i8 0, ptr {nullPtr}");

        return EmitCreateStringSeq(buf, "1");
    }

    // .equals(other) → Bool: byte-exact comparison via strcmp.
    // strcmp returns 0 when the strings are identical; icmp eq converts that to i1 true.
    private (string val, SuruType type) EmitStringEquals(string lhsSeqVal, Expression rhsExpr)
    {
        var (rhsSeqVal, _) = EmitValue(rhsExpr);
        var lhsData        = EmitExtractStringData(lhsSeqVal);
        var rhsData        = EmitExtractStringData(rhsSeqVal);

        _externals.AddStrcmp();
        var cmpResult = NextTmp();
        var eqResult  = NextTmp();
        _funcs.AppendLine($"  {cmpResult} = call i32 @strcmp(ptr {lhsData}, ptr {rhsData})");
        _funcs.AppendLine($"  {eqResult}  = icmp eq i32 {cmpResult}, 0");
        return (eqResult, SuruType.Bool);
    }

    // .slice(from, to) → String: return the substring covering bytes [from, to).
    //
    // Strategy: GEP to `from` in the source data; memcpy `(to - from)` bytes into a new
    // malloc'd buffer; null-terminate; wrap in a new Seq. The slice length is computed
    // as an SSA value so it serves both as the memcpy count and the Seq len field.
    private (string val, SuruType type) EmitStringSlice(
        string seqVal, Expression fromExpr, Expression toExpr)
    {
        var (fromVal, _) = EmitValue(fromExpr);
        var (toVal, _)   = EmitValue(toExpr);
        var dataPtr      = EmitExtractStringData(seqVal);

        // sliceLen = to - from
        var sliceLen = NextTmp();
        _funcs.AppendLine($"  {sliceLen} = sub i64 {toVal}, {fromVal}");

        // srcPtr = dataPtr + from
        var srcPtr = NextTmp();
        _funcs.AppendLine($"  {srcPtr} = getelementptr i8, ptr {dataPtr}, i64 {fromVal}");

        // Allocate sliceLen+1 bytes (room for null terminator)
        var bufSize = NextTmp();
        var buf     = NextTmp();
        _funcs.AppendLine($"  {bufSize} = add i64 {sliceLen}, 1");
        _funcs.AppendLine($"  {buf}     = call ptr @malloc(i64 {bufSize})");

        // Copy the slice
        _externals.AddMemcpy();
        _funcs.AppendLine($"  call ptr @memcpy(ptr {buf}, ptr {srcPtr}, i64 {sliceLen})");

        // Null-terminate at offset sliceLen
        var nullPtr = NextTmp();
        _funcs.AppendLine($"  {nullPtr} = getelementptr i8, ptr {buf}, i64 {sliceLen}");
        _funcs.AppendLine($"  store i8 0, ptr {nullPtr}");

        return EmitCreateStringSeq(buf, sliceLen);
    }

    // .ord() → Int64: return the ASCII value of the first character.
    // Extracts the data pointer from the Seq, loads the first i8, and zero-extends to i64.
    private (string val, SuruType type) EmitStringOrd(string seqVal)
    {
        var dataPtr = EmitExtractStringData(seqVal);
        var byteTmp = NextTmp();
        var extTmp  = NextTmp();
        _funcs.AppendLine($"  {byteTmp} = load i8, ptr {dataPtr}");
        _funcs.AppendLine($"  {extTmp}  = zext i8 {byteTmp} to i64");
        return (extTmp, SuruType.Int64);
    }

    // ─── String ↔ Int64 conversions ──────────────────────────────────────────

    // Int64.from(str) → Int64: parse a decimal string to a 64-bit integer via strtol.
    // Extracts the data pointer from the argument Seq and passes it to strtol with base 10.
    // The second argument (endptr) is null — we don't need to inspect the stop position.
    private (string val, SuruType type) EmitInt64FromString(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        var dataPtr     = EmitExtractStringData(seqVal);

        _externals.AddStrtol();
        var result = NextTmp();
        _funcs.AppendLine($"  {result} = call i64 @strtol(ptr {dataPtr}, ptr null, i32 10)");
        return (result, SuruType.Int64);
    }

    // n.toString() → String: format an Int64 as a decimal string via snprintf.
    //
    // Strategy (two-call snprintf pattern):
    //   1. snprintf(null, 0, "%lld", val) — C99-defined; returns the number of characters
    //      that would have been written (excluding the null terminator). No buffer needed.
    //   2. malloc(count + 1) — exact-size allocation.
    //   3. snprintf(buf, count+1, "%lld", val) — write the formatted digits into the buffer.
    //   4. Wrap the buffer in a new Seq via EmitCreateStringSeq.
    //
    // The count returned by snprintf is an i32; it is sign-extended to i64 for use as the
    // Seq len field and as the malloc size operand (i64).
    private (string val, SuruType type) EmitInt64ToString(string int64Val)
    {
        _boolStringGlobals.AddFmtIntRaw();
        _externals.AddSnprintf();

        // Measure: snprintf(null, 0, "%lld", val) → i32 char count
        var countRaw = NextTmp();
        _funcs.AppendLine(
            $"  {countRaw} = call i32 (ptr, i64, ptr, ...) @snprintf(" +
            $"ptr null, i64 0, ptr @.fmt_int_raw, i64 {int64Val})");

        var count64 = NextTmp();
        _funcs.AppendLine($"  {count64} = sext i32 {countRaw} to i64");

        // Allocate: count + 1 bytes (includes null terminator)
        var bufSize = NextTmp();
        var buf     = NextTmp();
        _funcs.AppendLine($"  {bufSize} = add i64 {count64}, 1");
        _funcs.AppendLine($"  {buf}     = call ptr @malloc(i64 {bufSize})");

        // Write: snprintf(buf, bufSize, "%lld", val)
        _funcs.AppendLine(
            $"  call i32 (ptr, i64, ptr, ...) @snprintf(" +
            $"ptr {buf}, i64 {bufSize}, ptr @.fmt_int_raw, i64 {int64Val})");

        return EmitCreateStringSeq(buf, count64);
    }

    // Int64 static method dispatch — mirrors EmitInt32StaticMethod in the main file.
    internal (string val, SuruType type) EmitInt64StaticMethod(
        string methodName, IReadOnlyList<Expression> args) => methodName switch
    {
        "from" => EmitInt64FromString(args[0]),
        _ => throw new NotSupportedException($"IR codegen: unknown Int64 static method '{methodName}'"),
    };
}
