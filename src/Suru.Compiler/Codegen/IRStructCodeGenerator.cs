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
//   [0] type_tag  — always 4 (TYPE_STRUCT)
//   [1] name      — ptr to a null-terminated field name string
//   [2] field_tag — i32 type tag (SuruType.TypeTag ordinal)
//   [3] val       — i64 storage; always ptrtoint(ptr %fieldVal to i64)
//   [4] next      — ptr to next field node, null for the tail
partial class IRCodeGenerator
{
    // ─── Field name interning ────────────────────────────────────────────────

    private string EmitFieldNamePtr(string fieldName)
    {
        if (!_fieldNames.TryGetValue(fieldName, out var entry))
        {
            var globalName = $"@.field_{_fieldCount++}";
            var byteLen    = fieldName.Length + 1;
            entry = (globalName, byteLen);
            _fieldNames[fieldName] = entry;
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

    // typeName is the declared Suru type name (e.g. "Point") or null for anonymous literals.
    // Field types are looked up from the declaration when typeName is known;
    // otherwise inferred from EmitValue's returned SuruType.
    private (string val, SuruType type) EmitStructLiteral(StructLiteralExpression lit, string? typeName)
    {
        _externals.AddMalloc();

        _module.TypeDeclarations.TryGetValue(typeName ?? "", out var typeDecl);

        var prevNodePtr = "null";

        for (int i = lit.Fields.Count - 1; i >= 0; i--)
        {
            var (fieldName, fieldExpr) = lit.Fields[i];

            SuruType fieldType = new SuruType.NamedType("");
            if (typeDecl != null)
            {
                var declField = typeDecl.Fields.FirstOrDefault(f => f.Field == fieldName);
                if (declField.Type != null)
                    fieldType = SuruTypeFromAnnotation(declField.Type);
            }

            if (fieldExpr is FieldAccessExpression { ResolvedType: null } faField)
                faField.ResolvedType = fieldType;
            // For `{ field: [] }` where the declared field is Array<T>, propagate the element
            // type so EmitArrayLiteral can emit the correct elem_tag.
            if (fieldExpr is ArrayLiteralExpression { Elements.Count: 0 } emptyArr && fieldType is SuruType.ArrayType)
                emptyArr.ResolvedType = fieldType;
            var (fieldVal, inferredType) = EmitValue(fieldExpr);

            if (typeDecl == null)
                fieldType = inferredType;

            var nodePtr = NextTmp();
            _funcs.AppendLine($"  {nodePtr} = call ptr @malloc(i64 40)");

            var typeTagGep = NextTmp();
            _funcs.AppendLine($"  {typeTagGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 0");
            _funcs.AppendLine($"  store i64 {SuruType.NamedType.Tag}, ptr {typeTagGep}");

            var nameGep = NextTmp();
            var namePtr = EmitFieldNamePtr(fieldName);
            _funcs.AppendLine($"  {nameGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 1");
            _funcs.AppendLine($"  store ptr {namePtr}, ptr {nameGep}");

            var tagGep = NextTmp();
            _funcs.AppendLine($"  {tagGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 2");
            _funcs.AppendLine($"  store i32 {fieldType.TypeTag}, ptr {tagGep}");

            var valGep = NextTmp();
            var as64   = EmitToI64(fieldVal, fieldType);
            _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 3");
            _funcs.AppendLine($"  store i64 {as64}, ptr {valGep}");

            var nextGep = NextTmp();
            _funcs.AppendLine($"  {nextGep} = getelementptr %suru.Field, ptr {nodePtr}, i32 0, i32 4");
            _funcs.AppendLine($"  store ptr {prevNodePtr}, ptr {nextGep}");

            prevNodePtr = nodePtr;
        }

        var resultType = typeName is not null
            ? (SuruType)new SuruType.NamedType(typeName)
            : new SuruType.NamedType("");
        return (prevNodePtr, resultType);
    }

    // ─── Field access / assignment ───────────────────────────────────────────

    private (string val, SuruType type) EmitFieldAccess(FieldAccessExpression fa)
    {
        var (headPtr, receiverType) = EmitValue(fa.Receiver);

        // Variant: unwrap to inner struct ptr before field lookup.
        if (receiverType is SuruType.NamedType nt && IsVariant(nt.Name))
        {
            _runtimeDecls.AddVariantInner();
            var innerTmp = NextTmp();
            _funcs.AppendLine($"  {innerTmp} = call ptr @suru_variant_inner(ptr {headPtr})");
            headPtr = innerTmp;
        }

        var node = EmitFindFieldCall(headPtr, fa.FieldName);

        var valGep = NextTmp();
        var raw    = NextTmp();
        _funcs.AppendLine($"  {valGep} = getelementptr %suru.Field, ptr {node}, i32 0, i32 3");
        _funcs.AppendLine($"  {raw}    = load i64, ptr {valGep}");

        var fieldType = fa.ResolvedType ?? new SuruType.NamedType("");
        var boxPtr    = EmitFromI64(raw, fieldType);
        if (IsScalar(fieldType))
        {
            var rawScalar = UnboxScalar(boxPtr, fieldType);
            return (rawScalar, fieldType);
        }
        return (boxPtr, fieldType);
    }

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

    // ─── Clone / Drop ────────────────────────────────────────────────────────

    internal (string val, SuruType type) EmitCloneStructDispatch(Expression arg)
    {
        var (headPtr, headType) = EmitValue(arg);
        return (EmitCloneStruct(headPtr), headType);
    }

    internal string EmitCloneStruct(string headPtr)
    {
        _runtimeDecls.AddStructClone();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_struct_clone(ptr {headPtr})");
        return tmp;
    }

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
