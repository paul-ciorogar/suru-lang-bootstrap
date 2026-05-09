// Covered fixtures: structs
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Struct IR emission for IRCodeGenerator.
//
// Phase 2: every Suru Struct is a ptr to a flat fixed-layout allocation:
//
//   Header (32 bytes):
//     offset  0: i64 type_tag    (4=struct, 7=variant)
//     offset  8: i64 variant_idx (0 for non-variants)
//     offset 16: ptr clone_fn    (per-type function ptr, sig: ptr(ptr))
//     offset 24: ptr drop_fn     (per-type function ptr, sig: void(ptr))
//
//   Fields (8 bytes each, starting at offset 32):
//     offset 32 + i*8: i64 field[i]  (TypeDeclaration.Fields order)
//
//   Field slot encoding:
//     Scalars (Bool/Int32/Int64/Float64): raw value widened to i64 — no boxing.
//     Heap (String/Array/NamedType/SumType): ptrtoint(ptr) to i64.
//
//   Size: (4 + field_count) * 8 bytes.  1 malloc per struct.
//
// clone_fn / drop_fn are per-TypeDeclaration LLVM functions emitted into the user
// module (by IRTypeCloneDropCodeGenerator) before any user function bodies.
// suru_clone_dyn / suru_drop_dyn read the vtable at offsets 16/24 for tag=4 and tag=7.
partial class IRCodeGenerator
{
    // Struct size in bytes for a type with the given field count.
    internal static int StructSize(int fieldCount) => (4 + fieldCount) * 8;

    // Byte offset of field[i] in the flat struct layout.
    internal static long FieldOffset(int fieldIndex) => 32L + fieldIndex * 8L;

