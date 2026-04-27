namespace Suru.Compiler.Codegen;

// Generates the three Suru runtime LLVM IR modules that are compiled and linked
// with every Suru program.  The runtime functions are extracted from inline codegen
// into standalone compilation units so each user .ll file only contains
// `declare` stubs; the linker resolves the symbols at link time.
//
// ── Modules ──────────────────────────────────────────────────────────────────
//
//   suru_string.ll — String heap operations: create, clone, drop, append, at,
//                    equals, slice, ord, suru_int64_from_string, suru_int64_to_string.
//
//   suru_array.ll  — Array heap operations: at, set, add (with amortised growth),
//                    slice, clone_scalar/string/struct, drop_scalar/string/struct.
//                    Cross-module calls into suru_string.ll and suru_struct.ll for
//                    clone/drop of pointer-typed elements.
//
//   suru_struct.ll — Struct linked-list operations: suru_find_field (phi-loop via strcmp),
//                    suru_struct_clone (node-by-node copy), suru_struct_drop (linked-list free).
//
// ── Conventions ──────────────────────────────────────────────────────────────
//
//   %suru.Seq   = { i64 len, ptr data }       — String header (16 bytes)
//   %suru.Array = { i64 len, i64 cap, ptr data } — Array header (24 bytes)
//   %suru.Field = { ptr name, i32 tag, i64 val, ptr next } — Struct field node (32 bytes)
//
//   Array elements of any type are stored as raw i64.  Scalar types use identity /
//   zext / bitcast; pointer types use ptrtoint/inttoptr.  The caller is responsible
//   for the ToI64/FromI64 conversion; the runtime functions operate on raw i64 buffers.
//
// ── Cross-module dependencies ─────────────────────────────────────────────────
//
//   suru_string.ll: depends on libc only (malloc, memcpy, free, strcmp, strtol, snprintf).
//   suru_struct.ll: depends on libc only (malloc, free, strcmp).
//   suru_array.ll:  depends on libc + suru_string_clone/drop + suru_struct_clone/drop.
//
// All three .o files are linked into every Suru binary so cross-module calls resolve.
public static class SuruRuntime
{
    // ─── String runtime ───────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_string.ll.
    //
    // Conventions:
    //   - Every function takes/returns `ptr` for Seq pointers.
    //   - All Seq headers are heap-allocated; their data pointers are heap-owned.
    //   - The format-string global @.srt_fmt_lld ("%lld\0") is private to this module.
    public static string GenerateStringRuntime() => """
; Suru string runtime — compiled to suru_string.o and linked with every Suru program.
;
; Every Suru String is a ptr to a heap-allocated %suru.Seq = { i64 len, ptr data }.
; `data` is a null-terminated, heap-owned i8 buffer; `len` is the character count
; excluding the null terminator.
;
; All functions are externally linkable so the linker can resolve calls from user .o files.

; ModuleID = 'suru_string.ll'
source_filename = "suru_string.ll"

%suru.Seq = type { i64, ptr }

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
; Allocate a new 16-byte %suru.Seq header, store `data` and `len`, return the ptr.
; The caller is responsible for ensuring `data` is heap-owned so it can be safely
; freed by suru_string_drop.
define ptr @suru_string_create(ptr %data, i64 %len) {
entry:
  %seq  = call ptr @malloc(i64 16)
  %lgep = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0
  store i64 %len, ptr %lgep
  %dgep = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1
  store ptr %data, ptr %dgep
  ret ptr %seq
}

; ─── suru_string_clone ─────────────────────────────────────────────────────────
;
; Produce an independent copy of a String.
; Allocates a new Seq header and a new char buffer of len+1 bytes (includes null terminator).
; memcpy copies all bytes from the source buffer including the terminator.
define ptr @suru_string_clone(ptr %s) {
entry:
  %lgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 1
  %data = load ptr, ptr %dgep
  %bufb = add i64 %len, 1
  %buf  = call ptr @malloc(i64 %bufb)
  call ptr @memcpy(ptr %buf, ptr %data, i64 %bufb)
  %seq  = call ptr @malloc(i64 16)
  %sl   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0
  store i64 %len, ptr %sl
  %sd   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_drop ──────────────────────────────────────────────────────────
;
; Free the memory backing a String: free(data) then free(Seq header).
; Always safe because every Seq's data ptr is heap-owned (suru_string_create ensures this).
define void @suru_string_drop(ptr %s) {
entry:
  %dgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 1
  %data = load ptr, ptr %dgep
  call void @free(ptr %data)
  call void @free(ptr %s)
  ret void
}

; ─── suru_string_append ────────────────────────────────────────────────────────
;
; Concatenate two Strings into a new heap-allocated String.
; Strategy: extract both lens and data ptrs; malloc totalLen+1; memcpy first half,
; GEP to midpoint and memcpy second half; null-terminate; wrap in a new Seq.
define ptr @suru_string_append(ptr %lhs, ptr %rhs) {
entry:
  %ll   = getelementptr %suru.Seq, ptr %lhs, i32 0, i32 0
  %llen = load i64, ptr %ll
  %ld   = getelementptr %suru.Seq, ptr %lhs, i32 0, i32 1
  %ldat = load ptr, ptr %ld
  %rl   = getelementptr %suru.Seq, ptr %rhs, i32 0, i32 0
  %rlen = load i64, ptr %rl
  %rd   = getelementptr %suru.Seq, ptr %rhs, i32 0, i32 1
  %rdat = load ptr, ptr %rd
  %tot  = add i64 %llen, %rlen
  %bsz  = add i64 %tot, 1
  %buf  = call ptr @malloc(i64 %bsz)
  call ptr @memcpy(ptr %buf, ptr %ldat, i64 %llen)
  %mid  = getelementptr i8, ptr %buf, i64 %llen
  call ptr @memcpy(ptr %mid, ptr %rdat, i64 %rlen)
  %nulp = getelementptr i8, ptr %buf, i64 %tot
  store i8 0, ptr %nulp
  %seq  = call ptr @malloc(i64 16)
  %sl   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0
  store i64 %tot, ptr %sl
  %sd   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_at ────────────────────────────────────────────────────────────
;
; Return a single-character String at byte index i.
; Allocates a 2-byte buffer ([char, '\0']), copies the byte at index i, wraps in new Seq.
define ptr @suru_string_at(ptr %s, i64 %i) {
entry:
  %dgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 1
  %data = load ptr, ptr %dgep
  %chp  = getelementptr i8, ptr %data, i64 %i
  %buf  = call ptr @malloc(i64 2)
  %ch   = load i8, ptr %chp
  store i8 %ch, ptr %buf
  %np   = getelementptr i8, ptr %buf, i64 1
  store i8 0, ptr %np
  %seq  = call ptr @malloc(i64 16)
  %sl   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0
  store i64 1, ptr %sl
  %sd   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_equals ────────────────────────────────────────────────────────
;
; Byte-exact comparison via strcmp. Returns i1 true when the strings are identical.
define i1 @suru_string_equals(ptr %lhs, ptr %rhs) {
entry:
  %ld  = getelementptr %suru.Seq, ptr %lhs, i32 0, i32 1
  %ldt = load ptr, ptr %ld
  %rd  = getelementptr %suru.Seq, ptr %rhs, i32 0, i32 1
  %rdt = load ptr, ptr %rd
  %cmp = call i32 @strcmp(ptr %ldt, ptr %rdt)
  %eq  = icmp eq i32 %cmp, 0
  ret i1 %eq
}

; ─── suru_string_slice ─────────────────────────────────────────────────────────
;
; Return the substring covering bytes [from, to).
; Allocates sliceLen+1 bytes, memcpy's the range, null-terminates, wraps in new Seq.
define ptr @suru_string_slice(ptr %s, i64 %from, i64 %to) {
entry:
  %dgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 1
  %data = load ptr, ptr %dgep
  %slen = sub i64 %to, %from
  %srcp = getelementptr i8, ptr %data, i64 %from
  %bsz  = add i64 %slen, 1
  %buf  = call ptr @malloc(i64 %bsz)
  call ptr @memcpy(ptr %buf, ptr %srcp, i64 %slen)
  %nulp = getelementptr i8, ptr %buf, i64 %slen
  store i8 0, ptr %nulp
  %seq  = call ptr @malloc(i64 16)
  %sl   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0
  store i64 %slen, ptr %sl
  %sd   = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1
  store ptr %buf, ptr %sd
  ret ptr %seq
}

; ─── suru_string_ord ───────────────────────────────────────────────────────────
;
; Return the ASCII code of the first byte as i64.
define i64 @suru_string_ord(ptr %s) {
entry:
  %dgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 1
  %data = load ptr, ptr %dgep
  %b    = load i8, ptr %data
  %ext  = zext i8 %b to i64
  ret i64 %ext
}

; ─── suru_int64_from_string ────────────────────────────────────────────────────
;
; Parse a decimal String to i64 via strtol(data, null, 10).
define i64 @suru_int64_from_string(ptr %s) {
entry:
  %dgep = getelementptr %suru.Seq, ptr %s, i32 0, i32 1
  %data = load ptr, ptr %dgep
  %v    = call i64 @strtol(ptr %data, ptr null, i32 10)
  ret i64 %v
}

; ─── suru_int64_to_string ──────────────────────────────────────────────────────
;
; Format an i64 as a decimal String using the two-call snprintf pattern:
;   1. snprintf(null, 0, "%lld", v) → char count (i32)
;   2. malloc(count + 1) → exact-size buffer
;   3. snprintf(buf, count+1, "%lld", v) → fill buffer
;   4. Wrap in a new Seq
define ptr @suru_int64_to_string(i64 %v) {
entry:
  %c0  = call i32 (ptr, i64, ptr, ...) @snprintf(ptr null, i64 0, ptr @.srt_fmt_lld, i64 %v)
  %c64 = sext i32 %c0 to i64
  %bsz = add i64 %c64, 1
  %buf = call ptr @malloc(i64 %bsz)
  call i32 (ptr, i64, ptr, ...) @snprintf(ptr %buf, i64 %bsz, ptr @.srt_fmt_lld, i64 %v)
  %seq = call ptr @malloc(i64 16)
  %sl  = getelementptr %suru.Seq, ptr %seq, i32 0, i32 0
  store i64 %c64, ptr %sl
  %sd  = getelementptr %suru.Seq, ptr %seq, i32 0, i32 1
  store ptr %buf, ptr %sd
  ret ptr %seq
}

""";

