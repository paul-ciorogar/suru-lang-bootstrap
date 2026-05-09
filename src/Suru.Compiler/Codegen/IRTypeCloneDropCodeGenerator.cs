// Covered by: all fixtures that use structs, clone, drop, or Array<NamedType>
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Emits per-type clone and drop functions for every TypeDeclaration in the module.
//
// These functions are stored in the struct vtable (clone_fn at offset 16, drop_fn at offset 24)
// so suru_clone_dyn / suru_drop_dyn can dispatch correctly without knowing the concrete type name.
//
// Layout assumed (see IRStructCodeGenerator for details):
//   offset  0: i64 type_tag
//   offset  8: i64 variant_idx
//   offset 16: ptr clone_fn
//   offset 24: ptr drop_fn
//   offset 32+i*8: i64 field[i]
//
// Field slot encoding: scalars as raw i64, heap values as ptrtoint(ptr).
// Clone: scalar fields are copied directly; heap fields go through @suru_clone_dyn.
// Drop:  scalar fields are ignored; heap fields go through @suru_drop_dyn; then free the struct.
partial class IRCodeGenerator
{
    // Emit @suru_clone_{Name} and @suru_drop_{Name} for every TypeDeclaration.
    // Written into _helpers so they appear before any user function bodies in the .ll output.
    // Must be called after _externals.AddMalloc() is ensured (already done by Emit() preamble).
    //
    // Only locally-declared types get their clone/drop emitted here. Imported types are compiled
    // in their origin module and their clone/drop functions are linked from the origin .o — emitting
    // them again would produce duplicate symbol linker errors.
    //
    // Detection: a type is external if any included source path has it in ExternalDeclarationRegistry.
    private void EmitTypeCloneDrop()
    {
        foreach (var (typeName, typeDecl) in _module.TypeDeclarations)
        {
            var isExternal = _module.IncludedSourcePaths.Any(
                path => _module.ExternalDeclarationRegistry.Contains(path, typeName));
            if (!isExternal)
                EmitOneTypeCloneDrop(typeName, typeDecl);
        }
    }

    private void EmitOneTypeCloneDrop(string typeName, TypeDeclaration typeDecl)
    {
        var fields = typeDecl.Fields;
        var fieldCount = fields.Count;
        var size = StructSize(fieldCount);

        // Determine which fields are heap types — those need recursive clone/drop.
        var fieldTypes = fields
            .Select(f => SuruTypeFromAnnotation(f.Type))
            .ToList();
        var hasHeapField = fieldTypes.Any(t => !IsScalar(t));

        if (hasHeapField)
        {
            _runtimeDecls.AddCloneDyn();
            _runtimeDecls.AddDropDyn();
        }

        // ── @suru_clone_{typeName} ────────────────────────────────────────────
        // Allocates a new struct of the same size, copies the header, recursively
        // clones heap fields, copies scalar fields, returns the new ptr.
        _helpers.AppendLine($"define ptr @suru_clone_{typeName}(ptr %s) {{");
        _helpers.AppendLine("entry:");

        var n = CG("malloc");
        _helpers.AppendLine($"  {n} = call ptr @malloc(i64 {size})");

        // Copy header word by word (type_tag and variant_idx as i64, clone_fn and drop_fn as ptr).
        CopyI64Field("s", n, 0);   // type_tag
        CopyI64Field("s", n, 8);   // variant_idx
        CopyPtrField("s", n, 16);  // clone_fn
        CopyPtrField("s", n, 24);  // drop_fn

        // Copy data fields.
        for (int i = 0; i < fieldCount; i++)
        {
            var offset = FieldOffset(i);
            var ft = fieldTypes[i];
            if (IsScalar(ft))
            {
                CopyI64Field("s", n, offset);
            }
            else
            {
                // Load the i64 slot, inttoptr, clone_dyn, ptrtoint, store.
                var raw  = CG("fraw");
                var fp   = CG("fptr");
                var fc   = CG("fcloned");
                var fi   = CG("fi64");
                var sgep = CG("fsgep");
                var dgep = CG("fdgep");
                _helpers.AppendLine($"  {sgep} = getelementptr i8, ptr %s, i64 {offset}");
                _helpers.AppendLine($"  {raw}  = load i64, ptr {sgep}");
                _helpers.AppendLine($"  {fp}   = inttoptr i64 {raw} to ptr");
                _helpers.AppendLine($"  {fc}   = call ptr @suru_clone_dyn(ptr {fp})");
                _helpers.AppendLine($"  {fi}   = ptrtoint ptr {fc} to i64");
                _helpers.AppendLine($"  {dgep} = getelementptr i8, ptr {n}, i64 {offset}");
                _helpers.AppendLine($"  store i64 {fi}, ptr {dgep}");
            }
        }

        _helpers.AppendLine($"  ret ptr {n}");
        _helpers.AppendLine("}");
        _helpers.AppendLine();

        // ── @suru_drop_{typeName} ─────────────────────────────────────────────
        // Recursively drops heap fields, then frees the allocation.
        _helpers.AppendLine($"define void @suru_drop_{typeName}(ptr %s) {{");
        _helpers.AppendLine("entry:");

        for (int i = 0; i < fieldCount; i++)
        {
            var ft = fieldTypes[i];
            if (!IsScalar(ft))
            {
                var offset = FieldOffset(i);
                var raw  = CG("fraw");
                var fp   = CG("fptr");
                var fgep = CG("fgep");
                _helpers.AppendLine($"  {fgep} = getelementptr i8, ptr %s, i64 {offset}");
                _helpers.AppendLine($"  {raw}  = load i64, ptr {fgep}");
                _helpers.AppendLine($"  {fp}   = inttoptr i64 {raw} to ptr");
                _helpers.AppendLine($"  call void @suru_drop_dyn(ptr {fp})");
            }
        }

        _externals.AddFree();
        _helpers.AppendLine("  call void @free(ptr %s)");
        _helpers.AppendLine("  ret void");
        _helpers.AppendLine("}");
        _helpers.AppendLine();
    }

    // ── Helper: copy a single i64 word from src to dst at the given byte offset ──
    private void CopyI64Field(string src, string dst, long byteOffset)
    {
        var sgep = CG("cgep");
        var v    = CG("cv");
        var dgep = CG("dgep");
        _helpers.AppendLine($"  {sgep} = getelementptr i8, ptr %{src}, i64 {byteOffset}");
        _helpers.AppendLine($"  {v}    = load i64, ptr {sgep}");
        _helpers.AppendLine($"  {dgep} = getelementptr i8, ptr {dst}, i64 {byteOffset}");
        _helpers.AppendLine($"  store i64 {v}, ptr {dgep}");
    }

    // ── Helper: copy a ptr word from src to dst at the given byte offset ─────────
    private void CopyPtrField(string src, string dst, long byteOffset)
    {
        var sgep = CG("pgep");
        var v    = CG("pv");
        var dgep = CG("dpgep");
        _helpers.AppendLine($"  {sgep} = getelementptr i8, ptr %{src}, i64 {byteOffset}");
        _helpers.AppendLine($"  {v}    = load ptr, ptr {sgep}");
        _helpers.AppendLine($"  {dgep} = getelementptr i8, ptr {dst}, i64 {byteOffset}");
        _helpers.AppendLine($"  store ptr {v}, ptr {dgep}");
    }

    // Per-type codegen uses its own counter to avoid collisions with _tmp.
    private int _cgTmp;
    private string CG(string prefix) => $"%{prefix}_{_cgTmp++}";
}
