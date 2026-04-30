namespace Suru.Compiler.Codegen;

// Generates the four Suru runtime LLVM IR modules that are compiled and linked
// with every Suru program.
//
// ── Type tag enum (unified across all modules) ───────────────────────────────
//
//   0 = Bool    1 = Int32    2 = Int64    3 = Float64
//   4 = Struct  5 = Array    6 = String
//
//   Every heap-allocated Suru value stores its type_tag as the FIRST i64 field.
//   This means `load i64, ptr %anyVal` gives the type_tag regardless of kind.
//
// ── Modules ──────────────────────────────────────────────────────────────────
//
//   suru_box.ll    — Scalar heap wrapper: %suru.Box = { i64 type_tag, i64 payload }.
//                    box/unbox for Bool/Int32/Int64/Float64, suru_box_clone,
//                    suru_println (stdout), suru_printerror (stderr).
//
//   suru_string.ll — String heap operations: create, clone, drop, append, at,
//                    equals, slice, ord, suru_int64_from_string, suru_int64_to_string.
//                    %suru.String = { i64 type_tag=6, i64 len, ptr data }
//
//   suru_array.ll  — Array heap operations: at (returns ptr), set/add (take ptr),
//                    slice, clone_dyn (dispatch on element type_tag), drop_dyn.
//                    %suru.Array = { i64 type_tag=5, i64 elem_tag, i64 len, i64 cap, ptr data }
//
//   suru_struct.ll — Struct linked-list operations: suru_find_field (phi-loop via strcmp),
//                    suru_struct_clone (node-by-node copy), suru_struct_drop (linked-list free).
//                    %suru.Field = { i64 type_tag=4, ptr name, i32 field_tag, i64 val, ptr next }
//
// ── Cross-module dependencies ─────────────────────────────────────────────────
//
//   suru_box.ll:    depends on libc only (malloc, free, printf, puts, fprintf, fputs).
//   suru_string.ll: depends on libc only (malloc, memcpy, free, strcmp, strtol, snprintf).
//   suru_struct.ll: depends on libc only (malloc, free, strcmp).
//   suru_array.ll:  depends on libc + suru_box_clone + suru_string_clone/drop + suru_struct_clone/drop.
//
// All four .o files are linked into every Suru binary so cross-module calls resolve.
public static class SuruRuntime
{
    // ─── Box runtime ──────────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_box.ll.
    //
    // %suru.Box wraps scalar Suru values (Bool/Int32/Int64/Float64) on the heap
    // so every runtime value is a ptr carrying its type_tag at offset 0.
    // suru_println / suru_printerror dispatch on the type_tag of any heap ptr.
    public static string GenerateBoxRuntime() => """
; Suru box runtime — compiled to suru_box.o and linked with every Suru program.
;
; %suru.Box wraps scalar values so every Suru value is a `ptr` with type_tag at offset 0.
; type_tag: 0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String

; ModuleID = 'suru_box.ll'
source_filename = "suru_box.ll"

%suru.Box    = type { i64, i64 }
%suru.String = type { i64, i64, ptr }

declare ptr  @malloc(i64)
declare void @free(ptr)
declare i32  @puts(ptr)
declare i32  @printf(ptr, ...)
declare i32  @fputs(ptr, ptr)
declare i32  @fprintf(ptr, ptr, ...)

@stderr = external global ptr

@.box_fmt_lld  = private unnamed_addr constant [6 x i8]  c"%lld\0a\00"
@.box_fmt_d    = private unnamed_addr constant [4 x i8]  c"%d\0a\00"
@.box_fmt_g    = private unnamed_addr constant [4 x i8]  c"%g\0a\00"
@.box_efmt_lld = private unnamed_addr constant [6 x i8]  c"%lld\0a\00"
@.box_efmt_d   = private unnamed_addr constant [4 x i8]  c"%d\0a\00"
@.box_efmt_g   = private unnamed_addr constant [4 x i8]  c"%g\0a\00"
@.box_true     = private unnamed_addr constant [5 x i8]  c"true\00"
@.box_false    = private unnamed_addr constant [6 x i8]  c"false\00"
@.box_struct   = private unnamed_addr constant [9 x i8]  c"<struct>\00"
@.box_array    = private unnamed_addr constant [8 x i8]  c"<array>\00"
@.box_newline  = private unnamed_addr constant [2 x i8]  c"\0a\00"

; ─── suru_box_bool ─────────────────────────────────────────────────────────────
define ptr @suru_box_bool(i1 %v) {
entry:
  %b   = call ptr @malloc(i64 16)
  %tg  = getelementptr %suru.Box, ptr %b, i32 0, i32 0
  store i64 0, ptr %tg
  %pg  = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %ext = zext i1 %v to i64
  store i64 %ext, ptr %pg
  ret ptr %b
}

; ─── suru_box_int32 ────────────────────────────────────────────────────────────
define ptr @suru_box_int32(i32 %v) {
entry:
  %b   = call ptr @malloc(i64 16)
  %tg  = getelementptr %suru.Box, ptr %b, i32 0, i32 0
  store i64 1, ptr %tg
  %pg  = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %ext = sext i32 %v to i64
  store i64 %ext, ptr %pg
  ret ptr %b
}

; ─── suru_box_int64 ────────────────────────────────────────────────────────────
define ptr @suru_box_int64(i64 %v) {
entry:
  %b  = call ptr @malloc(i64 16)
  %tg = getelementptr %suru.Box, ptr %b, i32 0, i32 0
  store i64 2, ptr %tg
  %pg = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  store i64 %v, ptr %pg
  ret ptr %b
}

; ─── suru_box_float64 ──────────────────────────────────────────────────────────
define ptr @suru_box_float64(double %v) {
entry:
  %b   = call ptr @malloc(i64 16)
  %tg  = getelementptr %suru.Box, ptr %b, i32 0, i32 0
  store i64 3, ptr %tg
  %pg  = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %raw = bitcast double %v to i64
  store i64 %raw, ptr %pg
  ret ptr %b
}

; ─── suru_unbox_bool ───────────────────────────────────────────────────────────
define i1 @suru_unbox_bool(ptr %b) {
entry:
  %pg = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %v  = load i64, ptr %pg
  %r  = trunc i64 %v to i1
  ret i1 %r
}

; ─── suru_unbox_int32 ──────────────────────────────────────────────────────────
define i32 @suru_unbox_int32(ptr %b) {
entry:
  %pg = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %v  = load i64, ptr %pg
  %r  = trunc i64 %v to i32
  ret i32 %r
}

; ─── suru_unbox_int64 ──────────────────────────────────────────────────────────
define i64 @suru_unbox_int64(ptr %b) {
entry:
  %pg = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %v  = load i64, ptr %pg
  ret i64 %v
}

; ─── suru_unbox_float64 ────────────────────────────────────────────────────────
define double @suru_unbox_float64(ptr %b) {
entry:
  %pg = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  %v  = load i64, ptr %pg
  %r  = bitcast i64 %v to double
  ret double %r
}

; ─── suru_box_clone ────────────────────────────────────────────────────────────
define ptr @suru_box_clone(ptr %src) {
entry:
  %b   = call ptr @malloc(i64 16)
  %st  = getelementptr %suru.Box, ptr %src, i32 0, i32 0
  %tv  = load i64, ptr %st
  %dt  = getelementptr %suru.Box, ptr %b, i32 0, i32 0
  store i64 %tv, ptr %dt
  %sp  = getelementptr %suru.Box, ptr %src, i32 0, i32 1
  %pv  = load i64, ptr %sp
  %dp  = getelementptr %suru.Box, ptr %b, i32 0, i32 1
  store i64 %pv, ptr %dp
  ret ptr %b
}

; ─── suru_println ──────────────────────────────────────────────────────────────
; Print any Suru value to stdout followed by a newline.
; Dispatches on type_tag at offset 0 — valid for Box, String, Array, Struct.
define void @suru_println(ptr %v) {
entry:
  %tag = load i64, ptr %v
  switch i64 %tag, label %unknown [
    i64 0, label %is_bool
    i64 1, label %is_int32
    i64 2, label %is_int64
    i64 3, label %is_float64
    i64 4, label %is_struct
    i64 5, label %is_array
    i64 6, label %is_string
  ]
is_bool:
  %bp  = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %bv  = load i64, ptr %bp
  %bi1 = trunc i64 %bv to i1
  br i1 %bi1, label %bool_true, label %bool_false
bool_true:
  call i32 @puts(ptr @.box_true)
  br label %done
bool_false:
  call i32 @puts(ptr @.box_false)
  br label %done
is_int32:
  %i32p = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %i32v = load i64, ptr %i32p
  %i32t = trunc i64 %i32v to i32
  call i32 (ptr, ...) @printf(ptr @.box_fmt_d, i32 %i32t)
  br label %done
is_int64:
  %i64p = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %i64v = load i64, ptr %i64p
  call i32 (ptr, ...) @printf(ptr @.box_fmt_lld, i64 %i64v)
  br label %done
is_float64:
  %f64p = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %f64r = load i64, ptr %f64p
  %f64v = bitcast i64 %f64r to double
  call i32 (ptr, ...) @printf(ptr @.box_fmt_g, double %f64v)
  br label %done
is_struct:
  call i32 @puts(ptr @.box_struct)
  br label %done
is_array:
  call i32 @puts(ptr @.box_array)
  br label %done
is_string:
  %sdp  = getelementptr %suru.String, ptr %v, i32 0, i32 2
  %sdat = load ptr, ptr %sdp
  call i32 @puts(ptr %sdat)
  br label %done
unknown:
  br label %done
done:
  ret void
}

; ─── suru_printerror ───────────────────────────────────────────────────────────
; Print any Suru value to stderr followed by a newline.
define void @suru_printerror(ptr %v) {
entry:
  %fp  = load ptr, ptr @stderr
  %tag = load i64, ptr %v
  switch i64 %tag, label %unknown [
    i64 0, label %is_bool
    i64 1, label %is_int32
    i64 2, label %is_int64
    i64 3, label %is_float64
    i64 4, label %is_struct
    i64 5, label %is_array
    i64 6, label %is_string
  ]
is_bool:
  %bp  = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %bv  = load i64, ptr %bp
  %bi1 = trunc i64 %bv to i1
  br i1 %bi1, label %bool_true, label %bool_false
bool_true:
  call i32 @fputs(ptr @.box_true, ptr %fp)
  call i32 @fputs(ptr @.box_newline, ptr %fp)
  br label %done
bool_false:
  call i32 @fputs(ptr @.box_false, ptr %fp)
  call i32 @fputs(ptr @.box_newline, ptr %fp)
  br label %done
is_int32:
  %i32p = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %i32v = load i64, ptr %i32p
  %i32t = trunc i64 %i32v to i32
  call i32 (ptr, ptr, ...) @fprintf(ptr %fp, ptr @.box_efmt_d, i32 %i32t)
  br label %done
is_int64:
  %i64p = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %i64v = load i64, ptr %i64p
  call i32 (ptr, ptr, ...) @fprintf(ptr %fp, ptr @.box_efmt_lld, i64 %i64v)
  br label %done
is_float64:
  %f64p = getelementptr %suru.Box, ptr %v, i32 0, i32 1
  %f64r = load i64, ptr %f64p
  %f64v = bitcast i64 %f64r to double
  call i32 (ptr, ptr, ...) @fprintf(ptr %fp, ptr @.box_efmt_g, double %f64v)
  br label %done
is_struct:
  call i32 @fputs(ptr @.box_struct, ptr %fp)
  call i32 @fputs(ptr @.box_newline, ptr %fp)
  br label %done
is_array:
  call i32 @fputs(ptr @.box_array, ptr %fp)
  call i32 @fputs(ptr @.box_newline, ptr %fp)
  br label %done
is_string:
  %sdp  = getelementptr %suru.String, ptr %v, i32 0, i32 2
  %sdat = load ptr, ptr %sdp
  call i32 @fputs(ptr %sdat, ptr %fp)
  call i32 @fputs(ptr @.box_newline, ptr %fp)
  br label %done
unknown:
  br label %done
done:
  ret void
}

; ─── suru_dyn_len ──────────────────────────────────────────────────────────────
; Return the length of any String or Array value. Dispatches on type_tag at offset 0.
;   tag=5 (Array):  loads %suru.Array field index 2 (len, i64, offset 16)
;   tag=6 (String): loads %suru.String field index 1 (len, i64, offset 8)
;   other:          returns 0
%suru.String.dyn = type { i64, i64, ptr }
%suru.Array.dyn  = type { i64, i64, i64, i64, ptr }
define i64 @suru_dyn_len(ptr %v) {
entry:
  %tag = load i64, ptr %v
  switch i64 %tag, label %unknown [
    i64 5, label %is_array
    i64 6, label %is_string
  ]
is_array:
  %agep = getelementptr %suru.Array.dyn, ptr %v, i32 0, i32 2
  %alen = load i64, ptr %agep
  ret i64 %alen
is_string:
  %sgep = getelementptr %suru.String.dyn, ptr %v, i32 0, i32 1
  %slen = load i64, ptr %sgep
  ret i64 %slen
unknown:
  ret i64 0
}

""";

