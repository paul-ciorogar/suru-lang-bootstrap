// Covered fixtures: structs
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Struct IR emission for IRCodeGenerator.
//
// Every Suru Struct value is a `ptr` to the head of a heap-allocated singly-linked list
// of %suru.Field nodes:
//
//   %suru.Field = type { ptr, i32, i64, ptr }
//                        [0]  [1]  [2]  [3]
//                        name tag  val  next
//
//   [0] name  — ptr to a null-terminated field name string (interned via _stringLiterals)
//   [1] tag   — i32 type tag: 0=Bool, 1=Int64, 2=Float64, 3=ptr (String/Array/Struct)
//   [2] val   — i64 storage; same ToI64/FromI64 encoding used by arrays
//   [3] next  — ptr to next field node, null for the tail
//
// Node size: 32 bytes on 64-bit (ptr=8 + i32=4 + pad=4 + i64=8 + ptr=8).
//
// Field name interning:
//   EmitFieldNamePtr reuses _stringLiterals to deduplicate field name constants.
//   The global is used as a raw i8* by suru_find_field — NOT wrapped in a Seq.
//
// suru_find_field:
//   An internal IR helper function emitted once per module (guarded by _findFieldEmitted).
//   It phi-loops through the linked list using strcmp to match field names and returns
//   the node pointer. All field access and assignment go through this runtime helper.
//
// ResolvedType on FieldAccessExpression:
//   The SemanticAnalyzer sets fa.ResolvedType from _structSymbols for fields declared
//   in the same lexical scope. The IR emitter uses this static type to call EmitFromI64
//   directly, avoiding a runtime tag-branch for the common case.
//
// Clone strategy (EmitCloneStruct):
//   Allocates a new node per field via a while-style loop (alloca + load + icmp + branch).
//   The head of the new list is tracked via a `chead` alloca, detected when `cprev == null`.
//   After copying slots 0-2 (name, tag, val), the previous node's `next` slot is wired up.
//
// Drop strategy (EmitDropStruct):
//   Walks the linked list, loading `next` before calling @free(current) to avoid
//   use-after-free. Terminates when the loaded node is null.
//
// Both clone and drop use _structCounter for unique label suffixes so multiple
// clone/drop calls within a single function don't produce duplicate labels.
//
// Why the fixture fix:
//   tests/fixtures/structs/main.suru had `drop(suru)` (line 23) without a matching
//   `let suru:` — the let was commented out, leaving a dangling undefined-variable
//   reference that caused a compile error. That line was commented out as part of
//   this migration.
partial class IRCodeGenerator
{
    // ─── suru_find_field helper ──────────────────────────────────────────────

    // Ensure the suru_find_field helper is present in the module (emitted at most once).
    private void EnsureFindFieldHelper()
    {
        if (_findFieldEmitted) return;
        _findFieldEmitted = true;
        EmitFindFieldHelper();
    }

    // Emit the suru_find_field internal helper into _helpers (module-level, before user fns).
    //
    // Uses a phi-loop: %ff_node starts as %head and advances to the next field on each
    // iteration. strcmp compares the stored name ptr against the search key. The function
    // assumes the field exists — no null-termination check. Called from EmitFindFieldCall.
    private void EmitFindFieldHelper()
    {
        _externals.AddStrcmp();
        _helpers.AppendLine("define internal ptr @suru_find_field(ptr %head, ptr %name) {");
        _helpers.AppendLine("entry:");
        _helpers.AppendLine("  br label %ff_loop");
        _helpers.AppendLine("ff_loop:");
        _helpers.AppendLine("  %ff_node = phi ptr [ %head, %entry ], [ %ff_next, %ff_continue ]");
        _helpers.AppendLine("  %ff_name_slot = getelementptr %suru.Field, ptr %ff_node, i32 0, i32 0");
        _helpers.AppendLine("  %ff_stored = load ptr, ptr %ff_name_slot");
        _helpers.AppendLine("  %ff_cmp = call i32 @strcmp(ptr %ff_stored, ptr %name)");
        _helpers.AppendLine("  %ff_found = icmp eq i32 %ff_cmp, 0");
        _helpers.AppendLine("  br i1 %ff_found, label %ff_done, label %ff_continue");
        _helpers.AppendLine("ff_continue:");
        _helpers.AppendLine("  %ff_next_slot = getelementptr %suru.Field, ptr %ff_node, i32 0, i32 3");
        _helpers.AppendLine("  %ff_next = load ptr, ptr %ff_next_slot");
        _helpers.AppendLine("  br label %ff_loop");
        _helpers.AppendLine("ff_done:");
        _helpers.AppendLine("  ret ptr %ff_node");
        _helpers.AppendLine("}");
        _helpers.AppendLine();
    }

