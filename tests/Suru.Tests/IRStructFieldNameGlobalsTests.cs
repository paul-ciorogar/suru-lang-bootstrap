using Suru.Compiler.Codegen;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Semantic;

namespace Suru.Tests;

// Verifies that struct field name globals use the @.field_N prefix (not @.str_N)
// and are emitted in a separate section from user string literals.
//
// Audit item #13: field name strings must not share the @.str_N namespace with
// user string literals — the two pools are logically distinct and mixing them
// makes the generated IR harder to read and opens a theoretical collision risk.
public class IRStructFieldNameGlobalsTests
{
    private static string GenerateIr(string source)
    {
        var module = Parser.Parse(new Tokens(new Lexer(source), "<test>")).Require();
        SemanticAnalyzer.Analyze(module);
        return IRCodeGenerator.Generate(module, "test");
    }

    [Fact]
    public void StructFieldNames_UseFieldPrefix_NotStrPrefix()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
                let v Int64: p.x
            }
            """);

        // Field name globals must use @.field_N.
        Assert.Contains("@.field_0", ir);
        Assert.Contains("@.field_1", ir);

        // No field name should appear under @.str_ — user strings are absent here,
        // so if @.str_ appears at all it means the field leaked into the wrong pool.
        Assert.DoesNotContain("@.str_", ir);
    }

    [Fact]
    public void FieldGlobals_AndStringLiterals_AreInSeparateSections()
    {
        var ir = GenerateIr("""
            type Msg: { text String }

            fn main(args Array<String>) {
                let m Msg: { text: "hello" }
                let s String: m.text
            }
            """);

        // User string literal "hello" uses @.str_N.
        Assert.Contains("@.str_0", ir);

        // Field name "text" uses @.field_N.
        Assert.Contains("@.field_0", ir);

        // The two pools must not overlap: field names must not appear under @.str_
        // and string literals must not appear under @.field_.
        var strLine   = ir.Split('\n').FirstOrDefault(l => l.StartsWith("@.str_0"));
        var fieldLine = ir.Split('\n').FirstOrDefault(l => l.StartsWith("@.field_0"));

        Assert.NotNull(strLine);
        Assert.NotNull(fieldLine);

        // "hello" must be in the @.str_ global, not in any @.field_ global.
        Assert.Contains("hello", strLine);
        Assert.DoesNotContain("hello", fieldLine);

        // "text" (field name) must be in the @.field_ global, not in any @.str_ global.
        Assert.Contains("text", fieldLine);
        Assert.DoesNotContain("text", strLine);
    }

    [Fact]
    public void SameFieldName_InternedOnce_AcrossMultipleStructLiterals()
    {
        var ir = GenerateIr("""
            type Point: { x Int64, y Int64 }

            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
                let q Point: { x: 3, y: 4 }
            }
            """);

        // "x" and "y" appear in two struct literals but must each be interned once.
        var fieldLines = ir.Split('\n').Where(l => l.StartsWith("@.field_")).ToList();
        Assert.Equal(2, fieldLines.Count); // @.field_0 (x) and @.field_1 (y) only
    }
}
