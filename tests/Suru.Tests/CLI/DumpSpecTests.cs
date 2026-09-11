using Suru.CLI;
using Suru.Compiler.Debug;

namespace Suru.Tests.CLI;

/// <summary>
/// The text form of the <see cref="Dump"/> flags, which is the command line's business
/// rather than the compiler's. <see cref="SpecTests"/> covers how a command line reaches
/// this; these are the stage list itself.
/// </summary>
public class DumpSpecTests
{
    [Theory]
    [InlineData("tokens", Dump.Tokens)]
    [InlineData("ast", Dump.Ast)]
    [InlineData("typed-ast", Dump.TypedAst)]
    [InlineData("llvm", Dump.Llvm)]
    [InlineData("all", Dump.All)]
    [InlineData("tokens,llvm", Dump.Tokens | Dump.Llvm)]
    [InlineData(" tokens , ast ", Dump.Tokens | Dump.Ast)]
    [InlineData("TOKENS", Dump.Tokens)]
    public void ParsesStageLists(string spec, Dump expected)
    {
        Assert.Equal(expected, Parsed(spec));
    }

    [Fact]
    public void RejectsAnUnknownStageRatherThanIgnoringIt()
    {
        Assert.Equal(
            "unknown dump stage 'nope'; expected one of: tokens, ast, typed-ast, llvm, all",
            Error("tokens,nope"));
    }

    // Nothing to dump is a stage list too, and the one a parse of it fails on is the
    // caller's to give meaning to: '--dump' with no stages means all of them, not none.
    [Fact]
    public void AnEmptyListIsNoStages()
    {
        Assert.Equal(Dump.None, Parsed(""));
    }

    private static Dump Parsed(string spec) =>
        DumpSpec.Parse(spec).Match(
            dump => dump,
            error => throw new Xunit.Sdk.XunitException($"expected stages, got error: {error.Message}"));

    private static string Error(string spec) =>
        DumpSpec.Parse(spec).Match(
            dump => throw new Xunit.Sdk.XunitException($"expected an error, got {dump}"),
            error => error.Message);

    // The error message and the usage text are the same list, so neither can name a stage
    // the other does not.
    [Fact]
    public void NamesEveryStageItAccepts()
    {
        Assert.Equal("tokens, ast, typed-ast, llvm, all", DumpSpec.Names);
    }
}