    // ─── String runtime ───────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_string.ll.
    // type_tag is 6 (TYPE_STRING), stored at field 0 of every %suru.String header.
    public static string GenerateStringRuntime() => """
; Suru string runtime — compiled to suru_string.o and linked with every Suru program.
;
; Every Suru String is a ptr to a heap-allocated
;   %suru.String = { i64 type_tag, i64 len, ptr data }  (24 bytes)
; type_tag is always 6 (TYPE_STRING). Placing it first means any heap ptr can be
; inspected at offset 0 to determine its Suru kind at runtime.

; ModuleID = 'suru_string.ll'
source_filename = "suru_string.ll"

%suru.String = type { i64, i64, ptr }

declare ptr  @malloc(i64)
declare ptr  @memcpy(ptr, ptr, i64)
declare void @free(ptr)
declare i32  @strcmp(ptr, ptr)
declare i64  @strtol(ptr, ptr, i32)
declare i32  @snprintf(ptr, i64, ptr, ...)

; Private format string used only by suru_int64_to_string.
@.srt_fmt_lld = private unnamed_addr constant [5 x i8] c"%lld\00"

; ─── suru_string_create ────────────────────────────────────────────────────────
;
; Allocate a new 24-byte %suru.String header: type_tag=6, len, data.
define ptr @suru_string_create(ptr %data, i64 %len) {
entry:
  %seq  = call ptr @malloc(i64 24)
  %tgep = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tgep
  %lgep = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 %len, ptr %lgep
  %dgep = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %data, ptr %dgep
  ret ptr %seq
}

; ─── suru_string_clone ─────────────────────────────────────────────────────────
define ptr @suru_string_clone(ptr %s) {
entry:
  %lgep = getelementptr %suru.String, ptr %s, i32 0, i32 1
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %bufb = add i64 %len, 1
  %buf  = call ptr @malloc(i64 %bufb)
  call ptr @memcpy(ptr %buf, ptr %data, i64 %bufb)
  %seq  = call ptr @malloc(i64 24)
  %tg   = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tg
  %sl   = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 %len, ptr %sl
  %sd   = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_drop ──────────────────────────────────────────────────────────
define void @suru_string_drop(ptr %s) {
entry:
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  call void @free(ptr %data)
  call void @free(ptr %s)
  ret void
}

; ─── suru_string_append ────────────────────────────────────────────────────────
define ptr @suru_string_append(ptr %lhs, ptr %rhs) {
entry:
  %ll   = getelementptr %suru.String, ptr %lhs, i32 0, i32 1
  %llen = load i64, ptr %ll
  %ld   = getelementptr %suru.String, ptr %lhs, i32 0, i32 2
  %ldat = load ptr, ptr %ld
  %rl   = getelementptr %suru.String, ptr %rhs, i32 0, i32 1
  %rlen = load i64, ptr %rl
  %rd   = getelementptr %suru.String, ptr %rhs, i32 0, i32 2
  %rdat = load ptr, ptr %rd
  %tot  = add i64 %llen, %rlen
  %bsz  = add i64 %tot, 1
  %buf  = call ptr @malloc(i64 %bsz)
  call ptr @memcpy(ptr %buf, ptr %ldat, i64 %llen)
  %mid  = getelementptr i8, ptr %buf, i64 %llen
  call ptr @memcpy(ptr %mid, ptr %rdat, i64 %rlen)
  %nulp = getelementptr i8, ptr %buf, i64 %tot
  store i8 0, ptr %nulp
  %seq  = call ptr @malloc(i64 24)
  %tg   = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tg
  %sl   = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 %tot, ptr %sl
  %sd   = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_at ────────────────────────────────────────────────────────────
define ptr @suru_string_at(ptr %s, i64 %i) {
entry:
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %chp  = getelementptr i8, ptr %data, i64 %i
  %buf  = call ptr @malloc(i64 2)
  %ch   = load i8, ptr %chp
  store i8 %ch, ptr %buf
  %np   = getelementptr i8, ptr %buf, i64 1
  store i8 0, ptr %np
  %seq  = call ptr @malloc(i64 24)
  %tg   = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tg
  %sl   = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 1, ptr %sl
  %sd   = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_equals ────────────────────────────────────────────────────────
define i1 @suru_string_equals(ptr %lhs, ptr %rhs) {
entry:
  %ld  = getelementptr %suru.String, ptr %lhs, i32 0, i32 2
  %ldt = load ptr, ptr %ld
  %rd  = getelementptr %suru.String, ptr %rhs, i32 0, i32 2
  %rdt = load ptr, ptr %rd
  %cmp = call i32 @strcmp(ptr %ldt, ptr %rdt)
  %eq  = icmp eq i32 %cmp, 0
  ret i1 %eq
}

; ─── suru_string_slice ─────────────────────────────────────────────────────────
define ptr @suru_string_slice(ptr %s, i64 %from, i64 %to) {
entry:
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %slen = sub i64 %to, %from
  %srcp = getelementptr i8, ptr %data, i64 %from
  %bsz  = add i64 %slen, 1
  %buf  = call ptr @malloc(i64 %bsz)
  call ptr @memcpy(ptr %buf, ptr %srcp, i64 %slen)
  %nulp = getelementptr i8, ptr %buf, i64 %slen
  store i8 0, ptr %nulp
  %seq  = call ptr @malloc(i64 24)
  %tg   = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tg
  %sl   = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 %slen, ptr %sl
  %sd   = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_ord ───────────────────────────────────────────────────────────
define i64 @suru_string_ord(ptr %s) {
entry:
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %b    = load i8, ptr %data
  %ext  = zext i8 %b to i64
  ret i64 %ext
}

; ─── suru_int64_from_string ────────────────────────────────────────────────────
define i64 @suru_int64_from_string(ptr %s) {
entry:
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %v    = call i64 @strtol(ptr %data, ptr null, i32 10)
  ret i64 %v
}

; ─── suru_int64_to_string ──────────────────────────────────────────────────────
define ptr @suru_int64_to_string(i64 %v) {
entry:
  %c0  = call i32 (ptr, i64, ptr, ...) @snprintf(ptr null, i64 0, ptr @.srt_fmt_lld, i64 %v)
  %c64 = sext i32 %c0 to i64
  %bsz = add i64 %c64, 1
  %buf = call ptr @malloc(i64 %bsz)
  call i32 (ptr, i64, ptr, ...) @snprintf(ptr %buf, i64 %bsz, ptr @.srt_fmt_lld, i64 %v)
  %seq = call ptr @malloc(i64 24)
  %tg  = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tg
  %sl  = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 %c64, ptr %sl
  %sd  = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %buf, ptr %sd
  ret ptr %seq
}

""";

