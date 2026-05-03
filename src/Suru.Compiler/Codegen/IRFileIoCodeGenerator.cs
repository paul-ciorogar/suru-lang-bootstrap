using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Array.at for argv ───────────────────────────────────────────────────

    // args.at(i) — extract the i-th element from the argv Seq built by @main.
    // Index is raw i64 for scalar expressions, or a box ptr for dynamic values.
    private (string val, SuruType type) EmitArgAt(string seqVal, Expression idxExpr)
    {
        var data                = EmitExtractStringData(seqVal);   // char**
        var (idxVal, idxType)   = EmitValue(idxExpr);
        var idx                 = IsScalar(idxType) ? idxVal : UnboxInt64(idxVal);

        var slotPtr = NextTmp();
        var cstrPtr = NextTmp();
        _funcs.AppendLine($"  {slotPtr} = getelementptr ptr, ptr {data}, i64 {idx}");
        _funcs.AppendLine($"  {cstrPtr} = load ptr, ptr {slotPtr}");

        _externals.AddStrlen();
        var len = NextTmp();
        _funcs.AppendLine($"  {len} = call i64 @strlen(ptr {cstrPtr})");

        return EmitCreateStringSeq(cstrPtr, len);
    }

    // ─── File I/O built-ins ──────────────────────────────────────────────────

    private (string val, SuruType type) EmitReadFile(Expression pathArg)
    {
        var (pathSeq, _) = EmitValue(pathArg);
        var pathData = EmitExtractStringData(pathSeq);

        _externals.AddFopen();
        _boolStringGlobals.AddModeR();
        var file = NextTmp();
        _funcs.AppendLine($"  {file} = call ptr @fopen(ptr {pathData}, ptr @.mode_r)");

        _externals.AddFseek();
        _funcs.AppendLine($"  call i32 @fseek(ptr {file}, i64 0, i32 2)");

        _externals.AddFtell();
        var size = NextTmp();
        _funcs.AppendLine($"  {size} = call i64 @ftell(ptr {file})");

        _externals.AddRewind();
        _funcs.AppendLine($"  call void @rewind(ptr {file})");

        var bufSize = NextTmp();
        var buf     = NextTmp();
        _funcs.AppendLine($"  {bufSize} = add i64 {size}, 1");
        _funcs.AppendLine($"  {buf} = call ptr @malloc(i64 {bufSize})");

        _externals.AddFread();
        _funcs.AppendLine($"  call i64 @fread(ptr {buf}, i64 1, i64 {size}, ptr {file})");

        var nullSlot = NextTmp();
        _funcs.AppendLine($"  {nullSlot} = getelementptr i8, ptr {buf}, i64 {size}");
        _funcs.AppendLine($"  store i8 0, ptr {nullSlot}");

        _externals.AddFclose();
        _funcs.AppendLine($"  call i32 @fclose(ptr {file})");

        return EmitCreateStringSeq(buf, size);
    }

    private void EmitWriteFile(Expression pathArg, Expression contentArg)
    {
        var (pathSeq, _)    = EmitValue(pathArg);
        var pathData        = EmitExtractStringData(pathSeq);
        var (contentSeq, _) = EmitValue(contentArg);
        var contentLen      = EmitExtractStringLen(contentSeq);
        var contentData     = EmitExtractStringData(contentSeq);

        _externals.AddFopen();
        _boolStringGlobals.AddModeW();
        var file = NextTmp();
        _funcs.AppendLine($"  {file} = call ptr @fopen(ptr {pathData}, ptr @.mode_w)");

        _externals.AddFwrite();
        _funcs.AppendLine($"  call i64 @fwrite(ptr {contentData}, i64 1, i64 {contentLen}, ptr {file})");

        _externals.AddFclose();
        _funcs.AppendLine($"  call i32 @fclose(ptr {file})");
    }
}
