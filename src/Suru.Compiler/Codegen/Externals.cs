// Covered fixtures: print, exit_test, print-error, negative-literals, arithmetic, comparisons, control-flow, fibonacci, while-loop, strings, include-test, file_io, file_io_write, arrays, structs
using System.Text;

namespace Suru.Compiler.Codegen;

// Tracks C runtime external declarations needed by the emitted module.
// Each Add* method is idempotent — callers invoke it whenever they emit an
// instruction that references the symbol; duplicate declarations are suppressed.
// ToString() returns the declaration block ready for insertion into the .ll file.
internal class Externals
{
    private readonly StringBuilder _sb = new();
    private bool _exitAdded     = false;
    private bool _fprintfAdded  = false;
    private bool _mallocAdded   = false;
    private bool _memcpyAdded   = false;
    private bool _printfAdded   = false;
    private bool _snprintfAdded = false;
    private bool _stderrAdded   = false;
    private bool _strcmpAdded   = false;
    private bool _strtolAdded   = false;
    private bool _reallocAdded  = false;
    private bool _fopenAdded    = false;
    private bool _fcloseAdded   = false;
    private bool _fseekAdded    = false;
    private bool _ftellAdded    = false;
    private bool _rewindAdded   = false;
    private bool _freadAdded    = false;
    private bool _fwriteAdded   = false;
    private bool _strlenAdded   = false;
    private bool _freeAdded     = false;

    public override string ToString() => _sb.ToString();

    internal void AddExit()
    {
        if (_exitAdded) return;
        // noreturn tells LLVM that exit() never returns, enabling better dead-code
        // elimination after the call site.
        _sb.AppendLine("declare void @exit(i32) noreturn");
        _exitAdded = true;
    }

    internal void AddFprintf()
    {
        if (_fprintfAdded) return;
        _sb.AppendLine("declare i32 @fprintf(ptr, ptr, ...)");
        _fprintfAdded = true;
    }

    internal void AddMalloc()
    {
        if (_mallocAdded) return;
        _sb.AppendLine("declare ptr @malloc(i64)");
        _mallocAdded = true;
    }

    internal void AddPrintf()
    {
        if (_printfAdded) return;
        _sb.AppendLine("declare i32 @printf(ptr, ...)");
        _printfAdded = true;
    }

    internal void AddStderr()
    {
        if (_stderrAdded) return;
        _sb.AppendLine("@stderr = external global ptr");
        _stderrAdded = true;
    }

    internal void AddStrcmp()
    {
        if (_strcmpAdded) return;
        _sb.AppendLine("declare i32 @strcmp(ptr, ptr)");
        _strcmpAdded = true;
    }

    internal void AddMemcpy()
    {
        if (_memcpyAdded) return;
        _sb.AppendLine("declare ptr @memcpy(ptr, ptr, i64)");
        _memcpyAdded = true;
    }

    internal void AddStrtol()
    {
        if (_strtolAdded) return;
        // strtol(str, endptr, base) — endptr is passed as null (ptr null) when unused.
        _sb.AppendLine("declare i64 @strtol(ptr, ptr, i32)");
        _strtolAdded = true;
    }

    internal void AddSnprintf()
    {
        if (_snprintfAdded) return;
        // snprintf(buf, size, fmt, ...) — called with buf=null and size=0 to measure length.
        _sb.AppendLine("declare i32 @snprintf(ptr, i64, ptr, ...)");
        _snprintfAdded = true;
    }

    internal void AddRealloc()
    {
        if (_reallocAdded) return;
        // realloc(ptr, newSize) — used by arr.add(v) to grow the data buffer in-place.
        _sb.AppendLine("declare ptr @realloc(ptr, i64)");
        _reallocAdded = true;
    }

    // ─── File I/O (readFile / writeFile built-ins) ───────────────────────────

    internal void AddFopen()
    {
        if (_fopenAdded) return;
        // fopen(path, mode) → file ptr; returns null on failure (not checked at runtime).
        _sb.AppendLine("declare ptr @fopen(ptr, ptr)");
        _fopenAdded = true;
    }

    internal void AddFclose()
    {
        if (_fcloseAdded) return;
        // fclose(file) → i32 status; called after every fread/fwrite to flush and release.
        _sb.AppendLine("declare i32 @fclose(ptr)");
        _fcloseAdded = true;
    }

    internal void AddFseek()
    {
        if (_fseekAdded) return;
        // fseek(file, offset, whence) — used with offset=0, whence=2 (SEEK_END) to size a file.
        _sb.AppendLine("declare i32 @fseek(ptr, i64, i32)");
        _fseekAdded = true;
    }

    internal void AddFtell()
    {
        if (_ftellAdded) return;
        // ftell(file) → byte offset from start; called after fseek(SEEK_END) to get file size.
        _sb.AppendLine("declare i64 @ftell(ptr)");
        _ftellAdded = true;
    }

    internal void AddRewind()
    {
        if (_rewindAdded) return;
        // rewind(file) — resets the file position to the start before fread.
        _sb.AppendLine("declare void @rewind(ptr)");
        _rewindAdded = true;
    }

    internal void AddFread()
    {
        if (_freadAdded) return;
        // fread(buf, size, count, file) → items read; used as fread(buf, 1, fileSize, file).
        _sb.AppendLine("declare i64 @fread(ptr, i64, i64, ptr)");
        _freadAdded = true;
    }

    internal void AddFwrite()
    {
        if (_fwriteAdded) return;
        // fwrite(buf, size, count, file) → items written; used as fwrite(data, 1, len, file).
        _sb.AppendLine("declare i64 @fwrite(ptr, i64, i64, ptr)");
        _fwriteAdded = true;
    }

    internal void AddStrlen()
    {
        if (_strlenAdded) return;
        // strlen(str) → char count excluding null terminator; used by args.at(i) to measure argv strings.
        _sb.AppendLine("declare i64 @strlen(ptr)");
        _strlenAdded = true;
    }

    internal void AddFree()
    {
        if (_freeAdded) return;
        // free(ptr) — used by drop() to release struct field nodes and array buffers.
        _sb.AppendLine("declare void @free(ptr)");
        _freeAdded = true;
    }
}