    // ─── Array runtime ────────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_array.ll.
    //
    // Layout: %suru.Array = { i64 type_tag=5, i64 elem_tag, i64 len, i64 cap, ptr data } (40 bytes)
    // type_tag at field 0 means any heap ptr can be identified as an Array at runtime.
    // elem_tag holds the full SuruType ordinal (0-6) for the element type.
    // All elements are stored as raw i64 (ptrtoint for ptr types, payload bits for scalars).
    // suru_array_at returns ptr (inttoptr of raw i64) — no element-type dispatch at call site.
    // suru_array_add / suru_array_set take ptr (ptrtoint to store) — uniform interface.
    // suru_array_clone_dyn / suru_array_drop_dyn read element type_tag at runtime.
    public static string GenerateArrayRuntime() => """
; Suru array runtime — compiled to suru_array.o and linked with every Suru program.
;
; Every Suru Array is a ptr to a heap-allocated
;   %suru.Array = { i64 type_tag, i64 elem_tag, i64 len, i64 cap, ptr data }  (40 bytes)
; type_tag=5 (TYPE_ARRAY) at field 0: any heap ptr inspected at offset 0 identifies this as Array.
; data is a flat i64[] buffer; each element is a ptr-as-i64 (Box for scalars, heap ptr for rest).

; ModuleID = 'suru_array.ll'
source_filename = "suru_array.ll"

%suru.Array = type { i64, i64, i64, i64, ptr }

declare ptr  @malloc(i64)
declare ptr  @realloc(ptr, i64)
declare ptr  @memcpy(ptr, ptr, i64)
declare void @free(ptr)

; Cross-module calls for element clone/drop.
declare ptr  @suru_box_clone(ptr)
declare ptr  @suru_string_clone(ptr)
declare void @suru_string_drop(ptr)
declare ptr  @suru_struct_clone(ptr)
declare void @suru_struct_drop(ptr)

; ─── suru_array_at ─────────────────────────────────────────────────────────────
;
; Load the element at index idx and return it as ptr (inttoptr of the stored i64).
; Works for all element types: scalars are Box ptrs, heap types are direct ptrs.
define ptr @suru_array_at(ptr %arr, i64 %idx) {
entry:
  %dgep   = getelementptr %suru.Array, ptr %arr, i32 0, i32 4
  %data   = load ptr, ptr %dgep
  %slot   = getelementptr i64, ptr %data, i64 %idx
  %raw    = load i64, ptr %slot
  %result = inttoptr i64 %raw to ptr
  ret ptr %result
}

; ─── suru_array_set ────────────────────────────────────────────────────────────
;
; Store val (a ptr) at element index idx, encoding it as i64 via ptrtoint.
define void @suru_array_set(ptr %arr, i64 %idx, ptr %val) {
entry:
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 4
  %data = load ptr, ptr %dgep
  %slot = getelementptr i64, ptr %data, i64 %idx
  %raw  = ptrtoint ptr %val to i64
  store i64 %raw, ptr %slot
  ret void
}

; ─── suru_array_add ────────────────────────────────────────────────────────────
;
; Append val (a ptr) to the array with amortised growth.
; Mutates the %suru.Array header in-place: cap and data may change in the grow path.
;
; Growth policy:
;   cap == 0       → new_cap = 4
;   0 < cap < 1024 → new_cap = cap * 2
;   cap >= 1024    → new_cap = cap + 1024
define void @suru_array_add(ptr %arr, ptr %val) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %len  = load i64, ptr %lgep
  %cgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 3
  %cap  = load i64, ptr %cgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 4
  %raw  = ptrtoint ptr %val to i64
  %full = icmp eq i64 %len, %cap
  br i1 %full, label %grow, label %store
grow:
  %dbl  = mul i64 %cap, 2
  %lin  = add i64 %cap, 1024
  %udbl = icmp ult i64 %cap, 1024
  %grwn = select i1 %udbl, i64 %dbl, i64 %lin
  %isz  = icmp eq i64 %cap, 0
  %ncap = select i1 %isz, i64 4, i64 %grwn
  %nbyt = mul i64 %ncap, 8
  %data = load ptr, ptr %dgep
  %ndat = call ptr @realloc(ptr %data, i64 %nbyt)
  store i64 %ncap, ptr %cgep
  store ptr %ndat, ptr %dgep
  br label %store
store:
  %cdat = load ptr, ptr %dgep
  %slot = getelementptr i64, ptr %cdat, i64 %len
  store i64 %raw, ptr %slot
  %nlen = add i64 %len, 1
  store i64 %nlen, ptr %lgep
  ret void
}

; ─── suru_array_slice ──────────────────────────────────────────────────────────
;
; Return a new array header containing a bitwise copy of elements [from, to).
; The raw i64 values are copied as-is. elem_tag is propagated from source.
define ptr @suru_array_slice(ptr %arr, i64 %from, i64 %to) {
entry:
  %egep = getelementptr %suru.Array, ptr %arr, i32 0, i32 1
  %etag = load i64, ptr %egep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 4
  %data = load ptr, ptr %dgep
  %slen = sub i64 %to, %from
  %bc   = mul i64 %slen, 8
  %srcp = getelementptr i64, ptr %data, i64 %from
  %nd   = call ptr @malloc(i64 %bc)
  call ptr @memcpy(ptr %nd, ptr %srcp, i64 %bc)
  %nh   = call ptr @malloc(i64 40)
  %tgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 0
  store i64 5, ptr %tgg
  %egg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 1
  store i64 %etag, ptr %egg
  %lgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 2
  store i64 %slen, ptr %lgg
  %cgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 3
  store i64 %slen, ptr %cgg
  %dgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 4
  store ptr %nd, ptr %dgg
  ret ptr %nh
}

; ─── suru_array_clone_dyn ──────────────────────────────────────────────────────
;
; Clone an array by reading each element's type_tag at runtime (offset 0 of element ptr).
;   type_tag 0-3 (Box scalar): suru_box_clone
;   type_tag 4   (Struct):     suru_struct_clone
;   type_tag 5   (Array):      suru_array_clone_dyn (recursive)
;   type_tag 6   (String):     suru_string_clone
define ptr @suru_array_clone_dyn(ptr %arr) {
entry:
  %tgep  = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %ttag  = load i64, ptr %tgep
  %egep  = getelementptr %suru.Array, ptr %arr, i32 0, i32 1
  %etag  = load i64, ptr %egep
  %lgep  = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %len   = load i64, ptr %lgep
  %dgep  = getelementptr %suru.Array, ptr %arr, i32 0, i32 4
  %sdat  = load ptr, ptr %dgep
  %bc    = mul i64 %len, 8
  %nh    = call ptr @malloc(i64 40)
  %nd    = call ptr @malloc(i64 %bc)
  %iptr  = alloca i64
  store i64 0, ptr %iptr
  br label %cond
cond:
  %i    = load i64, ptr %iptr
  %done = icmp eq i64 %i, %len
  br i1 %done, label %after, label %body
body:
  %ss   = getelementptr i64, ptr %sdat, i64 %i
  %ri64 = load i64, ptr %ss
  %ep   = inttoptr i64 %ri64 to ptr
  %etg  = load i64, ptr %ep
  switch i64 %etg, label %clone_box [
    i64 6, label %clone_string
    i64 4, label %clone_struct
    i64 5, label %clone_array
  ]
clone_string:
  %cs   = call ptr @suru_string_clone(ptr %ep)
  %csi  = ptrtoint ptr %cs to i64
  %dss  = getelementptr i64, ptr %nd, i64 %i
  store i64 %csi, ptr %dss
  br label %next
clone_struct:
  %cst  = call ptr @suru_struct_clone(ptr %ep)
  %csti = ptrtoint ptr %cst to i64
  %dst  = getelementptr i64, ptr %nd, i64 %i
  store i64 %csti, ptr %dst
  br label %next
clone_array:
  %ca   = call ptr @suru_array_clone_dyn(ptr %ep)
  %cai  = ptrtoint ptr %ca to i64
  %dsa  = getelementptr i64, ptr %nd, i64 %i
  store i64 %cai, ptr %dsa
  br label %next
clone_box:
  %cb   = call ptr @suru_box_clone(ptr %ep)
  %cbi  = ptrtoint ptr %cb to i64
  %dsb  = getelementptr i64, ptr %nd, i64 %i
  store i64 %cbi, ptr %dsb
  br label %next
next:
  %ni   = add i64 %i, 1
  store i64 %ni, ptr %iptr
  br label %cond
after:
  %tgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 0
  store i64 %ttag, ptr %tgg
  %egg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 1
  store i64 %etag, ptr %egg
  %lgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 2
  store i64 %len, ptr %lgg
  %cgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 3
  store i64 %len, ptr %cgg
  %dgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 4
  store ptr %nd, ptr %dgg
  ret ptr %nh
}

; ─── suru_array_drop_dyn ───────────────────────────────────────────────────────
;
; Drop an array by reading each element's type_tag at runtime.
;   type_tag 0-3 (Box scalar): free the Box
;   type_tag 4   (Struct):     suru_struct_drop
;   type_tag 5   (Array):      suru_array_drop_dyn (recursive)
;   type_tag 6   (String):     suru_string_drop
; Then frees the data buffer and the array header.
define void @suru_array_drop_dyn(ptr %arr) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 4
  %data = load ptr, ptr %dgep
  %iptr = alloca i64
  store i64 0, ptr %iptr
  br label %cond
cond:
  %i    = load i64, ptr %iptr
  %done = icmp eq i64 %i, %len
  br i1 %done, label %after, label %body
body:
  %slot = getelementptr i64, ptr %data, i64 %i
  %ri64 = load i64, ptr %slot
  %ep   = inttoptr i64 %ri64 to ptr
  %etg  = load i64, ptr %ep
  switch i64 %etg, label %drop_box [
    i64 6, label %drop_string
    i64 4, label %drop_struct
    i64 5, label %drop_array
  ]
drop_string:
  call void @suru_string_drop(ptr %ep)
  br label %next
drop_struct:
  call void @suru_struct_drop(ptr %ep)
  br label %next
drop_array:
  call void @suru_array_drop_dyn(ptr %ep)
  br label %next
drop_box:
  call void @free(ptr %ep)
  br label %next
next:
  %ni   = add i64 %i, 1
  store i64 %ni, ptr %iptr
  br label %cond
after:
  call void @free(ptr %data)
  call void @free(ptr %arr)
  ret void
}

""";

