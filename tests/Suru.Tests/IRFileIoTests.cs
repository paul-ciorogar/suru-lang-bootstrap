namespace Suru.Tests;

// Verifies the IR backend's file I/O built-ins and argv access via args.at(i).
//
// This is the first fixture that actually uses the `args Array` parameter — all
// prior fixtures call printLn or user-defined functions without reading argv.
//
// ── args.at(i) ──────────────────────────────────────────────────────────────
//
// The @main wrapper stores C's argc/argv as a %suru.Seq = { i64 len, ptr data }
// where data = argv (char**) and len = argc. Because the data field is a char**,
// element access cannot use the general i64-array GEP pattern — it must GEP into a
// ptr array instead:
//
//   %data  = load ptr from Seq.data            → char**
//   %slot  = getelementptr ptr, ptr %data, i64 %i → pointer to char*
//   %cstr  = load ptr, ptr %slot               → char* for argv[i]
//   %len   = call i64 @strlen(ptr %cstr)       → character count
//   result = EmitCreateStringSeq(%cstr, %len)  → String Seq wrapping the C string
//
// This is NOT a general Array.at implementation — it works only for the argv Seq.
// `_vars["args"]` is registered in EmitFunction (main branch) so the body can load
// it via the standard EmitLoad path, just like any let-bound local.
//
// ── readFile ────────────────────────────────────────────────────────────────
//
// EmitReadFile uses the fseek(SEEK_END) + ftell pattern to determine file size:
//   fopen(path_data, "r")         → file ptr
//   fseek(file, 0, SEEK_END=2)   → move to end
//   size = ftell(file)            → byte count
//   rewind(file)                  → reset to start
//   buf = malloc(size + 1)        → +1 for null terminator
//   fread(buf, 1, size, file)     → fill buffer
//   buf[size] = '\0'              → null-terminate (makes buf a valid C string)
//   fclose(file)
//   EmitCreateStringSeq(buf, size) → returned String Seq
//
// The @.mode_r constant ("r\00") is a raw [2 x i8] global — not a Seq-wrapped Suru
// String — because fopen expects a plain char*, not a %suru.Seq pointer.
//
// ── writeFile ───────────────────────────────────────────────────────────────
//
// EmitWriteFile extracts both the path data ptr and the content len+data from their
// Seq headers, then calls fopen("w") + fwrite + fclose.
// "w" mode truncates an existing file before writing.
[Collection("IntegrationIR")]
public class IRFileIoTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _readExe  = fixtures.GetExecutable("file_io");
    private readonly string _writeExe = fixtures.GetExecutable("file_io_write");

    [Fact]
    public void ReadFile_EchoesFileContent()
    {
        var tmpPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpPath, "hello from file");
            Assert.Equal("hello from file\n", Run(_readExe, tmpPath));
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }

    [Fact]
    public void WriteFile_CopiesContent()
    {
        var inPath  = Path.GetTempFileName();
        var outPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(inPath, "suru stage 7");
            Run(_writeExe, inPath, outPath);
            Assert.Equal("suru stage 7", File.ReadAllText(outPath));
        }
        finally
        {
            File.Delete(inPath);
            File.Delete(outPath);
        }
    }
}
