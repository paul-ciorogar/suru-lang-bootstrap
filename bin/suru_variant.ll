; Suru variant runtime — compiled to suru_variant.o and linked with every Suru program.
;
; Phase 2: a sum type value is a flat fixed-layout struct where type_tag=7 at byte offset 0
; identifies it as a variant, and variant_idx at byte offset 8 records the active variant.
; The struct header vtable (clone_fn at 16, drop_fn at 24) enables correct clone/drop dispatch.
;
; type_tag: 0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String 7=SumType

; ModuleID = 'suru_variant.ll'
source_filename = "suru_variant.ll"

declare void @free(ptr)

; ─── suru_variant_create ───────────────────────────────────────────────────────
;
; Marks an existing flat struct as a variant: sets type_tag=7 at byte offset 0,
; variant_idx at byte offset 8. Returns the same ptr — no new allocation.
define ptr @suru_variant_create(i64 %idx, ptr %struct_ptr) {
entry:
  %tag_gep = getelementptr i8, ptr %struct_ptr, i64 0
  store i64 7, ptr %tag_gep
  %vidx_gep = getelementptr i8, ptr %struct_ptr, i64 8
  store i64 %idx, ptr %vidx_gep
  ret ptr %struct_ptr
}

; ─── suru_variant_tag ──────────────────────────────────────────────────────────
;
; Returns variant_idx stored at byte offset 8 of the struct.
define i64 @suru_variant_tag(ptr %v) {
entry:
  %vidx_gep = getelementptr i8, ptr %v, i64 8
  %idx = load i64, ptr %vidx_gep
  ret i64 %idx
}

; ─── suru_variant_inner ────────────────────────────────────────────────────────
;
; Returns the ptr unchanged — the variant IS the flat struct.
define ptr @suru_variant_inner(ptr %v) {
entry:
  ret ptr %v
}

; ─── suru_variant_drop ─────────────────────────────────────────────────────────
;
; Calls drop_fn from the vtable at byte offset 24.
define void @suru_variant_drop(ptr %v) {
entry:
  %dfn_gep = getelementptr i8, ptr %v, i64 24
  %dfn = load ptr, ptr %dfn_gep
  call void (ptr) %dfn(ptr %v)
  ret void
}