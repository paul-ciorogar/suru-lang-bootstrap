using System.Text;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

public sealed partial class IRCodeGenerator
{
    // ─── Type utilities ───────────────────────────────────────────────────────

    // Resolves a TypeAnnotation to the statically-known SuruType.
    // Consults _module.TypeDeclarations for user-defined named types.
    private SuruType SuruTypeFromAnnotation(TypeAnnotation ann) => ann.Name switch
    {
        "Bool"    => SuruType.Bool,
        "Int32"   => SuruType.Int32,
        "Int64"   => SuruType.Int64,
        "Float64" => SuruType.Float64,
        "String"  => SuruType.String,
        "Array"   => ann.TypeParam is { } tp
                        ? new SuruType.ArrayType(SuruTypeFromAnnotation(tp))
                        : throw new NotSupportedException($"IR codegen: Array requires a type parameter"),
        _ => _module.TypeDeclarations.ContainsKey(ann.Name)
                ? new SuruType.NamedType(ann.Name)
                : throw new NotSupportedException($"IR codegen: unsupported type annotation '{ann}'"),
    };

    // Scalars (Bool/Int32/Int64/Float64) use raw LLVM types in local vars,
    // function params, and returns. All heap types use ptr.
    // VoidType and NamedType (struct) both map to ptr — void emits `ret ptr null`.
    private static string LlvmType(SuruType type) => type switch
    {
        SuruType.BoolType    => "i1",
        SuruType.Int32Type   => "i32",
        SuruType.Int64Type   => "i64",
        SuruType.Float64Type => "double",
        _                    => "ptr",
    };

    // True for the four scalar types that use raw LLVM values (not ptr).
    private static bool IsScalar(SuruType t) =>
        t is SuruType.BoolType or SuruType.Int32Type or SuruType.Int64Type or SuruType.Float64Type;

    // Raw LLVM type for arithmetic/comparison operands.
    // NamedType is treated as i64 (dynamic struct field assumed to be Box<Int64> at runtime).
    private static string RawLlvmType(SuruType type) => type switch
    {
        SuruType.BoolType    => "i1",
        SuruType.Int32Type   => "i32",
        SuruType.Int64Type   => "i64",
        SuruType.Float64Type => "double",
        SuruType.NamedType   => "i64",   // unknown struct field unboxed as i64 (see UnboxScalar)
        _                    => "ptr",
    };

    // Void functions return SuruType.Void; codegen emits `ret ptr null` for them
    // since Suru's IR convention uses ptr as the return type for non-scalar functions.
    private SuruType FnReturnSuruType(FunctionDeclaration fn)
        => fn.ReturnType.Name is "void" ? SuruType.Void : SuruTypeFromAnnotation(fn.ReturnType);

    // Default zero-value constant for implicit returns.
    private static string DefaultReturnValue(SuruType type) => type switch
    {
        SuruType.BoolType    => "0",
        SuruType.Int32Type   => "0",
        SuruType.Int64Type   => "0",
        SuruType.Float64Type => "0.0",
        _                    => "null",
    };

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
        SuruType.BoolType    => BoxBool(rawVal),
        SuruType.Int32Type   => BoxInt32(rawVal),
        SuruType.Int64Type   => BoxInt64(rawVal),
        SuruType.Float64Type => BoxFloat64(rawVal),
        _                    => rawVal,   // String/Array/NamedType already carry type_tag
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
    // For NamedType values (dynamic unknown struct field), assume Int64 at runtime.
    private string UnboxScalar(string ptrval, SuruType type) => type switch
    {
        SuruType.BoolType    => UnboxBool(ptrval),
        SuruType.Int32Type   => UnboxInt32(ptrval),
        SuruType.Int64Type   => UnboxInt64(ptrval),
        SuruType.Float64Type => UnboxFloat64(ptrval),
        SuruType.NamedType   => UnboxInt64(ptrval),   // assume Box(Int64) at runtime
        _ => throw new NotSupportedException($"IR codegen: cannot unbox {type}"),
    };

    // ─── String literal utilities ─────────────────────────────────────────────

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

    private static int CountStringBytes(string source) => source.Length;

    private string NextTmp() => $"%t{_tmp++}";
}
