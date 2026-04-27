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
// ── Runtime module ────────────────────────────────────────────────────────────
//
// All non-trivial struct operations (suru_find_field, suru_struct_clone, suru_struct_drop)
// are implemented in suru_struct.ll and linked as a separate compilation unit. The user's
// .ll only emits `declare` stubs via _runtimeDecls.
//
// ── What stays inline ─────────────────────────────────────────────────────────
//
//   EmitFieldNamePtr  — interns field name strings as module-local constants; cannot
//                       move to the runtime module.
//   EmitStructLiteral — constructs field nodes inline; allocates per-field malloc+GEP+store.
//   EmitFieldAccess   — field read: EmitFindFieldCall + GEP + load + EmitFromI64.
//   EmitFieldAssignment — field write: EmitFindFieldCall + EmitToI64 + GEP + store.
//
// ResolvedType on FieldAccessExpression:
//   The SemanticAnalyzer sets fa.ResolvedType from _structSymbols for fields declared
//   in the same lexical scope. The IR emitter uses this static type to call EmitFromI64
//   directly, avoiding a runtime tag-branch for the common case.
partial class IRCodeGenerator
{
    // ─── Field name interning ────────────────────────────────────────────────

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
        _runtimeDecls.AddFindField();
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

    // Dispatch helper for `clone(x)` in expression position.
    // Evaluates the argument, delegates to @suru_struct_clone, returns Struct.
    internal (string val, SuruType type) EmitCloneStructDispatch(Expression arg)
    {
        var (headPtr, _) = EmitValue(arg);
        return (EmitCloneStruct(headPtr), SuruType.Struct);
    }

    // Emit a call to @suru_struct_clone — deep-copies the field-node linked list.
    internal string EmitCloneStruct(string headPtr)
    {
        _runtimeDecls.AddStructClone();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_struct_clone(ptr {headPtr})");
        return tmp;
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    // Dispatch helper for `drop(x)` in expression position.
    // Returns ("0", Bool) so the call can appear in statement or expression position.
    internal (string val, SuruType type) EmitDropStructDispatch(Expression arg)
    {
        var (headPtr, _) = EmitValue(arg);
        EmitDropStruct(headPtr);
        return ("0", SuruType.Bool);
    }

    // Emit a call to @suru_struct_drop — frees all field nodes in the linked list.
    internal void EmitDropStruct(string headPtr)
    {
        _runtimeDecls.AddStructDrop();
        _funcs.AppendLine($"  call void @suru_struct_drop(ptr {headPtr})");
    }
}
