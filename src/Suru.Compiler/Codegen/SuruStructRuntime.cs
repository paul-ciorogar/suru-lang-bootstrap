namespace Suru.Compiler.Codegen;

public static partial class SuruRuntime
{
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