    // ─── Struct runtime ───────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_struct.ll.
    //
    // %suru.Field = { i64 type_tag=4, ptr name, i32 field_tag, i64 val, ptr next } (40 bytes)
    // type_tag=4 (TYPE_STRUCT) at field 0 — the user's .ll stores this value at struct creation.
    // suru_struct_clone copies the type_tag from source, so the clone inherits the correct tag.
    public static string GenerateStructRuntime() => """
; Suru struct runtime — compiled to suru_struct.o and linked with every Suru program.
;
; Every Suru Struct is a ptr to the head of a singly-linked list of %suru.Field nodes:
;   %suru.Field = { i64 type_tag, ptr name, i32 field_tag, i64 val, ptr next }  (40 bytes)
; type_tag=4 (TYPE_STRUCT) at field 0: any heap ptr inspected at offset 0 identifies this as Struct.
; The user's .ll stores type_tag=4 at node creation; suru_struct_clone propagates it automatically.
;
; Field name strings are interned in the user module's string literal table and passed
; as raw ptr (not Seq-wrapped). suru_find_field compares them via strcmp at runtime.

; ModuleID = 'suru_struct.ll'
source_filename = "suru_struct.ll"

%suru.Field = type { i64, ptr, i32, i64, ptr }

declare ptr  @malloc(i64)
declare void @free(ptr)
declare i32  @strcmp(ptr, ptr)

; Cross-module refs for dynamic dispatch.
declare ptr  @suru_box_clone(ptr)
declare ptr  @suru_string_clone(ptr)
declare void @suru_string_drop(ptr)
declare ptr  @suru_array_clone_dyn(ptr)
declare void @suru_array_drop_dyn(ptr)

; ─── suru_clone_dyn ────────────────────────────────────────────────────────────
;
; Clone any Suru heap value by reading type_tag at offset 0.
;   tag 0-3 (Box): suru_box_clone   tag 4 (Struct): suru_struct_clone
;   tag 5 (Array): suru_array_clone_dyn   tag 6 (String): suru_string_clone
define ptr @suru_clone_dyn(ptr %val) {
entry:
  %tg = load i64, ptr %val
  switch i64 %tg, label %clone_box [
    i64 4, label %clone_struct
    i64 5, label %clone_array
    i64 6, label %clone_string
  ]
clone_box:
  %r0 = call ptr @suru_box_clone(ptr %val)
  ret ptr %r0
clone_string:
  %r6 = call ptr @suru_string_clone(ptr %val)
  ret ptr %r6
clone_struct:
  %r4 = call ptr @suru_struct_clone(ptr %val)
  ret ptr %r4
clone_array:
  %r5 = call ptr @suru_array_clone_dyn(ptr %val)
  ret ptr %r5
}

; ─── suru_drop_dyn ─────────────────────────────────────────────────────────────
;
; Drop any Suru heap value by reading type_tag at offset 0.
;   tag 0-3 (Box): free   tag 4 (Struct): suru_struct_drop
;   tag 5 (Array): suru_array_drop_dyn   tag 6 (String): suru_string_drop
define void @suru_drop_dyn(ptr %val) {
entry:
  %tg = load i64, ptr %val
  switch i64 %tg, label %drop_box [
    i64 4, label %drop_struct
    i64 5, label %drop_array
    i64 6, label %drop_string
  ]
drop_box:
  call void @free(ptr %val)
  ret void
drop_string:
  call void @suru_string_drop(ptr %val)
  ret void
drop_struct:
  call void @suru_struct_drop(ptr %val)
  ret void
drop_array:
  call void @suru_array_drop_dyn(ptr %val)
  ret void
}

; ─── suru_find_field ───────────────────────────────────────────────────────────
;
; Walk the linked list starting at `head`, compare each node's stored name ptr via
; strcmp, and return the first matching node ptr. Assumes the field exists (no
; null-termination check). Uses a phi-loop so LLVM can recognise it as a simple loop.
define ptr @suru_find_field(ptr %head, ptr %name) {
entry:
  br label %loop
loop:
  %node = phi ptr [ %head, %entry ], [ %next, %cont ]
  %ngep = getelementptr %suru.Field, ptr %node, i32 0, i32 1
  %stor = load ptr, ptr %ngep
  %cmp  = call i32 @strcmp(ptr %stor, ptr %name)
  %fnd  = icmp eq i32 %cmp, 0
  br i1 %fnd, label %done, label %cont
cont:
  %nxgp = getelementptr %suru.Field, ptr %node, i32 0, i32 4
  %next = load ptr, ptr %nxgp
  br label %loop
done:
  ret ptr %node
}

; ─── suru_struct_clone ─────────────────────────────────────────────────────────
;
; Deep-copy a struct field-node linked list. Allocates a new 40-byte node for each
; source node, copies slots 0-3 (type_tag, name, field_tag, val); the new node's next
; ptr starts as null. The head of the new list is tracked via a `chead` alloca, set
; on the first node. The previous node's next slot is wired on every subsequent node.
define ptr @suru_struct_clone(ptr %head) {
entry:
  %csrc  = alloca ptr
  %cprev = alloca ptr
  %chead = alloca ptr
  store ptr %head, ptr %csrc
  store ptr null, ptr %cprev
  store ptr null, ptr %chead
  br label %cond
cond:
  %sv   = load ptr, ptr %csrc
  %isnl = icmp eq ptr %sv, null
  br i1 %isnl, label %done, label %body
body:
  %cn   = call ptr @malloc(i64 40)
  %sn0  = getelementptr %suru.Field, ptr %sv, i32 0, i32 0
  %tv0  = load i64, ptr %sn0
  %dn0  = getelementptr %suru.Field, ptr %cn, i32 0, i32 0
  store i64 %tv0, ptr %dn0
  %sn1  = getelementptr %suru.Field, ptr %sv, i32 0, i32 1
  %nv1  = load ptr, ptr %sn1
  %dn1  = getelementptr %suru.Field, ptr %cn, i32 0, i32 1
  store ptr %nv1, ptr %dn1
  %st2  = getelementptr %suru.Field, ptr %sv, i32 0, i32 2
  %tv2  = load i32, ptr %st2
  %dt2  = getelementptr %suru.Field, ptr %cn, i32 0, i32 2
  store i32 %tv2, ptr %dt2
  %sv3  = getelementptr %suru.Field, ptr %sv, i32 0, i32 3
  %vv3  = load i64, ptr %sv3
  %dv3  = getelementptr %suru.Field, ptr %cn, i32 0, i32 3
  store i64 %vv3, ptr %dv3
  %dn4  = getelementptr %suru.Field, ptr %cn, i32 0, i32 4
  store ptr null, ptr %dn4
  %pv   = load ptr, ptr %cprev
  %ifl  = icmp eq ptr %pv, null
  br i1 %ifl, label %sethead, label %wire
sethead:
  store ptr %cn, ptr %chead
  br label %cont
wire:
  %pnx  = getelementptr %suru.Field, ptr %pv, i32 0, i32 4
  store ptr %cn, ptr %pnx
  br label %cont
cont:
  store ptr %cn, ptr %cprev
  %snx  = getelementptr %suru.Field, ptr %sv, i32 0, i32 4
  %nxt  = load ptr, ptr %snx
  store ptr %nxt, ptr %csrc
  br label %cond
done:
  %res  = load ptr, ptr %chead
  ret ptr %res
}

; ─── suru_struct_drop ──────────────────────────────────────────────────────────
;
; Free all field nodes in the linked list. Loads `next` before calling free(current)
; to avoid use-after-free. Terminates when the current node is null.
define void @suru_struct_drop(ptr %head) {
entry:
  %dsrc = alloca ptr
  store ptr %head, ptr %dsrc
  br label %cond
cond:
  %v    = load ptr, ptr %dsrc
  %isnl = icmp eq ptr %v, null
  br i1 %isnl, label %done, label %body
body:
  %ng   = getelementptr %suru.Field, ptr %v, i32 0, i32 4
  %nxt  = load ptr, ptr %ng
  store ptr %nxt, ptr %dsrc
  call void @free(ptr %v)
  br label %cond
done:
  ret void
}

""";
}
