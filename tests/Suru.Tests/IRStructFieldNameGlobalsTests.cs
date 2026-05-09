using Suru.Compiler.Codegen;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Semantic;

namespace Suru.Tests;

// Verifies the flat struct layout emitted by IRCodeGenerator (Phase 2 zero-cost structs).
//
// Fields are accessed via byte-offset GEPs at offset 32+i*8.
// No @.field_N globals — the old suru_find_field linked-list approach has been removed.
// Per-type @suru_clone_T / @suru_drop_T functions are emitted into _helpers.
public class IRStructFlatLayoutTests
{
    private static string GenerateIr(string source)
    {
        var module = Parser.Parse(new Tokens(new Lexer(source), "<test>")).Require();
        SemanticAnalyzer.Analyze(module);
        return IRCodeGenerator.Generate(module, "test");
    }

    [Fact]
    public void StructLiteral_EmitsPerTypeCloneDrop()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
            }
            """);

        Assert.Contains("define ptr @suru_clone_Point(ptr %s)", ir);
        Assert.Contains("define void @suru_drop_Point(ptr %s)", ir);
    }

    [Fact]
    public void StructLiteral_StoresVtablePtrs_AtOffsets16And24()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
            }
            """);

        Assert.Contains("store ptr @suru_clone_Point", ir);
        Assert.Contains("store ptr @suru_drop_Point", ir);
    }

    [Fact]
    public void FieldAccess_UsesGepOffset32()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
                let v Int64: p.x
            }
            """);

        // Field 0 (x) lives at offset 32.
        Assert.Contains("getelementptr i8, ptr", ir);
        Assert.Contains("i64 32", ir);
    }

    [Fact]
    public void TwoTypes_EmitTwoIndependentCloneDropPairs()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }
            type Rect: { w Int64, h Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
                let r Rect: { w: 3, h: 4 }
            }
            """);

        Assert.Contains("define ptr @suru_clone_Point(ptr %s)", ir);
        Assert.Contains("define void @suru_drop_Point(ptr %s)", ir);
        Assert.Contains("define ptr @suru_clone_Rect(ptr %s)", ir);
        Assert.Contains("define void @suru_drop_Rect(ptr %s)", ir);
    }

    [Fact]
    public void NoFieldNameGlobals_InFlatLayout()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
                let v Int64: p.x
            }
            """);

        // The old linked-list approach interned field names as @.field_N globals.
        // The flat layout uses GEP offsets — no field name globals should appear.
        Assert.DoesNotContain("@.field_", ir);
    }
}
