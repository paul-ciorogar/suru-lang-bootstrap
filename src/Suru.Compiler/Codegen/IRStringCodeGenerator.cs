// Covered fixtures: print, exit_test, print-error, negative-literals, arithmetic, comparisons,
//                   control-flow, fibonacci, while-loop, strings, include-test, file_io, file_io_write, arrays
using System.Globalization;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// String IR emission for IRCodeGenerator.
//
// Every Suru String value is a `ptr` to a heap-allocated
//   %suru.String = { i64 type_tag=6, i64 len, ptr data }  (24 bytes)
// type_tag=6 at field 0: any heap ptr can be identified as String at runtime.
// `data` points to a null-terminated i8 buffer; `len` holds the character count.
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
//
// ── Layout diagram for `let s String: "hi"` ──────────────────────────────────
//
//   @.str_0 = [3 x i8] c"hi\00"         ← interned global, source bytes only
//
//   suru_main:
//     %buf  = malloc 3                   ← heap-owned copy of the 3 raw bytes
//     memcpy(%buf, @.str_0, 3)
//     %seq  = call @suru_string_create(%buf, 2)  ← runtime allocates the 24-byte Seq
//     store ptr %seq → %s.addr
partial class IRCodeGenerator
{
    // ─── String literal ──────────────────────────────────────────────────────

    private (string val, SuruType type) EmitStringLiteralValue(string text)
    {
        if (!_stringLiterals.TryGetValue(text, out var entry))
        {
            var globalName = $"@.str_{_strCount++}";
            var byteLen    = CountStringBytes(text) + 1;
            entry = (globalName, byteLen);
            _stringLiterals[text] = entry;
        }
        var charLen   = (entry.byteLen - 1).ToString(CultureInfo.InvariantCulture);
        var byteCount = entry.byteLen.ToString(CultureInfo.InvariantCulture);

        _externals.AddMemcpy();
        var buf = NextTmp();
        _funcs.AppendLine($"  {buf} = call ptr @malloc(i64 {byteCount})");
        _funcs.AppendLine($"  call ptr @memcpy(ptr {buf}, ptr {entry.name}, i64 {byteCount})");

        return EmitCreateStringSeq(buf, charLen);
    }

    // ─── Low-level Seq helpers ───────────────────────────────────────────────
    //
    // %suru.String layout: { type_tag@0, len@1, data@2 }
    // These helpers load raw i64/ptr values; callers that need a boxed Suru value
    // should use EmitStringLen (which boxes the result).

    private string EmitExtractStringData(string seqVal)
    {
        var gep  = NextTmp();
        var data = NextTmp();
        _funcs.AppendLine($"  {gep}  = getelementptr %suru.String, ptr {seqVal}, i32 0, i32 2");
        _funcs.AppendLine($"  {data} = load ptr, ptr {gep}");
        return data;
    }

    private string EmitExtractStringLen(string seqVal)
    {
        var gep = NextTmp();
        var len = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.String, ptr {seqVal}, i32 0, i32 1");
        _funcs.AppendLine($"  {len} = load i64, ptr {gep}");
        return len;
    }

    private (string val, SuruType type) EmitCreateStringSeq(string dataPtr, string lenVal)
    {
        _runtimeDecls.AddStringCreate();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_create(ptr {dataPtr}, i64 {lenVal})");
        return (tmp, SuruType.String);
    }

    // ─── String instance methods ─────────────────────────────────────────────

    // .len() → Box(Int64): GEP+load the raw len, then box it.
    private (string val, SuruType type) EmitStringLen(string seqVal)
    {
        var raw = EmitExtractStringLen(seqVal);
        return (BoxInt64(raw), SuruType.Int64);
    }

    // .append(other) → String
    private (string val, SuruType type) EmitStringAppend(string lhsSeqVal, Expression rhsExpr)
    {
        var (rhsSeqVal, _) = EmitValue(rhsExpr);
        _runtimeDecls.AddStringAppend();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_append(ptr {lhsSeqVal}, ptr {rhsSeqVal})");
        return (tmp, SuruType.String);
    }

