using Suru.Compiler;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Semantic;

namespace Suru.Tests;

// Verifies Stage 12.5b: named type declarations (`type Foo: { ... }`).
//
// A named type declaration introduces a user-defined struct shape at module scope.
// At the semantic layer it resolves to SuruType.Struct; at runtime it uses the same
// suru.Field linked-list representation as anonymous Struct values.  The declaration
// itself generates no IR — it only informs the type system so that `let p Point: { ... }`
// is accepted wherever a Struct-typed variable is expected.
//
// Integration test: compiles tests/fixtures/named-types/main.suru and verifies stdout.
// Unit tests: exercise Lexer, Parser, and SemanticAnalyzer directly without file I/O.
[Collection("IntegrationIR")]
public class IRNamedTypeTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("named-types");
    private bool _testPassed;

    // ─── Integration ─────────────────────────────────────────────────────────

    [Fact]
    public void NamedType_FixtureProducesExpectedOutput()
    {
        // Point: x=2283, y=2281; Person: name="Suru", age=1
        Assert.Equal("2283\n2281\nSuru\n1\n", Run(_exe));
        _testPassed = true;
    }

    // ─── Lexer unit tests ────────────────────────────────────────────────────

    [Fact]
    public void Lex_TypeKeyword_ProducesTypeToken()
    {
        var tokens = Lexer.Tokenize("type Point: { x Int64 }").ToList();
        Assert.Equal(TokenKind.Type, tokens[0].Kind);
        Assert.Equal("type", tokens[0].Text);
    }

    [Fact]
    public void Lex_TypeIsNotAnIdentifier()
    {
        // `type` must lex as TokenKind.Type, not TokenKind.Identifier.
        var tokens = Lexer.Tokenize("type").ToList();
        Assert.Equal(TokenKind.Type, tokens[0].Kind);
        Assert.NotEqual(TokenKind.Identifier, tokens[0].Kind);
    }

    // ─── Parser unit tests ───────────────────────────────────────────────────

    [Fact]
    public void Parse_InlineTypeDeclaration_ProducesCorrectNode()
    {
        var module = ParseSource("type Point: { x Int64, y Int64 }\nfn main(args Array<String>) { }");
        var td = module.Statements.OfType<TypeDeclaration>().First();
        Assert.Equal("Point", td.Name);
        Assert.Equal(2, td.Fields.Count);
        Assert.Equal("x", td.Fields[0].Field);
        Assert.Equal("Int64", td.Fields[0].Type.Name);
        Assert.Equal("y", td.Fields[1].Field);
        Assert.Equal("Int64", td.Fields[1].Type.Name);
    }

    [Fact]
    public void Parse_MultilineTypeDeclaration_ProducesCorrectNode()
    {
        var src = "type Person: {\n    name String\n    age Int64\n}\nfn main(args Array<String>) { }";
        var module = ParseSource(src);
        var td = module.Statements.OfType<TypeDeclaration>().First();
        Assert.Equal("Person", td.Name);
        Assert.Equal(2, td.Fields.Count);
        Assert.Equal("name", td.Fields[0].Field);
        Assert.Equal("String", td.Fields[0].Type.Name);
        Assert.Equal("age", td.Fields[1].Field);
        Assert.Equal("Int64", td.Fields[1].Type.Name);
    }

    [Fact]
    public void Parse_TypeDeclaration_PopulatesModuleTypeDeclarations()
    {
        var module = ParseSource("type Point: { x Int64, y Int64 }\nfn main(args Array<String>) { }");
        Assert.True(module.TypeDeclarations.ContainsKey("Point"));
        Assert.Equal(2, module.TypeDeclarations["Point"].Fields.Count);
    }

    [Fact]
    public void Parse_GenericFieldType_InTypeDeclaration()
    {
        var module = ParseSource("type Container: { items Array<Int64> }\nfn main(args Array<String>) { }");
        var td = module.Statements.OfType<TypeDeclaration>().First();
        Assert.Equal("Array", td.Fields[0].Type.Name);
        Assert.Equal("Int64", td.Fields[0].Type.TypeParam!.Name);
    }

    // ─── Semantic unit tests ─────────────────────────────────────────────────

    [Fact]
    public void Semantic_DeclaredNamedType_AcceptedAsVariableType()
    {
        var src = """
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
            }
            """;
        var errors = AnalyzeSource(src);
        Assert.Empty(errors);
    }

    [Fact]
    public void Semantic_UnknownType_ReportsError()
    {
        var src = """
            fn main(args Array<String>) {
                let p Unknown: { x: 1 }
            }
            """;
        var errors = AnalyzeSource(src);
        Assert.Contains(errors, e => e.Contains("Unknown"));
    }

    [Fact]
    public void Semantic_DuplicateTypeDeclaration_ReportsError()
    {
        var src = """
            type Point: { x Int64 }
            type Point: { y Int64 }
            fn main(args Array<String>) { }
            """;
        var errors = AnalyzeSource(src);
        Assert.Contains(errors, e => e.Contains("Point") && e.Contains("already declared"));
    }

    [Fact]
    public void Semantic_NamedTypeInFunctionParam_Accepted()
    {
        var src = """
            type Point: { x Int64, y Int64 }
            fn getX(p Point) Int64 {
                return p.x
            }
            fn main(args Array<String>) { }
            """;
        var errors = AnalyzeSource(src);
        Assert.Empty(errors);
    }

    // ─── Stage 12.5c: Typed struct instantiation (no field annotations) ──────

    [Fact]
    public void Parse_StructLiteral_NoTypeAnnotation_ProducesCorrectFields()
    {
        var module = ParseSource("fn main(args Array<String>) { let s Struct: { x: 1, y: 2 } }");
        var fn = module.Statements.OfType<FunctionDeclaration>().First();
        var let = fn.Body.OfType<LetStatement>().First();
        var sl = Assert.IsType<StructLiteralExpression>(let.Value);
        Assert.Equal(2, sl.Fields.Count);
        Assert.Equal("x", sl.Fields[0].Name);
        Assert.Equal("y", sl.Fields[1].Name);
    }

    [Fact]
    public void Semantic_StructLiteralAgainstNamedType_ValidFields_NoError()
    {
        var src = """
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2 }
            }
            """;
        var errors = AnalyzeSource(src);
        Assert.Empty(errors);
    }

    [Fact]
    public void Semantic_StructLiteralAgainstNamedType_ExtraField_ReportsError()
    {
        var src = """
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) {
                let p Point: { x: 1, y: 2, z: 3 }
            }
            """;
        var errors = AnalyzeSource(src);
        Assert.Contains(errors, e => e.Contains("Point") && e.Contains("field"));
    }

    [Fact]
    public void Semantic_StructLiteralAgainstNamedType_WrongFieldName_ReportsError()
    {
        var src = """
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) {
                let p Point: { x: 1, z: 2 }
            }
            """;
        var errors = AnalyzeSource(src);
        Assert.Contains(errors, e => e.Contains("Point") && e.Contains("'z'"));
    }

    [Fact]
    public void Semantic_StructLiteralAgainstNamedType_MissingField_ReportsError()
    {
        var src = """
            type Point: { x Int64, y Int64 }
            fn main(args Array<String>) {
                let p Point: { x: 1 }
            }
            """;
        var errors = AnalyzeSource(src);
        Assert.Contains(errors, e => e.Contains("Point") && e.Contains("2 field"));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Module ParseSource(string source)
    {
        var tokens = new Tokens(new Lexer(source), "<test>");
        return Parser.Parse(tokens);
    }

    private static IReadOnlyList<string> AnalyzeSource(string source)
    {
        var tokens = new Tokens(new Lexer(source), "<test>");
        var module = Parser.Parse(tokens);
        return SemanticAnalyzer.Analyze(module);
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("named-types"); }
}
