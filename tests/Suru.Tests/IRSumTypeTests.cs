using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Semantic;
using Suru.Compiler.Types;

namespace Suru.Tests;

// Verifies Stage 13g: named sum type declarations (`type Shape: Circle, Square`).
//
// A sum type declaration introduces a discriminated union at module scope. Each variant
// name must refer to a declared struct type (TypeDeclaration). The declaration itself
// generates no IR in Stage 13g — codegen support arrives in Stage 13h.
//
// These are pure unit tests (lexer, parser, semantic). No integration fixture needed
// until Stage 13h wires up codegen.
public class IRSumTypeTests
{
    // ─── Parser tests ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SumTypeDeclaration_TwoVariants_ProducesCorrectNode()
    {
        var module = ParseSource("""
            type Circle: { radius Int64 }
            type Square: { side Int64 }
            type Shape: Circle, Square
            fn main(args Array<String>) { }
            """);
        var std = module.Statements.OfType<SumTypeDeclaration>().First();
        Assert.Equal("Shape", std.Name);
        Assert.Equal(2, std.Variants.Count);
        Assert.Equal("Circle", std.Variants[0]);
        Assert.Equal("Square", std.Variants[1]);
    }

    [Fact]
    public void Parse_SumTypeDeclaration_SingleVariant_Accepted()
    {
        var module = ParseSource("""
            type Some: { value Int64 }
            type Maybe: Some
            fn main(args Array<String>) { }
            """);
        var std = module.Statements.OfType<SumTypeDeclaration>().First();
        Assert.Equal("Maybe", std.Name);
        Assert.Single(std.Variants);
        Assert.Equal("Some", std.Variants[0]);
    }

    [Fact]
    public void Parse_SumTypeDeclaration_PopulatesModuleSumTypeDeclarations()
    {
        var module = ParseSource("""
            type Circle: { radius Int64 }
            type Square: { side Int64 }
            type Shape: Circle, Square
            fn main(args Array<String>) { }
            """);
        Assert.True(module.SumTypeDeclarations.ContainsKey("Shape"));
        Assert.Equal(2, module.SumTypeDeclarations["Shape"].Variants.Count);
    }

    [Fact]
    public void Parse_StructDeclaration_NotAffectedBySumTypeDispatch()
    {
        // The struct path must be unaffected after introducing sum type dispatch.
        var module = ParseSource("""
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) { }
            """);
        var td = module.Statements.OfType<TypeDeclaration>().First();
        Assert.Equal("Point", td.Name);
        Assert.Equal(2, td.Fields.Count);
        Assert.Empty(module.SumTypeDeclarations);
    }

    [Fact]
    public void Parse_BothStructAndSumType_BothIndexed()
    {
        var module = ParseSource("""
            type Circle: { radius Int64 }
            type Shape: Circle
            fn main(args Array<String>) { }
            """);
        Assert.True(module.TypeDeclarations.ContainsKey("Circle"));
        Assert.True(module.SumTypeDeclarations.ContainsKey("Shape"));
    }

    // ─── AstPrinter tests ────────────────────────────────────────────────────

    [Fact]
    public void AstPrinter_SumTypeDeclaration_RendersCorrectly()
    {
        var module = ParseSource("""
            type Circle: { radius Int64 }
            type Square: { side Int64 }
            type Shape: Circle, Square
            fn main(args Array<String>) { }
            """);
        var printed = Suru.Compiler.Parse.AstPrinter.Print(module);
        Assert.Contains("SumType [Shape] Variants: [Circle, Square]", printed);
    }

    // ─── Semantic tests ──────────────────────────────────────────────────────

    [Fact]
    public void Semantic_ValidSumTypeDeclaration_NoErrors()
    {
        var errors = AnalyzeSource("""
            type Circle: { radius Int64 }
            type Square: { side Int64 }
            type Shape: Circle, Square
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void Semantic_DuplicateSumTypeName_ReportsError()
    {
        var errors = AnalyzeSource("""
            type Circle: { radius Int64 }
            type Shape: Circle
            type Shape: Circle
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("Shape") && e.Contains("already declared"));
    }

    [Fact]
    public void Semantic_UnknownVariant_ReportsError()
    {
        var errors = AnalyzeSource("""
            type Circle: { radius Int64 }
            type Shape: Circle, Triangle
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("Triangle"));
    }

    [Fact]
    public void Semantic_AllVariantsUnknown_ReportsErrorForEach()
    {
        var errors = AnalyzeSource("""
            type Shape: Foo, Bar
            fn main(args Array<String>) { }
            """);
        Assert.Contains(errors, e => e.Contains("Foo"));
        Assert.Contains(errors, e => e.Contains("Bar"));
    }

    [Fact]
    public void Semantic_SumTypeNameUsedAsVariableType_Accepted()
    {
        // Sum type names must be valid type annotations once declared.
        var errors = AnalyzeSource("""
            type Circle: { radius Int64 }
            type Shape: Circle
            fn getShape(s Shape) Int64 {
                return 0
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void Semantic_SumTypeResolvesToSumType_InTypeSystem()
    {
        // Verify the type system returns SuruType.SumType (not null) for declared sum types.
        var module = ParseSource("""
            type Circle: { radius Int64 }
            type Shape: Circle
            fn main(args Array<String>) { }
            """);
        var ann = new Suru.Compiler.Parse.Ast.TypeAnnotation("Shape");
        var resolved = SuruTypeSystem.TryResolve(ann, module.TypeDeclarations, module.SumTypeDeclarations);
        var sumType = Assert.IsType<SuruType.SumType>(resolved);
        Assert.Equal("Shape", sumType.Name);
        Assert.Single(sumType.Variants);
        Assert.Equal("Circle", sumType.Variants[0]);
    }

    [Fact]
    public void Semantic_SumType_HasCorrectTypeTag()
    {
        Assert.Equal(7, SuruType.SumType.Tag);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Module ParseSource(string source)
    {
        var tokens = new Tokens(new Lexer(source), "<test>");
        return Parser.Parse(tokens).Require();
    }

    private static IReadOnlyList<string> AnalyzeSource(string source)
    {
        var tokens = new Tokens(new Lexer(source), "<test>");
        var module = Parser.Parse(tokens).Require();
        return SemanticAnalyzer.Analyze(module);
    }
}

// Stage 13i: variant creation and field access codegen.
//
// Verifies that `let c Circle: { radius: 2283 }` wraps the struct in
// @suru_variant_create, and that `c.radius` unwraps via @suru_variant_inner
// before calling @suru_find_field to load the field.
[Collection("IntegrationIR")]
public class IRSumTypeIntegrationTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("sum-types");
    private bool _testPassed;

    [Fact]
    public void SumType_VariantCreation_FieldAccess_PrintsRadius()
    {
        var output = Run(_exe);
        Assert.Equal("2283\n", output);
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("sum-types"); }
}
