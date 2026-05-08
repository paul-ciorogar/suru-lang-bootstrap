using Suru.Compiler;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;

namespace Suru.Tests;

// Unit tests for IncludeResolver and IncludeGraph.
//
// Each test writes temporary .suru files into a per-test temp directory,
// resolves them directly through IncludeResolver.Resolve, and inspects the
// resulting Module — no IR codegen or file-system compilation involved.
public class IncludeResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SuruIncludeTests_" + Guid.NewGuid());

    public IncludeResolverTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private string Write(string fileName, string source)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, source);
        return path;
    }

    private static Module Parse(string path)
    {
        var source = File.ReadAllText(path);
        return Parser.Parse(new Tokens(new Lexer(source), path)).Require();
    }

    private Module Resolve(string mainPath)
    {
        var module = Parse(mainPath);
        var graph  = new IncludeGraph(Path.GetFullPath(mainPath));
        return IncludeResolver.Resolve(module, _dir, graph);
    }

    // ─── No-op ───────────────────────────────────────────────────────────────

    [Fact]
    public void NoIncludes_ReturnsModuleUnchanged()
    {
        var path = Write("main.suru", "fn main(args Array<String>) { }");
        var result = Resolve(path);
        Assert.Empty(result.Aliases.All);
        Assert.Empty(result.IncludedSourcePaths);
    }

    // ─── Error cases ─────────────────────────────────────────────────────────

    [Fact]
    public void MissingIncludeFile_Throws()
    {
        var path = Write("main.suru", """include "missing.suru" as lib""");
        var ex = Assert.Throws<Exception>(() => Resolve(path));
        Assert.Contains("Include file not found", ex.Message);
        Assert.Contains("missing.suru", ex.Message);
    }

    [Fact]
    public void CircularInclude_Throws()
    {
        // a.suru includes b.suru, b.suru includes a.suru
        Write("a.suru", """
            include "b.suru" as b
            fn fa() Int64 { return 1 }
            """);
        Write("b.suru", """
            include "a.suru" as a
            fn fb() Int64 { return 2 }
            """);
        var mainPath = Write("main.suru", """include "a.suru" as a""");

        var ex = Assert.Throws<Exception>(() => Resolve(mainPath));
        Assert.Contains("Circular include detected", ex.Message);
    }

    // ─── Namespace / function merging ─────────────────────────────────────────

    [Fact]
    public void SingleInclude_AddsAliasAndMergesFunction()
    {
        var libPath = Write("lib.suru", "fn double(n Int64) Int64 { return n.multiply(2) }");
        var mainPath = Write("main.suru", """
            include "lib.suru" as lib
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        Assert.True(result.Aliases.Contains("lib"));
        // Function is merged with its unqualified name; SourcePath identifies origin.
        var fns = result.Statements.OfType<FunctionDeclaration>().ToList();
        Assert.Contains(fns, f => f.Name == "double" && f.SourcePath != null);
        // Registry maps the canonical (path, name) identity.
        var absLib = Path.GetFullPath(libPath);
        Assert.NotNull(result.ExternalDeclarationRegistry.LookupFunction(absLib, "double"));
    }

    [Fact]
    public void SingleInclude_IncludedPathRecorded()
    {
        var libPath = Write("lib.suru", "fn greet() Int64 { return 1 }");
        var mainPath = Write("main.suru", """
            include "lib.suru" as lib
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var absLib = Path.GetFullPath(libPath);
        Assert.Single(result.IncludedSourcePaths);
        Assert.Equal(absLib, result.IncludedSourcePaths[0]);
    }

    // ─── Diamond include ──────────────────────────────────────────────────────

    [Fact]
    public void DiamondInclude_SharedFileAppearsOnceInPaths()
    {
        // common.suru is included by both left.suru and right.suru.
        // main.suru includes both left and right.
        // common should appear exactly once in IncludedSourcePaths.
        var commonPath = Write("common.suru", "fn shared() Int64 { return 0 }");
        Write("left.suru",  """include "common.suru" as com""");
        Write("right.suru", """include "common.suru" as com""");
        var mainPath = Write("main.suru", """
            include "left.suru" as left
            include "right.suru" as right
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var absCommon = Path.GetFullPath(commonPath);
        Assert.Equal(1, result.IncludedSourcePaths.Count(p => p == absCommon));
    }

    [Fact]
    public void DiamondInclude_FunctionsNotDuplicated()
    {
        Write("common.suru", "fn shared() Int64 { return 0 }");
        Write("left.suru",  """include "common.suru" as com""");
        Write("right.suru", """include "common.suru" as com""");
        var mainPath = Write("main.suru", """
            include "left.suru" as left
            include "right.suru" as right
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var count = result.Statements.OfType<FunctionDeclaration>()
            .Count(f => f.Name == "shared");
        Assert.Equal(1, count);
    }

    // ─── Type merging ─────────────────────────────────────────────────────────

    [Fact]
    public void IncludedTypeDeclaration_MergedIntoTypeDeclarations()
    {
        Write("lib.suru", "type Point: { x Int64, y Int64 }");
        var mainPath = Write("main.suru", """
            include "lib.suru" as lib
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        Assert.True(result.TypeDeclarations.ContainsKey("Point"));
    }

    [Fact]
    public void DiamondTypeInclude_TypeAppearsOnce()
    {
        Write("types.suru", "type Token: { kind Int64 }");
        Write("lexer.suru", """include "types.suru" as t""");
        var mainPath = Write("main.suru", """
            include "types.suru" as t
            include "lexer.suru" as lex
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var count = result.Statements.OfType<TypeDeclaration>().Count(td => td.Name == "Token");
        Assert.Equal(1, count);
    }

    // ─── Constant merging ─────────────────────────────────────────────────────

    [Fact]
    public void ScalarInt64Constant_MergedFromInclude()
    {
        Write("lib.suru", "let LIMIT Int64: 100");
        var mainPath = Write("main.suru", """
            include "lib.suru" as lib
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var constants = result.Statements.OfType<LetStatement>().Select(ls => ls.Name).ToList();
        Assert.Contains("LIMIT", constants);
    }

    [Fact]
    public void StringConstant_NotMergedFromInclude()
    {
        // String constants cannot be emitted as simple LLVM globals, so they are
        // intentionally excluded from the merge.
        Write("lib.suru", """let GREETING String: "hello" """);
        var mainPath = Write("main.suru", """
            include "lib.suru" as lib
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var constants = result.Statements.OfType<LetStatement>().Select(ls => ls.Name).ToList();
        Assert.DoesNotContain("GREETING", constants);
    }

    [Fact]
    public void DiamondConstant_AppearsOnce()
    {
        Write("common.suru", "let LIMIT Int64: 100");
        Write("left.suru",  """include "common.suru" as com""");
        Write("right.suru", """include "common.suru" as com""");
        var mainPath = Write("main.suru", """
            include "left.suru" as left
            include "right.suru" as right
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var count = result.Statements.OfType<LetStatement>().Count(ls => ls.Name == "LIMIT");
        Assert.Equal(1, count);
    }

    // ─── Transitive propagation ────────────────────────────────────────────────

    [Fact]
    public void TransitiveNamespace_PropagatedToImporter()
    {
        // main includes mid, mid includes leaf as lns.
        // After resolution, main's AliasMap should contain "lns".
        Write("leaf.suru", "fn leafFn() Int64 { return 1 }");
        Write("mid.suru",  """include "leaf.suru" as lns""");
        var mainPath = Write("main.suru", """
            include "mid.suru" as mid
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        Assert.True(result.Aliases.Contains("lns"));
    }

    [Fact]
    public void TransitiveFunction_PropagatedInRegistry()
    {
        // main includes mid, mid includes leaf as lns.
        // leaf's "leafFn" should appear in statements with its unqualified name
        // and be resolvable from the registry via the leaf file's canonical path.
        var leafPath = Write("leaf.suru", "fn leafFn() Int64 { return 1 }");
        Write("mid.suru",  """include "leaf.suru" as lns""");
        var mainPath = Write("main.suru", """
            include "mid.suru" as mid
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var fns = result.Statements.OfType<FunctionDeclaration>().Select(f => f.Name).ToList();
        Assert.Contains("leafFn", fns);
        var absLeaf = Path.GetFullPath(leafPath);
        Assert.NotNull(result.ExternalDeclarationRegistry.LookupFunction(absLeaf, "leafFn"));
    }

    [Fact]
    public void TransitiveSourcePath_PropagatedToImporter()
    {
        var leafPath = Write("leaf.suru", "fn leafFn() Int64 { return 1 }");
        Write("mid.suru",  """include "leaf.suru" as lns""");
        var mainPath = Write("main.suru", """
            include "mid.suru" as mid
            fn main(args Array<String>) { }
            """);

        var result = Resolve(mainPath);

        var absLeaf = Path.GetFullPath(leafPath);
        Assert.Contains(absLeaf, result.IncludedSourcePaths);
    }
}
