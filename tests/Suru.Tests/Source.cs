using Suru.Compiler;
using Suru.Compiler.Lex;
using Suru.Compiler.Parse;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Semantic;

namespace Suru.Tests;

/// <summary>
/// Runs the front end over in-memory source text. Tests that only care about
/// lexing, parsing or semantic analysis use this instead of <see cref="CompiledFixtures"/>,
/// which needs a file on disk, LLVM and a C toolchain.
/// </summary>
internal static class Source
{
    public const string Path = "test.suru";

    public static Module Parse(string text, BuildMode mode = BuildMode.Production) =>
        Parser.Parse(new Lexer(text, Path, mode));

    public static IReadOnlyList<string> Analyze(string text, BuildMode mode = BuildMode.Production) =>
        SemanticAnalyzer.Analyze(Parse(text, mode));

    /// <summary>Parses and analyzes, asserting the source is error free, and returns the module.</summary>
    public static Module Analyzed(string text, BuildMode mode = BuildMode.Production)
    {
        var module = Parse(text, mode);
        Assert.Empty(SemanticAnalyzer.Analyze(module));
        return module;
    }

    /// <summary>The single statement of <paramref name="text"/>, as its expression.</summary>
    public static Expression SingleExpression(Module module) =>
        Assert.IsType<ExpressionStatement>(Assert.Single(module.Statements)).Expression;
}
