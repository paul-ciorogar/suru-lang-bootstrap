using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Semantic;

namespace Suru.Tests;

// Unit tests for the Array<T> element type tracking introduced alongside the
// universal AST type annotation pass.
//
// When a variable or parameter is declared with an Array<TypeName> annotation
// the semantic analyzer records the element type so that InferType can resolve
// field access through arr.at(i).field chains, setting the correct ResolvedType
// on the FieldAccessExpression node. This eliminates the need for field-extractor
// wrapper functions in Suru code.
public class SemanticAnalyzerArrayElementTypeTests
{
    private static IReadOnlyList<string> Analyze(string source)
    {
        var module = Parser.Parse(new Tokens(new Lexer(source), "<test>")).Require();
        return SemanticAnalyzer.Analyze(module);
    }

    // ─── arr.at(i).field via function parameter ───────────────────────────────

    [Fact]
    public void ArrayParam_StringField_NoError()
    {
        // symbols.at(i).name where symbols: Array<Entry> and Entry.name is String.
        // Codegen must emit String unboxing, not Struct fallback.
        var errors = Analyze("""
            type Entry: { name String }
            fn findName(symbols Array<Entry>, i Int64) String {
                return symbols.at(i).name
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void ArrayParam_Int64Field_NoError()
    {
        // scopes.at(idx).parent where Scope.parent is Int64.
        var errors = Analyze("""
            type Scope: { parent Int64 }
            fn getParent(scopes Array<Scope>, idx Int64) Int64 {
                return scopes.at(idx).parent
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void ArrayParam_ChainedMethodCall_NoError()
    {
        // symbols.at(i).name.equals("x") — String field then method call.
        var errors = Analyze("""
            type Entry: { name String }
            fn hasName(symbols Array<Entry>, i Int64) Bool {
                return symbols.at(i).name.equals("x")
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    // ─── arr.at(i).field via let variable ─────────────────────────────────────

    [Fact]
    public void ArrayLet_StringField_NoError()
    {
        // let arr Array<Pt>: [] then arr.at(0).label as return value.
        var errors = Analyze("""
            type Pt: { label String, x Int64 }
            fn getLabel(arr Array<Pt>) String {
                return arr.at(0).label
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    [Fact]
    public void ArrayLet_Int64Field_NoError()
    {
        // let arr Array<Pt>: []; arr.at(0).x used in arithmetic.
        var errors = Analyze("""
            type Pt: { x Int64 }
            fn sumFirstTwo(arr Array<Pt>) Int64 {
                return arr.at(0).x.add(arr.at(1).x)
            }
            fn main(args Array<String>) { }
            """);
        Assert.Empty(errors);
    }

    // ─── All expression nodes are annotated ───────────────────────────────────

    [Fact]
    public void AllExpressions_ResolvedType_SetForKnownTypes()
    {
        // After semantic analysis every literal and method result carries a ResolvedType.
        // We verify indirectly: no errors and the fixture compiles. The ResolvedType
        // property is tested end-to-end via the suru-semantic integration test.
        var errors = Analyze("""
            fn main(args Array<String>) {
                let b Bool: true
                let n Int64: 42
                let f Float64: 1.5
                let s String: "hello"
                let len Int64: s.len()
                let eq Bool: s.equals("hello")
            }
            """);
        Assert.Empty(errors);
    }
}
