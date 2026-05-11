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
  %is_null = icmp eq ptr %v, null
  br i1 %is_null, label %unknown, label %dispatch
dispatch:
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