    // .at(i) → String: unbox idx Box(Int64), then call @suru_string_at.
    private (string val, SuruType type) EmitStringAt(string seqVal, Expression idxExpr)
    {
        var (idxBox, _) = EmitValue(idxExpr);
        var idx = UnboxInt64(idxBox);
        _runtimeDecls.AddStringAt();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_at(ptr {seqVal}, i64 {idx})");
        return (tmp, SuruType.String);
    }

    // .equals(other) → Box(Bool)
    private (string val, SuruType type) EmitStringEquals(string lhsSeqVal, Expression rhsExpr)
    {
        var (rhsSeqVal, _) = EmitValue(rhsExpr);
        _runtimeDecls.AddStringEquals();
        var rawBool = NextTmp();
        _funcs.AppendLine($"  {rawBool} = call i1 @suru_string_equals(ptr {lhsSeqVal}, ptr {rhsSeqVal})");
        return (BoxBool(rawBool), SuruType.Bool);
    }

    // .slice(from, to) → String: unbox from/to Box(Int64).
    private (string val, SuruType type) EmitStringSlice(
        string seqVal, Expression fromExpr, Expression toExpr)
    {
        var (fromBox, _) = EmitValue(fromExpr);
        var (toBox, _)   = EmitValue(toExpr);
        var from = UnboxInt64(fromBox);
        var to   = UnboxInt64(toBox);
        _runtimeDecls.AddStringSlice();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_slice(ptr {seqVal}, i64 {from}, i64 {to})");
        return (tmp, SuruType.String);
    }

    // .ord() → Box(Int64): ASCII code of the first byte.
    private (string val, SuruType type) EmitStringOrd(string seqVal)
    {
        _runtimeDecls.AddStringOrd();
        var rawOrd = NextTmp();
        _funcs.AppendLine($"  {rawOrd} = call i64 @suru_string_ord(ptr {seqVal})");
        return (BoxInt64(rawOrd), SuruType.Int64);
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    internal (string val, SuruType type) EmitCloneStringDispatch(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        return (EmitCloneString(seqVal), SuruType.String);
    }

    internal string EmitCloneString(string seqPtr)
    {
        _runtimeDecls.AddStringClone();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_string_clone(ptr {seqPtr})");
        return tmp;
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    internal (string val, SuruType type) EmitDropStringDispatch(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        EmitDropString(seqVal);
        return ("null", SuruType.Bool);
    }

    internal void EmitDropString(string seqPtr)
    {
        _runtimeDecls.AddStringDrop();
        _funcs.AppendLine($"  call void @suru_string_drop(ptr {seqPtr})");
    }

    // ─── String ↔ Int64 conversions ──────────────────────────────────────────

    // Int64.from(str) → Box(Int64): parse a decimal string via strtol, then box.
    private (string val, SuruType type) EmitInt64FromString(Expression arg)
    {
        var (seqVal, _) = EmitValue(arg);
        _runtimeDecls.AddInt64FromString();
        var rawI64 = NextTmp();
        _funcs.AppendLine($"  {rawI64} = call i64 @suru_int64_from_string(ptr {seqVal})");
        return (BoxInt64(rawI64), SuruType.Int64);
    }

    // n.toString() → String: format a raw i64 via snprintf.
    // Caller is responsible for unboxing before passing int64Val.
    private (string val, SuruType type) EmitInt64ToString(string int64Val)
    {
        _runtimeDecls.AddInt64ToString();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_int64_to_string(i64 {int64Val})");
        return (tmp, SuruType.String);
    }

    // Int64 static method dispatch.
    internal (string val, SuruType type) EmitInt64StaticMethod(
        string methodName, IReadOnlyList<Expression> args) => methodName switch
    {
        "from" => EmitInt64FromString(args[0]),
        _ => throw new NotSupportedException($"IR codegen: unknown Int64 static method '{methodName}'"),
    };
}