    // Return the 0-based index of fieldName in typeDecl.Fields, or -1 if not found.
    // IReadOnlyList<T> doesn't expose FindIndex, so we scan with Enumerable.Range.
    private static int FieldIndex(TypeDeclaration typeDecl, string fieldName)
    {
        var fields = typeDecl.Fields;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Field == fieldName) return i;
        return -1;
    }

    // ─── Scalar ↔ i64 for struct field slots ────────────────────────────────

    // Convert a scalar or heap value to the i64 that is stored in a field slot.
    // Scalars are widened/bitcast directly — no Box allocation.
    // Heap values are ptrtoint'd.
    private string StructFieldToI64(string val, SuruType type)
    {
        switch (type)
        {
            case SuruType.BoolType:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = zext i1 {val} to i64");
                return t;
            }
            case SuruType.Int32Type:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = sext i32 {val} to i64");
                return t;
            }
            case SuruType.Int64Type:
                return val;   // already i64
            case SuruType.Float64Type:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = bitcast double {val} to i64");
                return t;
            }
            default:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = ptrtoint ptr {val} to i64");
                return t;
            }
        }
    }

    // Convert an i64 field slot value back to its native type.
    // Scalars are truncated/bitcast directly — no unbox call.
    // Heap values are inttoptr'd.
    private string StructFieldFromI64(string raw, SuruType type)
    {
        switch (type)
        {
            case SuruType.BoolType:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = trunc i64 {raw} to i1");
                return t;
            }
            case SuruType.Int32Type:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = trunc i64 {raw} to i32");
                return t;
            }
            case SuruType.Int64Type:
                return raw;   // already i64
            case SuruType.Float64Type:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = bitcast i64 {raw} to double");
                return t;
            }
            default:
            {
                var t = NextTmp();
                _funcs.AppendLine($"  {t} = inttoptr i64 {raw} to ptr");
                return t;
            }
        }
    }

    // ─── Struct literal ──────────────────────────────────────────────────────

    // Emit a flat struct allocation for the given literal.
    // typeName is the declared Suru type name (e.g. "Point") — must not be null or empty;
    // struct literals must always appear in a typed let/return/match-arm context.
    private (string val, SuruType type) EmitStructLiteral(StructLiteralExpression lit, string? typeName)
    {
        if (typeName is not { Length: > 0 })
            throw new NotSupportedException(
                "IR codegen: struct literal requires a declared type name — " +
                "use 'let name TypeName: { ... }' or a typed return");

        _externals.AddMalloc();

        if (!_module.TypeDeclarations.TryGetValue(typeName, out var typeDecl))
            throw new InvalidOperationException(
                $"IR codegen: struct literal refers to unknown type '{typeName}'");

        var fieldCount = typeDecl.Fields.Count;
        var size = StructSize(fieldCount);

        var mem = NextTmp();
        _funcs.AppendLine($"  {mem} = call ptr @malloc(i64 {size})");

        // Header: type_tag=4 (overridden to 7 for variants after this call)
        var tagGep = NextTmp();
        _funcs.AppendLine($"  {tagGep} = getelementptr i8, ptr {mem}, i64 0");
        _funcs.AppendLine($"  store i64 4, ptr {tagGep}");

        var vidxGep = NextTmp();
        _funcs.AppendLine($"  {vidxGep} = getelementptr i8, ptr {mem}, i64 8");
        _funcs.AppendLine($"  store i64 0, ptr {vidxGep}");

        // Vtable: per-type clone_fn and drop_fn (generated by IRTypeCloneDropCodeGenerator).
        // If the type is external (defined in an included module), register a declare stub
        // so the LLVM assembler can resolve the forward reference; the linker supplies the body.
        var isExternal = _module.IncludedSourcePaths.Any(
            p => _module.ExternalDeclarationRegistry.Contains(p, typeName));
        if (isExternal)
            _externalTypeCloneDrop.Add(typeName);

        var cfnGep = NextTmp();
        _funcs.AppendLine($"  {cfnGep} = getelementptr i8, ptr {mem}, i64 16");
        _funcs.AppendLine($"  store ptr @suru_clone_{typeName}, ptr {cfnGep}");

        var dfnGep = NextTmp();
        _funcs.AppendLine($"  {dfnGep} = getelementptr i8, ptr {mem}, i64 24");
        _funcs.AppendLine($"  store ptr @suru_drop_{typeName}, ptr {dfnGep}");

        // Fields — store in TypeDeclaration order (canonical GEP offset order).
        // Fields absent from the literal are zero-initialized: i64 0.
        // This is essential for partial/empty struct literals (e.g. {} used as a "cleared"
        // placeholder before drop) — uninitialized heap-field slots would cause
        // suru_drop_dyn to dereference garbage when the struct is later dropped.
        var litByName = lit.Fields.ToDictionary(f => f.Name, f => f.Value);
        for (int i = 0; i < typeDecl.Fields.Count; i++)
        {
            var declField = typeDecl.Fields[i];
            var fieldType = SuruTypeFromAnnotation(declField.Type);
            var fGep = NextTmp();
            _funcs.AppendLine($"  {fGep} = getelementptr i8, ptr {mem}, i64 {FieldOffset(i)}");

            if (!litByName.TryGetValue(declField.Field, out var fieldExpr))
            {
                // Missing field — zero-initialize so heap-typed slots hold null (not garbage).
                _funcs.AppendLine($"  store i64 0, ptr {fGep}");
                continue;
            }

            if (fieldExpr is FieldAccessExpression { ResolvedType: null } faField)
                faField.ResolvedType = fieldType;
            if (fieldExpr is ArrayLiteralExpression { Elements.Count: 0 } emptyArr &&
                fieldType is SuruType.ArrayType)
                emptyArr.ResolvedType = fieldType;

            (string fieldVal, SuruType fieldValType) = fieldExpr is StructLiteralExpression slNested
                && fieldType is SuruType.NamedType ntNested
                ? EmitStructLiteral(slNested, ntNested.Name)
                : EmitValue(fieldExpr);
            // When the declared field slot is a heap type but the expression is a scalar,
            // box the scalar to a heap ptr so ptrtoint produces a valid i64 address.
            var storedVal = IsScalar(fieldValType) && !IsScalar(fieldType)
                ? BoxValue(fieldVal, fieldValType)
                : fieldVal;
            var as64 = StructFieldToI64(storedVal, fieldType);

            _funcs.AppendLine($"  store i64 {as64}, ptr {fGep}");
        }

        return (mem, new SuruType.NamedType(typeName));
    }

    // ─── Field access / assignment ───────────────────────────────────────────

    private (string val, SuruType type) EmitFieldAccess(FieldAccessExpression fa)
    {
        var (headPtr, receiverType) = EmitValue(fa.Receiver);
        var typeName = (receiverType as SuruType.NamedType)?.Name ?? "";

        // Variant receivers: the struct type IS the variant type (NamedType("Circle")),
        // so TypeDeclarations["Circle"] gives the field list directly.
        if (!_module.TypeDeclarations.TryGetValue(typeName, out var typeDecl))
            throw new InvalidOperationException(
                $"IR codegen: cannot resolve struct type '{typeName}' for field access '{fa.FieldName}'");

        var fieldIdx = FieldIndex(typeDecl, fa.FieldName);
        if (fieldIdx < 0)
            throw new InvalidOperationException(
                $"IR codegen: field '{fa.FieldName}' not found in type '{typeName}'");

        var fGep = NextTmp();
        var raw  = NextTmp();
        _funcs.AppendLine($"  {fGep} = getelementptr i8, ptr {headPtr}, i64 {FieldOffset(fieldIdx)}");
        _funcs.AppendLine($"  {raw}  = load i64, ptr {fGep}");

        var fieldType = fa.ResolvedType ?? new SuruType.NamedType("");
        return (StructFieldFromI64(raw, fieldType), fieldType);
    }

    private void EmitFieldAssignment(FieldAssignmentStatement fa)
    {
        var (headPtr, receiverType) = EmitValue(fa.Receiver);
        var typeName = (receiverType as SuruType.NamedType)?.Name ?? "";

        if (!_module.TypeDeclarations.TryGetValue(typeName, out var typeDecl))
            throw new InvalidOperationException(
                $"IR codegen: cannot resolve struct type '{typeName}' for field assignment '{fa.FieldName}'");

        var fieldIdx = FieldIndex(typeDecl, fa.FieldName);
        if (fieldIdx < 0)
            throw new InvalidOperationException(
                $"IR codegen: field '{fa.FieldName}' not found in type '{typeName}'");

        var fieldType = SuruTypeFromAnnotation(typeDecl.Fields[fieldIdx].Type);
        (string newVal, SuruType newType) = fa.Value is StructLiteralExpression slfa
            ? fieldType is SuruType.NamedType ntfa
                ? EmitStructLiteral(slfa, ntfa.Name)
                : fieldType is SuruType.ArrayType atfa
                    // {} used to "clear" an array slot before drop — emit empty array with known elem type.
                    ? EmitArrayLiteral(new ArrayLiteralExpression(Array.Empty<Expression>())
                        { ResolvedType = atfa })
                    : slfa.Fields.Count == 0 && fieldType is SuruType.SumType
                        // {} used to "clear" a sum-type slot — store null ptr; suru_drop_dyn has null guard.
                        ? ("0", SuruType.Int64)
                        : EmitValue(fa.Value)
            : EmitValue(fa.Value);
        var as64 = StructFieldToI64(newVal, newType);

        var fGep = NextTmp();
        _funcs.AppendLine($"  {fGep} = getelementptr i8, ptr {headPtr}, i64 {FieldOffset(fieldIdx)}");
        _funcs.AppendLine($"  store i64 {as64}, ptr {fGep}");
    }

    // ─── Clone / Drop ────────────────────────────────────────────────────────
    // Note: explicit clone(x)/drop(x) go through EmitCloneDyn/EmitDropDyn →
    // suru_clone_dyn/suru_drop_dyn, which dispatch via the vtable at offsets 16/24.
    // EmitCloneStructDispatch and EmitDropStructDispatch are retained for API compatibility
    // but not called by any current code path.

    internal (string val, SuruType type) EmitCloneStructDispatch(Expression arg)
    {
        var (val, type) = EmitValue(arg);
        _runtimeDecls.AddCloneDyn();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_clone_dyn(ptr {val})");
        return (tmp, type);
    }

    internal (string val, SuruType type) EmitDropStructDispatch(Expression arg)
    {
        var (val, _) = EmitValue(arg);
        _runtimeDecls.AddDropDyn();
        _funcs.AppendLine($"  call void @suru_drop_dyn(ptr {val})");
        return ("null", SuruType.Bool);
    }
}
