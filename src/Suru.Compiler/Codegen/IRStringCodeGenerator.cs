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
// ── Runtime module ────────────────────────────────────────────────────────────
//
// All non-trivial string operations (append, at, equals, slice, ord, clone, drop,
// Int64.from, toString) are implemented in suru_string.ll and linked as a separate
// compilation unit. The user's .ll only emits `declare` stubs via _runtimeDecls.
//
// ── What stays inline ─────────────────────────────────────────────────────────
//
//   EmitExtractStringData / EmitExtractStringLen — simple 2-instruction GEP+load
//     patterns; used by printLn, writeFile, and EmitMatchTestChain (string patterns).
//
//   EmitStringLiteralValue — interns the raw bytes as a module-local [N x i8] global,
//     malloc+memcpy's them into a heap buffer, then calls suru_string_create.
//     The global is module-local so it cannot move to the runtime module.
//
//   EmitCreateStringSeq — now delegates to @suru_string_create in the runtime.
//     Kept as a thin helper for call sites that already have a data ptr and length.
//
// ── Layout diagram for `let s String: "hi"` ──────────────────────────────────
//
//   @.str_0 = [3 x i8] c"hi\00"         ← interned global, source bytes only
//
//   suru_main:
//     %buf  = malloc 3                   ← heap-owned copy of the 3 raw bytes
//     memcpy(%buf, @.str_0, 3)
//     %seq  = call @suru_string_create(%buf, 2)  ← runtime allocates the 16-byte Seq
//     store ptr %seq → %s.addr
partial class IRCodeGenerator
{
    // ─── String literal ──────────────────────────────────────────────────────

    // Intern the raw bytes as a global constant; malloc+memcpy into a fresh heap buffer;
    // then call suru_string_create to wrap data+len in a new Seq header.
    //
    // The global is module-local (cannot move to the runtime module), but the Seq
    // allocation itself is delegated to the runtime so the header layout stays
    // centralised in suru_string.ll.
    private (string val, SuruType type) EmitStringLiteralValue(string text)
    {
        if (!_stringLiterals.TryGetValue(text, out var entry))
        {
            var globalName = $"@.str_{_strCount++}";
            var byteLen    = CountStringBytes(text) + 1;  // +1 for null terminator
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
    //
    // These two helpers remain inline because they are used in contexts where a full
    // function call would be wasteful (printLn, writeFile, string match patterns).

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

    // Call @suru_string_create(data, len) to allocate a new %suru.Seq on the heap.
    // `dataPtr` — SSA name of the i8* data buffer (must be heap-owned).
    // `lenVal`  — SSA name or integer literal string for the i64 character count.
    private (string val, SuruType type) EmitCreateStringSeq(string dataPtr, string lenVal)
    {
        _runtimeDecls.AddStringCreate();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_create(ptr {dataPtr}, i64 {lenVal})");
        return (tmp, SuruType.String);
    }

    // ─── String instance methods ─────────────────────────────────────────────

    // .len() → Int64: load the len field inline (2 instructions; no runtime call needed).
    private (string val, SuruType type) EmitStringLen(string seqVal)
        => (EmitExtractStringLen(seqVal), SuruType.Int64);

    // .append(other) → String: delegate to @suru_string_append in the runtime.
    private (string val, SuruType type) EmitStringAppend(string lhsSeqVal, Expression rhsExpr)
    {
        var (rhsSeqVal, _) = EmitValue(rhsExpr);
        _runtimeDecls.AddStringAppend();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_append(ptr {lhsSeqVal}, ptr {rhsSeqVal})");
        return (tmp, SuruType.String);
    }

    // .at(i) → String: single-character String at byte index i.
    private (string val, SuruType type) EmitStringAt(string seqVal, Expression idxExpr)
    {
        var (idxVal, _) = EmitValue(idxExpr);
        _runtimeDecls.AddStringAt();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_at(ptr {seqVal}, i64 {idxVal})");
        return (tmp, SuruType.String);
    }

    // .equals(other) → Bool: byte-exact comparison via strcmp.
    private (string val, SuruType type) EmitStringEquals(string lhsSeqVal, Expression rhsExpr)
    {
        var (rhsSeqVal, _) = EmitValue(rhsExpr);
        _runtimeDecls.AddStringEquals();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i1 @suru_string_equals(ptr {lhsSeqVal}, ptr {rhsSeqVal})");
        return (tmp, SuruType.Bool);
    }

    // .slice(from, to) → String: substring covering bytes [from, to).
    private (string val, SuruType type) EmitStringSlice(
        string seqVal, Expression fromExpr, Expression toExpr)
    {
        var (fromVal, _) = EmitValue(fromExpr);
        var (toVal, _)   = EmitValue(toExpr);
        _runtimeDecls.AddStringSlice();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_slice(ptr {seqVal}, i64 {fromVal}, i64 {toVal})");
        return (tmp, SuruType.String);
    }

    // .ord() → Int64: ASCII code of the first byte.
    private (string val, SuruType type) EmitStringOrd(string seqVal)
    {
        _runtimeDecls.AddStringOrd();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i64 @suru_string_ord(ptr {seqVal})");
        return (tmp, SuruType.Int64);
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    // Dispatch helper for `clone(s)` in expression position.
    // Evaluates the argument, delegates to @suru_string_clone, returns String.
    internal (string val, SuruType type) EmitCloneStringDispatch(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        return (EmitCloneString(seqVal), SuruType.String);
    }

    // Emit a call to @suru_string_clone — produces an independent copy of a String.
    // Also called from IRArrayCodeGenerator when cloning an Array<String> element.
    internal string EmitCloneString(string seqPtr)
    {
        _runtimeDecls.AddStringClone();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_clone(ptr {seqPtr})");
        return tmp;
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    // Dispatch helper for `drop(s)` in expression position.
    // Returns ("0", Bool) so the call can appear in statement or expression position.
    internal (string val, SuruType type) EmitDropStringDispatch(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        EmitDropString(seqVal);
        return ("0", SuruType.Bool);
    }

    // Emit a call to @suru_string_drop — frees the char buffer then the Seq header.
    // Also called from IRArrayCodeGenerator when dropping an Array<String> element.
    internal void EmitDropString(string seqPtr)
    {
        _runtimeDecls.AddStringDrop();
        _funcs.AppendLine($"  call void @suru_string_drop(ptr {seqPtr})");
    }

    // ─── String ↔ Int64 conversions ──────────────────────────────────────────

    // Int64.from(str) → Int64: parse a decimal string via strtol.
    private (string val, SuruType type) EmitInt64FromString(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        _runtimeDecls.AddInt64FromString();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i64 @suru_int64_from_string(ptr {seqVal})");
        return (tmp, SuruType.Int64);
    }

    // n.toString() → String: format an Int64 as a decimal string via snprintf.
    private (string val, SuruType type) EmitInt64ToString(string int64Val)
    {
        _runtimeDecls.AddInt64ToString();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_int64_to_string(i64 {int64Val})");
        return (tmp, SuruType.String);
    }

    // Int64 static method dispatch — mirrors EmitInt32StaticMethod in the main file.
    internal (string val, SuruType type) EmitInt64StaticMethod(
        string methodName, IReadOnlyList<Expression> args) => methodName switch
    {
        "from" => EmitInt64FromString(args[0]),
        _ => throw new NotSupportedException($"IR codegen: unknown Int64 static method '{methodName}'"),
    };
}
