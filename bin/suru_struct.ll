; Suru struct runtime — compiled to suru_struct.o and linked with every Suru program.
;
; Phase 2: structs use a flat fixed-layout allocation.
; Header (32 bytes): { i64 type_tag, i64 variant_idx, ptr clone_fn, ptr drop_fn }
; Fields follow at byte offset 32 + i*8, in TypeDeclaration order, stored as raw i64.
; Field slot encoding: scalars as raw value; heap ptrs as ptrtoint(ptr to i64).
;
; clone_fn and drop_fn are per-type LLVM functions emitted in the user module.
; suru_clone_dyn/drop_dyn call them via function pointer for tag=4 and tag=7.
;
; type_tag: 0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String 7=SumType

; ModuleID = 'suru_struct.ll'
source_filename = "suru_struct.ll"

declare ptr  @malloc(i64)
declare void @free(ptr)

; Cross-module refs for dynamic dispatch.
declare ptr  @suru_box_clone(ptr)
declare ptr  @suru_string_clone(ptr)
declare void @suru_string_drop(ptr)
declare ptr  @suru_array_clone_dyn(ptr)
declare void @suru_array_drop_dyn(ptr)

; ─── suru_clone_dyn ────────────────────────────────────────────────────────────
;
; Clone any Suru heap value by reading type_tag at offset 0.
;   tag 0-3 (Box): suru_box_clone   tag 5 (Array): suru_array_clone_dyn
;   tag 6 (String): suru_string_clone
;   tag 4 (Struct) / tag 7 (Variant): call clone_fn from vtable at offset 16
; null guard: zero-initialized struct fields store i64 0; inttoptr gives null —
; return null for null input so the cloned struct also carries a null field.
define ptr @suru_clone_dyn(ptr %val) {
entry:
  %is_null = icmp eq ptr %val, null
  br i1 %is_null, label %ret_null, label %dispatch
ret_null:
  ret ptr null
dispatch:
  %tg = load i64, ptr %val
  switch i64 %tg, label %clone_box [
    i64 4, label %clone_struct
    i64 5, label %clone_array
    i64 6, label %clone_string
    i64 7, label %clone_struct
  ]
clone_box:
  %r0 = call ptr @suru_box_clone(ptr %val)
  ret ptr %r0
clone_string:
  %r6 = call ptr @suru_string_clone(ptr %val)
  ret ptr %r6
clone_struct:
  %cfn_gep = getelementptr i8, ptr %val, i64 16
  %cfn = load ptr, ptr %cfn_gep
  %r4 = call ptr (ptr) %cfn(ptr %val)
  ret ptr %r4
clone_array:
  %r5 = call ptr @suru_array_clone_dyn(ptr %val)
  ret ptr %r5
}

; ─── suru_drop_dyn ─────────────────────────────────────────────────────────────
;
; Drop any Suru heap value by reading type_tag at offset 0.
;   tag 0-3 (Box): free   tag 5 (Array): suru_array_drop_dyn
;   tag 6 (String): suru_string_drop
;   tag 4 (Struct) / tag 7 (Variant): call drop_fn from vtable at offset 24
; null guard: zero-initialized struct fields store i64 0; inttoptr gives null —
; treat null as a no-op so partial/empty struct literals don't crash on drop.
define void @suru_drop_dyn(ptr %val) {
entry:
  %is_null = icmp eq ptr %val, null
  br i1 %is_null, label %done, label %dispatch
dispatch:
  %tg = load i64, ptr %val
  switch i64 %tg, label %drop_box [
    i64 4, label %drop_struct
    i64 5, label %drop_array
    i64 6, label %drop_string
    i64 7, label %drop_struct
  ]
drop_box:
  call void @free(ptr %val)
  ret void
drop_string:
  call void @suru_string_drop(ptr %val)
  ret void
drop_struct:
  %dfn_gep = getelementptr i8, ptr %val, i64 24
  %dfn = load ptr, ptr %dfn_gep
  call void (ptr) %dfn(ptr %val)
  ret void
drop_array:
  call void @suru_array_drop_dyn(ptr %val)
  ret void
done:
  ret void
}