    // ─── Array runtime ────────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_array.ll.
    //
    // Conventions:
    //   - All element values are passed/returned as raw i64. The caller applies
    //     ToI64 before calling set/add and FromI64 after calling at.
    //   - suru_array_add mutates the header in-place via the passed ptr.
    //   - Clone/drop variants are per element type; the caller (user codegen) picks
    //     the right variant based on the statically-known element type.
    //   - suru_array_clone_string/struct call into suru_string.ll / suru_struct.ll
    //     via extern declares — resolved at link time.
    public static string GenerateArrayRuntime() => """
; Suru array runtime — compiled to suru_array.o and linked with every Suru program.
;
; Every Suru Array is a ptr to a heap-allocated
;   %suru.Array = { i64 len, i64 cap, ptr data }  (24 bytes)
; where data is a flat i64[] buffer (8 bytes per element regardless of element type).
;
; Elements are stored as raw i64: Bool via zext/trunc, Float64 via bitcast, pointers
; via ptrtoint/inttoptr. Callers apply the conversion before passing to set/add and
; after receiving from at. The runtime functions are element-type-agnostic for scalars.

; ModuleID = 'suru_array.ll'
source_filename = "suru_array.ll"

%suru.Array = type { i64, i64, ptr }

declare ptr  @malloc(i64)
declare ptr  @realloc(ptr, i64)
declare ptr  @memcpy(ptr, ptr, i64)
declare void @free(ptr)

; Cross-module calls for pointer-element clone/drop.
declare ptr  @suru_string_clone(ptr)
declare void @suru_string_drop(ptr)
declare ptr  @suru_struct_clone(ptr)
declare void @suru_struct_drop(ptr)

; ─── suru_array_at ─────────────────────────────────────────────────────────────
;
; Load the raw i64 stored at element index idx. The caller applies FromI64 to
; convert the raw bits back to the element's Suru type.
define i64 @suru_array_at(ptr %arr, i64 %idx) {
entry:
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %slot = getelementptr i64, ptr %data, i64 %idx
  %raw  = load i64, ptr %slot
  ret i64 %raw
}

; ─── suru_array_set ────────────────────────────────────────────────────────────
;
; Store a raw i64 at element index idx. The caller applies ToI64 before calling.
define void @suru_array_set(ptr %arr, i64 %idx, i64 %val) {
entry:
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %slot = getelementptr i64, ptr %data, i64 %idx
  store i64 %val, ptr %slot
  ret void
}

; ─── suru_array_add ────────────────────────────────────────────────────────────
;
; Append a raw i64 to the array. The caller applies ToI64 before calling.
; Mutates the %suru.Array header in-place: cap and data may change in the grow path.
;
; Growth policy (one branch total; cap choice via two select instructions):
;   cap == 0        → new_cap = 4          (first push: start small)
;   0 < cap < 1024  → new_cap = cap * 2   (doubling; amortised O(1))
;   cap >= 1024     → new_cap = cap + 1024 (linear; bounded per-push cost)
define void @suru_array_add(ptr %arr, i64 %val) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %cgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 1
  %cap  = load i64, ptr %cgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %data = load ptr, ptr %dgep
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
  %ndat = call ptr @realloc(ptr %data, i64 %nbyt)
  store i64 %ncap, ptr %cgep
  store ptr %ndat, ptr %dgep
  br label %store
store:
  %cdat = load ptr, ptr %dgep
  %slot = getelementptr i64, ptr %cdat, i64 %len
  store i64 %val, ptr %slot
  %nlen = add i64 %len, 1
  store i64 %nlen, ptr %lgep
  ret void
}

; ─── suru_array_slice ──────────────────────────────────────────────────────────
;
; Return a new array header containing a bitwise copy of elements [from, to).
; The raw i64 values are copied as-is (pointer elements are not cloned).
define ptr @suru_array_slice(ptr %arr, i64 %from, i64 %to) {
entry:
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %slen = sub i64 %to, %from
  %bc   = mul i64 %slen, 8
  %srcp = getelementptr i64, ptr %data, i64 %from
  %nd   = call ptr @malloc(i64 %bc)
  call ptr @memcpy(ptr %nd, ptr %srcp, i64 %bc)
  %nh   = call ptr @malloc(i64 24)
  %lgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 0
  store i64 %slen, ptr %lgg
  %cgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 1
  store i64 %slen, ptr %cgg
  %dgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 2
  store ptr %nd, ptr %dgg
  ret ptr %nh
}

; ─── suru_array_clone_scalar ───────────────────────────────────────────────────
;
; Clone an array whose elements are scalars (Int64, Float64, Bool) or nested Arrays.
; A single memcpy of len*8 bytes copies all raw i64 values. cap is set to len (exact fit).
define ptr @suru_array_clone_scalar(ptr %arr) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %data = load ptr, ptr %dgep
  %bc   = mul i64 %len, 8
  %nh   = call ptr @malloc(i64 24)
  %nd   = call ptr @malloc(i64 %bc)
  call ptr @memcpy(ptr %nd, ptr %data, i64 %bc)
  %lgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 0
  store i64 %len, ptr %lgg
  %cgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 1
  store i64 %len, ptr %cgg
  %dgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 2
  store ptr %nd, ptr %dgg
  ret ptr %nh
}

; ─── suru_array_clone_string ───────────────────────────────────────────────────
;
; Clone an Array<String>: loop over elements, call suru_string_clone on each ptr,
; store the new ptr (as i64) into the new data buffer.
define ptr @suru_array_clone_string(ptr %arr) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %sdat = load ptr, ptr %dgep
  %bc   = mul i64 %len, 8
  %nh   = call ptr @malloc(i64 24)
  %nd   = call ptr @malloc(i64 %bc)
  %iptr = alloca i64
  store i64 0, ptr %iptr
  br label %cond
cond:
  %i    = load i64, ptr %iptr
  %done = icmp eq i64 %i, %len
  br i1 %done, label %after, label %body
body:
  %ss   = getelementptr i64, ptr %sdat, i64 %i
  %ri64 = load i64, ptr %ss
  %sp   = inttoptr i64 %ri64 to ptr
  %cp   = call ptr @suru_string_clone(ptr %sp)
  %ci64 = ptrtoint ptr %cp to i64
  %ds   = getelementptr i64, ptr %nd, i64 %i
  store i64 %ci64, ptr %ds
  %ni   = add i64 %i, 1
  store i64 %ni, ptr %iptr
  br label %cond
after:
  %lgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 0
  store i64 %len, ptr %lgg
  %cgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 1
  store i64 %len, ptr %cgg
  %dgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 2
  store ptr %nd, ptr %dgg
  ret ptr %nh
}

; ─── suru_array_clone_struct ───────────────────────────────────────────────────
;
; Clone an Array<Struct>: loop over elements, call suru_struct_clone on each ptr,
; store the new ptr (as i64) into the new data buffer.
define ptr @suru_array_clone_struct(ptr %arr) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %sdat = load ptr, ptr %dgep
  %bc   = mul i64 %len, 8
  %nh   = call ptr @malloc(i64 24)
  %nd   = call ptr @malloc(i64 %bc)
  %iptr = alloca i64
  store i64 0, ptr %iptr
  br label %cond
cond:
  %i    = load i64, ptr %iptr
  %done = icmp eq i64 %i, %len
  br i1 %done, label %after, label %body
body:
  %ss   = getelementptr i64, ptr %sdat, i64 %i
  %ri64 = load i64, ptr %ss
  %sp   = inttoptr i64 %ri64 to ptr
  %cp   = call ptr @suru_struct_clone(ptr %sp)
  %ci64 = ptrtoint ptr %cp to i64
  %ds   = getelementptr i64, ptr %nd, i64 %i
  store i64 %ci64, ptr %ds
  %ni   = add i64 %i, 1
  store i64 %ni, ptr %iptr
  br label %cond
after:
  %lgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 0
  store i64 %len, ptr %lgg
  %cgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 1
  store i64 %len, ptr %cgg
  %dgg  = getelementptr %suru.Array, ptr %nh, i32 0, i32 2
  store ptr %nd, ptr %dgg
  ret ptr %nh
}

; ─── suru_array_drop_scalar ────────────────────────────────────────────────────
;
; Free a scalar-element array: free(data buffer) then free(%suru.Array header).
define void @suru_array_drop_scalar(ptr %arr) {
entry:
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
  %data = load ptr, ptr %dgep
  call void @free(ptr %data)
  call void @free(ptr %arr)
  ret void
}

; ─── suru_array_drop_string ────────────────────────────────────────────────────
;
; Drop an Array<String>: call suru_string_drop on each element ptr, then free
; the data buffer and header.
define void @suru_array_drop_string(ptr %arr) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
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
  %sp   = inttoptr i64 %ri64 to ptr
  call void @suru_string_drop(ptr %sp)
  %ni   = add i64 %i, 1
  store i64 %ni, ptr %iptr
  br label %cond
after:
  call void @free(ptr %data)
  call void @free(ptr %arr)
  ret void
}

; ─── suru_array_drop_struct ────────────────────────────────────────────────────
;
; Drop an Array<Struct>: call suru_struct_drop on each element ptr, then free
; the data buffer and header.
define void @suru_array_drop_struct(ptr %arr) {
entry:
  %lgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 0
  %len  = load i64, ptr %lgep
  %dgep = getelementptr %suru.Array, ptr %arr, i32 0, i32 2
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
  %sp   = inttoptr i64 %ri64 to ptr
  call void @suru_struct_drop(ptr %sp)
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
    // Conventions:
    //   - %suru.Field = { ptr name, i32 tag, i64 val, ptr next } (32 bytes)
    //   - suru_find_field uses a phi-loop + strcmp to locate a field by name.
    //   - suru_struct_clone allocates a new node per field; suru_struct_drop
    //     loads `next` before calling free to avoid use-after-free.
    public static string GenerateStructRuntime() => """
; Suru struct runtime — compiled to suru_struct.o and linked with every Suru program.
;
; Every Suru Struct is a ptr to the head of a singly-linked list of %suru.Field nodes:
;   %suru.Field = { ptr name, i32 tag, i64 val, ptr next }  (32 bytes)
;
; Field name strings are interned in the user module's string literal table and passed
; as raw ptr (not Seq-wrapped). suru_find_field compares them via strcmp at runtime.

; ModuleID = 'suru_struct.ll'
source_filename = "suru_struct.ll"

%suru.Field = type { ptr, i32, i64, ptr }

declare ptr  @malloc(i64)
declare void @free(ptr)
declare i32  @strcmp(ptr, ptr)

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
  %ngep = getelementptr %suru.Field, ptr %node, i32 0, i32 0
  %stor = load ptr, ptr %ngep
  %cmp  = call i32 @strcmp(ptr %stor, ptr %name)
  %fnd  = icmp eq i32 %cmp, 0
  br i1 %fnd, label %done, label %cont
cont:
  %nxgp = getelementptr %suru.Field, ptr %node, i32 0, i32 3
  %next = load ptr, ptr %nxgp
  br label %loop
done:
  ret ptr %node
}

