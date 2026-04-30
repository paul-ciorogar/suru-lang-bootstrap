// Covered fixtures: structs
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Struct IR emission for IRCodeGenerator.
//
// Every Suru Struct value is a `ptr` to the head of a heap-allocated singly-linked list
// of %suru.Field nodes:
//
//   %suru.Field = type { i64, ptr, i32, i64, ptr }
//                        [0]  [1]  [2]  [3]  [4]
//                       ttag name ftag  val  next
//
//   [0] type_tag  — always 4 (TYPE_STRUCT); identifies this heap ptr as a Struct
//   [1] name      — ptr to a null-terminated field name string (interned via _stringLiterals)
//   [2] field_tag — i32 type tag using unified enum: 0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String
//   [3] val       — i64 storage; always ptrtoint(ptr %fieldVal to i64) since all values are ptr
//   [4] next      — ptr to next field node, null for the tail
//
// Node size: 40 bytes on 64-bit.
//
// ── Runtime module ────────────────────────────────────────────────────────────
//
// All non-trivial struct operations (suru_find_field, suru_struct_clone, suru_struct_drop)
// are implemented in suru_struct.ll and linked as a separate compilation unit. The user's
// .ll only emits `declare` stubs via _runtimeDecls.
//
// ── What stays inline ─────────────────────────────────────────────────────────
//
//   EmitFieldNamePtr    — interns field name strings as module-local constants.
//   EmitStructLiteral   — constructs field nodes inline; allocates per-field malloc+GEP+store.
//   EmitFieldAccess     — field read: EmitFindFieldCall + GEP + load + inttoptr.
//   EmitFieldAssignment — field write: EmitFindFieldCall + ptrtoint + GEP + store.
partial class IRCodeGenerator
{
    // ─── Field name interning ────────────────────────────────────────────────

    private string EmitFieldNamePtr(string fieldName)
    {
        if (!_stringLiterals.TryGetValue(fieldName, out var entry))
        {
            var globalName = $"@.str_{_strCount++}";
            var byteLen    = fieldName.Length + 1;
            entry = (globalName, byteLen);
            _stringLiterals[fieldName] = entry;
        }
        return entry.name;
    }

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
    // typeName is the declared Suru type name (e.g. "Point", "Struct", or null for inline/anonymous).
    // When typeName matches a type declaration in _module.TypeDeclarations, field types are looked
    // up by name from the declaration. Otherwise, field types are inferred from EmitValue's returned
    // SuruType — every value is a tagged pointer so the field_tag is always accurate.
    //
    // Nodes are built in reverse field order so the head of the list is the first
    // declared field. For each field: malloc(40), store type_tag=4/name/field_tag/val/next.
    // Field values are all ptr (Box for scalars, direct ptr for heap types); stored as
    // ptrtoint(ptr %fieldVal to i64) in the i64 val slot.
    private (string val, SuruType type) EmitStructLiteral(StructLiteralExpression lit, string? typeName)
    {
        _externals.AddMalloc();

        // Try to resolve field types from the named type declaration.
        _module.TypeDeclarations.TryGetValue(typeName ?? "", out var typeDecl);

        var prevNodePtr = "null";

        for (int i = lit.Fields.Count - 1; i >= 0; i--)
        {
            var (fieldName, fieldExpr) = lit.Fields[i];

            // Determine field type: from declaration (named type) or infer from EmitValue.
            SuruType fieldType = SuruType.Struct; // fallback for FieldAccessExpression.ResolvedType
            if (typeDecl != null)
            {
                var declField = typeDecl.Fields.FirstOrDefault(f => f.Field == fieldName);
                if (declField.Type != null)
                    fieldType = SuruTypeFromAnnotation(declField.Type);
            }

            if (fieldExpr is FieldAccessExpression { ResolvedType: null } faField)
                faField.ResolvedType = fieldType;
            var (fieldVal, inferredType) = EmitValue(fieldExpr);

            // When no type declaration is available, use the inferred type from the value.
            if (typeDecl == null)
                fieldType = inferredType;

            var nodePtr = NextTmp();
            _funcs.AppendLine($"  {nodePtr} = call ptr @malloc(i64 40)");

            // [0] type_tag — always 4 (TYPE_STRUCT)
            var typeTagGep = NextTmp();
            _funcs.AppendLine($"  {typeTagGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 0");
            _funcs.AppendLine($"  store i64 4, ptr {typeTagGep}");

            // [1] name ptr
            var nameGep = NextTmp();
            var namePtr = EmitFieldNamePtr(fieldName);
            _funcs.AppendLine($"  {nameGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 1");
            _funcs.AppendLine($"  store ptr {namePtr}, ptr {nameGep}");

            // [2] field_tag — unified type enum ordinal
            var tagGep = NextTmp();
            _funcs.AppendLine($"  {tagGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 2");
            _funcs.AppendLine($"  store i32 {(int)fieldType}, ptr {tagGep}");

            // [3] value as i64 via ptrtoint (all values are ptr in the tagged-pointer system)
            var valGep = NextTmp();
            var as64   = EmitToI64(fieldVal, fieldType);
            _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 3");
            _funcs.AppendLine($"  store i64 {as64}, ptr {valGep}");

            // [4] next
            var nextGep = NextTmp();
            _funcs.AppendLine($"  {nextGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 4");
            _funcs.AppendLine($"  store ptr {prevNodePtr}, ptr {nextGep}");

            prevNodePtr = nodePtr;
        }

        return (prevNodePtr, SuruType.Struct);
    }

