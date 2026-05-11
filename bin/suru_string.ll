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
declare i64    @strtol(ptr, ptr, i32)
declare double @strtod(ptr, ptr)
declare i32    @snprintf(ptr, i64, ptr, ...)

; Private format strings.
@.srt_fmt_lld    = private unnamed_addr constant [5 x i8]  c"%lld\00"
@.srt_fmt_hex_f64 = private unnamed_addr constant [10 x i8] c"0x%016llX\00"

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

; ─── suru_float64_from_string ──────────────────────────────────────────────────
; Parse a decimal string (e.g. "-2.5") into a raw double via strtod.
define double @suru_float64_from_string(ptr %s) {
entry:
  %dgep = getelementptr %suru.String, ptr %s, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %v    = call double @strtod(ptr %data, ptr null)
  ret double %v
}

; ─── suru_float64_to_llvm_hex ──────────────────────────────────────────────────
; Convert a double to its LLVM IR hex literal string, e.g. "0xC004000000000000".
; The output is always 18 chars: "0x" + 16 uppercase hex digits.
define ptr @suru_float64_to_llvm_hex(double %f) {
entry:
  %bits = bitcast double %f to i64
  %buf  = call ptr @malloc(i64 19)
  call i32 (ptr, i64, ptr, ...) @snprintf(ptr %buf, i64 19, ptr @.srt_fmt_hex_f64, i64 %bits)
  %seq  = call ptr @malloc(i64 24)
  %tg   = getelementptr %suru.String, ptr %seq, i32 0, i32 0
  store i64 6, ptr %tg
  %sl   = getelementptr %suru.String, ptr %seq, i32 0, i32 1
  store i64 18, ptr %sl
  %sd   = getelementptr %suru.String, ptr %seq, i32 0, i32 2
  store ptr %buf, ptr %sd
  ret ptr %seq
}