; ─── suru_struct_clone ─────────────────────────────────────────────────────────
;
; Deep-copy a struct field-node linked list. Allocates a new 32-byte node for each
; source node, copies slots 0-2 (name, tag, val); the new node's next ptr starts as null.
; The head of the new list is tracked via a `chead` alloca, set on the first node.
; The previous node's next slot is wired on every subsequent node.
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
  %cn   = call ptr @malloc(i64 32)
  %sn0  = getelementptr %suru.Field, ptr %sv, i32 0, i32 0
  %nv0  = load ptr, ptr %sn0
  %dn0  = getelementptr %suru.Field, ptr %cn, i32 0, i32 0
  store ptr %nv0, ptr %dn0
  %st1  = getelementptr %suru.Field, ptr %sv, i32 0, i32 1
  %tv1  = load i32, ptr %st1
  %dt1  = getelementptr %suru.Field, ptr %cn, i32 0, i32 1
  store i32 %tv1, ptr %dt1
  %sv2  = getelementptr %suru.Field, ptr %sv, i32 0, i32 2
  %vv2  = load i64, ptr %sv2
  %dv2  = getelementptr %suru.Field, ptr %cn, i32 0, i32 2
  store i64 %vv2, ptr %dv2
  %dn3  = getelementptr %suru.Field, ptr %cn, i32 0, i32 3
  store ptr null, ptr %dn3
  %pv   = load ptr, ptr %cprev
  %ifl  = icmp eq ptr %pv, null
  br i1 %ifl, label %sethead, label %wire
sethead:
  store ptr %cn, ptr %chead
  br label %cont
wire:
  %pnx  = getelementptr %suru.Field, ptr %pv, i32 0, i32 3
  store ptr %cn, ptr %pnx
  br label %cont
cont:
  store ptr %cn, ptr %cprev
  %snx  = getelementptr %suru.Field, ptr %sv, i32 0, i32 3
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
  %ng   = getelementptr %suru.Field, ptr %v, i32 0, i32 3
  %nxt  = load ptr, ptr %ng
  store ptr %nxt, ptr %dsrc
  call void @free(ptr %v)
  br label %cond
done:
  ret void
}

""";
}