    // ─── Field access / assignment ───────────────────────────────────────────

    // .field — load a field value from a struct.
    //
    // Calls suru_find_field to locate the node, loads the raw i64 from slot [3],
    // then applies inttoptr to get back the original ptr. The ptr is a Box for scalars
    // or a direct heap ptr for String/Array/Struct. fa.ResolvedType is returned as the
    // compile-time type so callers can unbox correctly (e.g. UnboxInt64 for Int64 fields).
    private (string val, SuruType type) EmitFieldAccess(FieldAccessExpression fa)
    {
        var (headPtr, _) = EmitValue(fa.Receiver);
        var node         = EmitFindFieldCall(headPtr, fa.FieldName);

        var valGep = NextTmp();
        var raw    = NextTmp();
        _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {node}, i32 0, i32 3");
        _funcs.AppendLine($"  {raw}    = load i64, ptr {valGep}");

        var fieldType = fa.ResolvedType ?? SuruType.Struct;
        var result    = EmitFromI64(raw, fieldType);
        return (result, fieldType);
    }

    // receiver.field: value — update a field's stored i64 in-place.
    //
    // Locates the node via suru_find_field, converts the new ptr value via ptrtoint,
    // and stores it into slot [3].
    private void EmitFieldAssignment(FieldAssignmentStatement fa)
    {
        var (headPtr, _)      = EmitValue(fa.Receiver);
        var (newVal, newType) = EmitValue(fa.Value);
        var node = EmitFindFieldCall(headPtr, fa.FieldName);

        var as64   = EmitToI64(newVal, newType);
        var valGep = NextTmp();
        _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {node}, i32 0, i32 3");
        _funcs.AppendLine($"  store i64 {as64}, ptr {valGep}");
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    internal (string val, SuruType type) EmitCloneStructDispatch(Expression arg)
    {
        var (headPtr, _) = EmitValue(arg);
        return (EmitCloneStruct(headPtr), SuruType.Struct);
    }

    internal string EmitCloneStruct(string headPtr)
    {
        _runtimeDecls.AddStructClone();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_struct_clone(ptr {headPtr})");
        return tmp;
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    internal (string val, SuruType type) EmitDropStructDispatch(Expression arg)
    {
        var (headPtr, _) = EmitValue(arg);
        EmitDropStruct(headPtr);
        return ("null", SuruType.Bool);
    }

    internal void EmitDropStruct(string headPtr)
    {
        _runtimeDecls.AddStructDrop();
        _funcs.AppendLine($"  call void @suru_struct_drop(ptr {headPtr})");
    }
}
