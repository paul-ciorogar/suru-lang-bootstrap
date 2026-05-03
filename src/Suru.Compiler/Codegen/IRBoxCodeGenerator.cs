using System.Text;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Type utilities ───────────────────────────────────────────────────────

    // Non-static so it can consult _module.TypeDeclarations for user-defined named types.
    private SuruType SuruTypeFromAnnotation(TypeAnnotation ann) => ann.Name switch
    {
        "Bool"    => SuruType.Bool,
        "Int32"   => SuruType.Int32,
        "Int64"   => SuruType.Int64,
        "Float64" => SuruType.Float64,
        "String"  => SuruType.String,
        "Array"   => SuruType.Array,
        // Named types declared via `type Foo: { ... }` use the same heap struct layout.
        _ => _module.TypeDeclarations.ContainsKey(ann.Name)
            ? SuruType.Struct
            : throw new NotSupportedException($"IR codegen: unsupported type annotation '{ann}'"),
    };

    // Scalars use their raw LLVM type; heap types remain `ptr`.
    // Scalars (Bool/Int32/Int64/Float64) are stored as raw LLVM types in local vars,
    // function params, and returns. Box calls are made only at three boundary points:
    // printLn/printError, array element store/load, and struct field store/load.
    private static string LlvmType(SuruType type) => type switch
    {
        SuruType.Bool    => "i1",
        SuruType.Int32   => "i32",
        SuruType.Int64   => "i64",
        SuruType.Float64 => "double",
        _                => "ptr",
    };

    // True for the four scalar types that use raw LLVM types (not ptr).
    private static bool IsScalar(SuruType t) =>
        t is SuruType.Bool or SuruType.Int32 or SuruType.Int64 or SuruType.Float64;

    // Raw LLVM type for arithmetic/comparison operands.
    // Struct is treated as i64 (unknown field assumed to be box-of-Int64 at runtime).
    private static string RawLlvmType(SuruType type) => type switch
    {
        SuruType.Bool    => "i1",
        SuruType.Int32   => "i32",
        SuruType.Int64   => "i64",
        SuruType.Float64 => "double",
        SuruType.Struct  => "i64",   // unknown struct fields unbox as i64 (see UnboxScalar)
        _                => "ptr",
    };

    // Void functions map to SuruType.Struct so LlvmType → "ptr" and implicit return is `ret ptr null`.
    private SuruType FnReturnSuruType(FunctionDeclaration fn)
        => fn.ReturnType.Name is "void" ? SuruType.Struct : SuruTypeFromAnnotation(fn.ReturnType);

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

    // Default zero-value constant for use in implicit returns.
    private static string DefaultReturnValue(SuruType type) => type switch
    {
        SuruType.Bool    => "0",
        SuruType.Int32   => "0",
        SuruType.Int64   => "0",
        SuruType.Float64 => "0.0",
        _                => "null",
    };

    private string NextTmp() => $"%t{_tmp++}";
}
