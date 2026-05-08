namespace Suru.Compiler.Codegen;

public static partial class SuruRuntime
{
    // ─── Variant runtime ──────────────────────────────────────────────────────

    // Returns the full LLVM IR text for suru_variant.ll.
    //
    // %suru.Variant = { i64 type_tag=7, i64 variant_idx, ptr inner } (24 bytes)
    // type_tag=7 at field 0 identifies this heap value as a sum type variant.
    // variant_idx (field 1) records which variant of the sum type this is.
    // inner (field 2) is a ptr to the heap-allocated struct payload for this variant.
    //
    // suru_variant_drop frees only the 24-byte wrapper; inner struct drop is the
    // caller's responsibility (Stage 13i will wire this up at the language level).
    public static string GenerateVariantRuntime() => """
; Suru variant runtime — compiled to suru_variant.o and linked with every Suru program.
;
; Every Suru sum type value is a ptr to a heap-allocated %suru.Variant:
;   %suru.Variant = { i64 type_tag, i64 variant_idx, ptr inner }  (24 bytes)
; type_tag=7 (TYPE_SUMTYPE) at field 0: any heap ptr inspected at offset 0 identifies this as a variant.
; variant_idx holds the zero-based index of the active variant within its sum type declaration.
; inner points to the heap-allocated struct payload for this variant.
;
; type_tag: 0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String 7=SumType

; ModuleID = 'suru_variant.ll'
source_filename = "suru_variant.ll"

%suru.Variant = type { i64, i64, ptr }

declare ptr  @malloc(i64)
declare void @free(ptr)

; ─── suru_variant_create ───────────────────────────────────────────────────────
;
; Allocates a 24-byte %suru.Variant, stores type_tag=7, variant_idx, and inner ptr.
; Returns a ptr to the new wrapper.
define ptr @suru_variant_create(i64 %idx, ptr %inner) {
entry:
  %mem = call ptr @malloc(i64 24)
  ; store type_tag = 7 at field 0
  %tag_ptr = getelementptr %suru.Variant, ptr %mem, i32 0, i32 0
  store i64 7, ptr %tag_ptr
  ; store variant_idx at field 1
  %idx_ptr = getelementptr %suru.Variant, ptr %mem, i32 0, i32 1
  store i64 %idx, ptr %idx_ptr
  ; store inner ptr at field 2
  %inner_ptr = getelementptr %suru.Variant, ptr %mem, i32 0, i32 2
  store ptr %inner, ptr %inner_ptr
  ret ptr %mem
}

; ─── suru_variant_tag ──────────────────────────────────────────────────────────
;
; Returns the variant_idx stored at field 1 of %suru.Variant.
; Used by match dispatch (Stage 13j) to branch on which variant is active.
define i64 @suru_variant_tag(ptr %v) {
entry:
  %idx_ptr = getelementptr %suru.Variant, ptr %v, i32 0, i32 1
  %idx = load i64, ptr %idx_ptr
  ret i64 %idx
}

; ─── suru_variant_inner ────────────────────────────────────────────────────────
;
; Returns the inner struct ptr stored at field 2 of %suru.Variant.
; Used by field access on variant values (Stage 13i) to reach the payload struct.
define ptr @suru_variant_inner(ptr %v) {
entry:
  %inner_ptr = getelementptr %suru.Variant, ptr %v, i32 0, i32 2
  %inner = load ptr, ptr %inner_ptr
  ret ptr %inner
}

; ─── suru_variant_drop ─────────────────────────────────────────────────────────
;
; Frees the 24-byte %suru.Variant wrapper.
; Does NOT free the inner struct — the caller must drop the payload separately.
define void @suru_variant_drop(ptr %v) {
entry:
  call void @free(ptr %v)
  ret void
}
""";
}
