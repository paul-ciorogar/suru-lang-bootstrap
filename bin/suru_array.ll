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

; Cross-module dynamic dispatch for element clone/drop.
; suru_clone_dyn / suru_drop_dyn handle all type_tags (0-7) via vtable for structs/variants.
declare ptr  @suru_clone_dyn(ptr)
declare void @suru_drop_dyn(ptr)

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
; Clone an array by delegating each element to @suru_clone_dyn.
; suru_clone_dyn dispatches on type_tag at offset 0 and handles all types:
;   tag 0-3 (Box scalar): suru_box_clone
;   tag 4   (Struct):     per-type clone_fn from vtable at offset 16
;   tag 5   (Array):      suru_array_clone_dyn (recursive)
;   tag 6   (String):     suru_string_clone
;   tag 7   (Variant):    per-type clone_fn from vtable at offset 16
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
  %ec   = call ptr @suru_clone_dyn(ptr %ep)
  %eci  = ptrtoint ptr %ec to i64
  %ds   = getelementptr i64, ptr %nd, i64 %i
  store i64 %eci, ptr %ds
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
; Drop an array by delegating each element to @suru_drop_dyn.
; suru_drop_dyn dispatches on type_tag at offset 0 and handles all types including
; tag=4 (Struct) and tag=7 (Variant) via vtable at offset 24.
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
  call void @suru_drop_dyn(ptr %ep)
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