    // Intern a field name as a raw string constant (reuses _stringLiterals dedup logic).
    // Returns the global name (e.g. @.str_3) suitable for use as a raw ptr argument
    // to suru_find_field — NOT a Seq-wrapped Suru String.
    private string EmitFieldNamePtr(string fieldName)
    {
        if (!_stringLiterals.TryGetValue(fieldName, out var entry))
        {
            var globalName = $"@.str_{_strCount++}";
            var byteLen    = fieldName.Length + 1;  // ASCII field names only; +1 for \0
            entry = (globalName, byteLen);
            _stringLiterals[fieldName] = entry;
        }
        return entry.name;
    }

    // Emit a call to @suru_find_field for the named field.
    // Returns the SSA name of the resulting node ptr.
    private string EmitFindFieldCall(string headPtr, string fieldName)
    {
        EnsureFindFieldHelper();
        var nameGlobal = EmitFieldNamePtr(fieldName);
        var node = NextTmp();
        _funcs.AppendLine($"  {node} = call ptr @suru_find_field(ptr {headPtr}, ptr {nameGlobal})");
        return node;
    }

    // ─── Struct literal ──────────────────────────────────────────────────────

    // Emit a `{ field1: val1, field2: val2, ... }` struct literal.
    //
    // Nodes are built in reverse field order so the head of the list is the first
    // declared field — matching the LLVMSharp backend's traversal order.
    // For each field: malloc(32), store name/tag/val/next, link to prior node.
    private (string val, SuruType type) EmitStructLiteral(StructLiteralExpression lit)
    {
        EnsureFindFieldHelper();
        _externals.AddMalloc();

        var prevNodePtr = "null";

        for (int i = lit.Fields.Count - 1; i >= 0; i--)
        {
            var (fieldName, fieldExpr) = lit.Fields[i];
            var (fieldVal, fieldType)  = EmitValue(fieldExpr);

            var nodePtr = NextTmp();
            _funcs.AppendLine($"  {nodePtr} = call ptr @malloc(i64 32)");

            // [0] name ptr
            var nameGep = NextTmp();
            var namePtr = EmitFieldNamePtr(fieldName);
            _funcs.AppendLine($"  {nameGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 0");
            _funcs.AppendLine($"  store ptr {namePtr}, ptr {nameGep}");

            // [1] tag
            int tag = fieldType switch { SuruType.Bool => 0, SuruType.Int64 => 1, SuruType.Float64 => 2, _ => 3 };
            var tagGep = NextTmp();
            _funcs.AppendLine($"  {tagGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 1");
            _funcs.AppendLine($"  store i32 {tag}, ptr {tagGep}");

            // [2] value as i64
            var valGep = NextTmp();
            var as64   = EmitToI64(fieldVal, fieldType);
            _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 2");
            _funcs.AppendLine($"  store i64 {as64}, ptr {valGep}");

            // [3] next
            var nextGep = NextTmp();
            _funcs.AppendLine($"  {nextGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 3");
            _funcs.AppendLine($"  store ptr {prevNodePtr}, ptr {nextGep}");

            prevNodePtr = nodePtr;
        }

        return (prevNodePtr, SuruType.Struct);
    }

    // ─── Field access / assignment ───────────────────────────────────────────

    // .field — load a field value from a struct.
    //
    // Calls suru_find_field to locate the node, loads the raw i64 from slot [2],
    // then applies EmitFromI64 using the statically known ResolvedType. ResolvedType
    // is set by the SemanticAnalyzer for fields declared in the same scope; it is
    // non-null for all field accesses in the current fixture.
    private (string val, SuruType type) EmitFieldAccess(FieldAccessExpression fa)
    {
        var (headPtr, _) = EmitValue(fa.Receiver);
        var node         = EmitFindFieldCall(headPtr, fa.FieldName);

        var valGep = NextTmp();
        var raw    = NextTmp();
        _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {node}, i32 0, i32 2");
        _funcs.AppendLine($"  {raw}    = load i64, ptr {valGep}");

        var fieldType = fa.ResolvedType ?? SuruType.Struct;
        var result    = EmitFromI64(raw, fieldType);
        return (result, fieldType);
    }

