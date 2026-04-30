using System.Text;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Type utilities ───────────────────────────────────────────────────────

    private static SuruType SuruTypeFromAnnotation(TypeAnnotation ann) => ann.Name switch
    {
        "Bool"    => SuruType.Bool,
        "Int32"   => SuruType.Int32,
        "Int64"   => SuruType.Int64,
        "Float64" => SuruType.Float64,
        "String"  => SuruType.String,
        "Array"   => SuruType.Array,
        "Struct"  => SuruType.Struct,
        _ => throw new NotSupportedException($"IR codegen: unsupported type annotation '{ann}'"),
    };

    // Every Suru value in user .ll is a `ptr` (Box for scalars, direct heap ptr for rest).
    private static string LlvmType(SuruType _) => "ptr";

    // Raw LLVM type for scalar operations (box/unbox calls, global constant loads, arithmetic).
    private static string RawLlvmType(SuruType type) => type switch
    {
        SuruType.Bool    => "i1",
        SuruType.Int32   => "i32",
        SuruType.Int64   => "i64",
        SuruType.Float64 => "double",
        SuruType.Struct  => "i64",   // unknown struct fields unbox as i64 (see UnboxScalar)
        _                => "ptr",
    };

    private static SuruType FnReturnSuruType(FunctionDeclaration fn)
        => fn.ReturnType.Name is "void" ? SuruType.Int64 : SuruTypeFromAnnotation(fn.ReturnType);

    // ─── Box / Unbox helpers ──────────────────────────────────────────────────

    private string BoxBool(string i1val)
    {
        _runtimeDecls.AddBoxBool();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_bool(i1 {i1val})");
        return tmp;
    }

    private string BoxInt32(string i32val)
    {
        _runtimeDecls.AddBoxInt32();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_int32(i32 {i32val})");
        return tmp;
    }

    private string BoxInt64(string i64val)
    {
        _runtimeDecls.AddBoxInt64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_int64(i64 {i64val})");
        return tmp;
    }

    private string BoxFloat64(string doubleval)
    {
        _runtimeDecls.AddBoxFloat64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call ptr @suru_box_float64(double {doubleval})");
        return tmp;
    }

    private string BoxValue(string rawVal, SuruType type) => type switch
    {
        SuruType.Bool    => BoxBool(rawVal),
        SuruType.Int32   => BoxInt32(rawVal),
        SuruType.Int64   => BoxInt64(rawVal),
        SuruType.Float64 => BoxFloat64(rawVal),
        _                => rawVal,   // String/Array/Struct already carry type_tag
    };

    private string UnboxBool(string ptrval)
    {
        _runtimeDecls.AddUnboxBool();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i1 @suru_unbox_bool(ptr {ptrval})");
        return tmp;
    }

    private string UnboxInt32(string ptrval)
    {
        _runtimeDecls.AddUnboxInt32();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i32 @suru_unbox_int32(ptr {ptrval})");
        return tmp;
    }

    private string UnboxInt64(string ptrval)
    {
        _runtimeDecls.AddUnboxInt64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call i64 @suru_unbox_int64(ptr {ptrval})");
        return tmp;
    }

    private string UnboxFloat64(string ptrval)
    {
        _runtimeDecls.AddUnboxFloat64();
        var tmp = NextTmp();
        _funcs.AppendLine($"  {tmp} = call double @suru_unbox_float64(ptr {ptrval})");
        return tmp;
    }

    // Unbox a scalar value to its raw LLVM type for arithmetic/comparison.
    // For Struct-typed values (dynamic unknown type), assume Int64 at runtime.
    private string UnboxScalar(string ptrval, SuruType type) => type switch
    {
        SuruType.Bool    => UnboxBool(ptrval),
        SuruType.Int32   => UnboxInt32(ptrval),
        SuruType.Int64   => UnboxInt64(ptrval),
        SuruType.Float64 => UnboxFloat64(ptrval),
        SuruType.Struct  => UnboxInt64(ptrval),   // assume Box(Int64) at runtime
        _                => throw new NotSupportedException($"IR codegen: cannot unbox {type}"),
    };

    // ─── String literal utilities ─────────────────────────────────────────────

    // Escape a C# string (post-Suru-lexer, fully unescaped) for use as an LLVM IR
    // string constant.  LLVM's only escape form is \XX (hex), so we hex-escape every
    // non-printable byte and the two special chars (`"` and `\`).  The Suru lexer has
    // already converted escape sequences (e.g. `\n` in source → 0x0A in memory), so we
    // must NOT re-interpret `\n` here — a backslash followed by `n` is two literal bytes.
    private static string EscapeStringForIR(string source)
    {
        var sb = new StringBuilder();
        foreach (char c in source)
        {
            if (c >= 32 && c < 127 && c != '"' && c != '\\')
                sb.Append(c);
            else
                sb.Append($"\\{(int)c:X2}");
        }
        return sb.ToString();
    }

    // Each C# char maps to one byte (Suru strings are ASCII).
    // The Suru lexer has already unescaped all escape sequences, so there are no
    // multi-char escape tokens here — each char in the C# string is a real byte.
    private static int CountStringBytes(string source) => source.Length;

    private string NextTmp() => $"%t{_tmp++}";
}