    // receiver.field: value — update a field's stored i64 in-place.
    //
    // Locates the node via suru_find_field, converts the new value via EmitToI64,
    // and stores it into slot [2]. The tag is not updated — the field type is fixed
    // at struct-literal construction time.
    private void EmitFieldAssignment(FieldAssignmentStatement fa)
    {
        var (headPtr, _)    = EmitValue(fa.Receiver);
        var (newVal, newType) = EmitValue(fa.Value);
        var node = EmitFindFieldCall(headPtr, fa.FieldName);

        var as64   = EmitToI64(newVal, newType);
        var valGep = NextTmp();
        _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {node}, i32 0, i32 2");
        _funcs.AppendLine($"  store i64 {as64}, ptr {valGep}");
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    // clone(src) → Struct: deep-copy the linked list into a new set of nodes.
    //
    // Allocates a new node for each source node, copying slots 0-2 (name, tag, val).
    // The head of the new list is tracked: when cprev is null (first node), the new
    // node is stored as the new head; otherwise the previous node's next slot is wired
    // to the new node. After the loop the new head is loaded and returned.
    private (string val, SuruType type) EmitCloneStruct(string headPtr)
    {
        _externals.AddMalloc();
        var n = _structCounter++;

        // Loop state allocas
        var csrc  = NextTmp();
        var cprev = NextTmp();
        var chead = NextTmp();
        _funcs.AppendLine($"  {csrc}  = alloca ptr");
        _funcs.AppendLine($"  {cprev} = alloca ptr");
        _funcs.AppendLine($"  {chead} = alloca ptr");
        _funcs.AppendLine($"  store ptr {headPtr}, ptr {csrc}");
        _funcs.AppendLine($"  store ptr null, ptr {cprev}");
        _funcs.AppendLine($"  store ptr null, ptr {chead}");
        _funcs.AppendLine($"  br label %clone_cond_{n}");

        _funcs.AppendLine($"clone_cond_{n}:");
        var csrcV  = NextTmp();
        var cnull  = NextTmp();
        _funcs.AppendLine($"  {csrcV} = load ptr, ptr {csrc}");
        _funcs.AppendLine($"  {cnull} = icmp eq ptr {csrcV}, null");
        _funcs.AppendLine($"  br i1 {cnull}, label %clone_done_{n}, label %clone_body_{n}");

        _funcs.AppendLine($"clone_body_{n}:");
        var cnode = NextTmp();
        _funcs.AppendLine($"  {cnode} = call ptr @malloc(i64 32)");

        // Copy slot [0] name ptr
        var srcName = NextTmp(); var srcNameV = NextTmp(); var dstName = NextTmp();
        _funcs.AppendLine($"  {srcName}  = getelementptr %suru.Field, ptr {csrcV}, i32 0, i32 0");
        _funcs.AppendLine($"  {srcNameV} = load ptr, ptr {srcName}");
        _funcs.AppendLine($"  {dstName}  = getelementptr %suru.Field, ptr {cnode}, i32 0, i32 0");
        _funcs.AppendLine($"  store ptr {srcNameV}, ptr {dstName}");

        // Copy slot [1] tag i32
        var srcTag = NextTmp(); var srcTagV = NextTmp(); var dstTag = NextTmp();
        _funcs.AppendLine($"  {srcTag}  = getelementptr %suru.Field, ptr {csrcV}, i32 0, i32 1");
        _funcs.AppendLine($"  {srcTagV} = load i32, ptr {srcTag}");
        _funcs.AppendLine($"  {dstTag}  = getelementptr %suru.Field, ptr {cnode}, i32 0, i32 1");
        _funcs.AppendLine($"  store i32 {srcTagV}, ptr {dstTag}");

        // Copy slot [2] val i64
        var srcVal = NextTmp(); var srcValV = NextTmp(); var dstVal = NextTmp();
        _funcs.AppendLine($"  {srcVal}  = getelementptr %suru.Field, ptr {csrcV}, i32 0, i32 2");
        _funcs.AppendLine($"  {srcValV} = load i64, ptr {srcVal}");
        _funcs.AppendLine($"  {dstVal}  = getelementptr %suru.Field, ptr {cnode}, i32 0, i32 2");
        _funcs.AppendLine($"  store i64 {srcValV}, ptr {dstVal}");

        // Slot [3] next = null initially
        var dstNext = NextTmp();
        _funcs.AppendLine($"  {dstNext} = getelementptr %suru.Field, ptr {cnode}, i32 0, i32 3");
        _funcs.AppendLine($"  store ptr null, ptr {dstNext}");

        // First node? → set chead; otherwise → wire previous node's next
        var cprevV  = NextTmp();
        var cfirst  = NextTmp();
        _funcs.AppendLine($"  {cprevV}  = load ptr, ptr {cprev}");
        _funcs.AppendLine($"  {cfirst}  = icmp eq ptr {cprevV}, null");
        _funcs.AppendLine($"  br i1 {cfirst}, label %clone_sethead_{n}, label %clone_wire_{n}");

        _funcs.AppendLine($"clone_sethead_{n}:");
        _funcs.AppendLine($"  store ptr {cnode}, ptr {chead}");
        _funcs.AppendLine($"  br label %clone_after_{n}");

        _funcs.AppendLine($"clone_wire_{n}:");
        var cprevNext = NextTmp();
        _funcs.AppendLine($"  {cprevNext} = getelementptr %suru.Field, ptr {cprevV}, i32 0, i32 3");
        _funcs.AppendLine($"  store ptr {cnode}, ptr {cprevNext}");
        _funcs.AppendLine($"  br label %clone_after_{n}");

        _funcs.AppendLine($"clone_after_{n}:");
        _funcs.AppendLine($"  store ptr {cnode}, ptr {cprev}");
        var srcNextGep = NextTmp(); var csrcNext = NextTmp();
        _funcs.AppendLine($"  {srcNextGep} = getelementptr %suru.Field, ptr {csrcV}, i32 0, i32 3");
        _funcs.AppendLine($"  {csrcNext}   = load ptr, ptr {srcNextGep}");
        _funcs.AppendLine($"  store ptr {csrcNext}, ptr {csrc}");
        _funcs.AppendLine($"  br label %clone_cond_{n}");

        _funcs.AppendLine($"clone_done_{n}:");
        var cloneResult = NextTmp();
        _funcs.AppendLine($"  {cloneResult} = load ptr, ptr {chead}");
        return (cloneResult, SuruType.Struct);
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    // drop(src) — free all field nodes in the linked list.
    //
    // Loads `next` before calling @free so the node pointer remains valid at
    // the free call site (no use-after-free). Returns ("0", Bool) to allow
    // drop() in expression position (the result is always discarded).
    private (string val, SuruType type) EmitDropStruct(string headPtr)
    {
        _externals.AddFree();
        var n = _structCounter++;

        var dsrc = NextTmp();
        _funcs.AppendLine($"  {dsrc} = alloca ptr");
        _funcs.AppendLine($"  store ptr {headPtr}, ptr {dsrc}");
        _funcs.AppendLine($"  br label %drop_cond_{n}");

        _funcs.AppendLine($"drop_cond_{n}:");
        var dsrcV = NextTmp();
        var dnull = NextTmp();
        _funcs.AppendLine($"  {dsrcV} = load ptr, ptr {dsrc}");
        _funcs.AppendLine($"  {dnull} = icmp eq ptr {dsrcV}, null");
        _funcs.AppendLine($"  br i1 {dnull}, label %drop_done_{n}, label %drop_body_{n}");

        _funcs.AppendLine($"drop_body_{n}:");
        var dnextGep = NextTmp();
        var dnext    = NextTmp();
        _funcs.AppendLine($"  {dnextGep} = getelementptr %suru.Field, ptr {dsrcV}, i32 0, i32 3");
        _funcs.AppendLine($"  {dnext}    = load ptr, ptr {dnextGep}");
        _funcs.AppendLine($"  store ptr {dnext}, ptr {dsrc}");
        _funcs.AppendLine($"  call void @free(ptr {dsrcV})");
        _funcs.AppendLine($"  br label %drop_cond_{n}");

        _funcs.AppendLine($"drop_done_{n}:");
        return ("0", SuruType.Bool);
    }
}
