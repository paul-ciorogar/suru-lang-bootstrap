using Suru.Compiler.Debug;
using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests.Integration;

/// <summary>
/// The LLVM dump is the one stage that cannot be checked without LLVM, so it
/// drives a real compile rather than going through <see cref="CompiledFixtures"/>,
/// which does not take dump options.
/// </summary>
[Collection("Integration")]
public class DumpIntegrationTests : IDisposable
{
    private readonly string _buildDir =
        Path.Combine(Path.GetTempPath(), "suru-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_buildDir))
            Directory.Delete(_buildDir, recursive: true);
    }

    [Fact]
    public void DumpsLlvmIrAndNothingElse()
    {
        var dump = Compile(Dump.Llvm);

        Assert.Contains("===== llvm ir =====", dump);
        Assert.Contains("define i32 @main", dump);
        Assert.Contains("declare i32 @printf", dump);
        Assert.DoesNotContain("===== tokens =====", dump);
    }

    [Fact]
    public void DumpsEveryStageInPipelineOrder()
    {
        var dump = Compile(Dump.All);

        var titles = new[] { "tokens", "ast after parse", "ast after semantic analysis", "llvm ir" };
        var positions = titles.Select(title => dump.IndexOf($"===== {title} =====", StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void WritesNothingWhenNoStageIsEnabled()
    {
        Assert.Equal("", Compile(Dump.None));
    }

    [Fact]
    public void EveryStackSlotSitsInTheEntryBlock()
    {
        // Suru's 'main' rather than the C entry point, which holds no bindings of its own.
        var dump = Compile(Dump.Llvm, "if");
        var main = dump[dump.IndexOf("define internal void @suru.main()", StringComparison.Ordinal)..];

        // 'entry' is the frame and nothing else: allocas, then the jump into the code.
        var entry = main[main.IndexOf("entry:", StringComparison.Ordinal)..];
        entry = entry[..entry.IndexOf("body:", StringComparison.Ordinal)];

        Assert.Contains("alloca", entry);
        Assert.Contains("br label %body", entry);
        Assert.DoesNotContain("call", entry);
        Assert.DoesNotContain("alloca", main[main.IndexOf("body:", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void ABranchEmitsLabelledBasicBlocks()
    {
        var dump = Compile(Dump.Llvm, "if");

        // Substrings rather than whole labels: LLVM uniquifies a name it has already used.
        Assert.Contains("br i1 ", dump);
        Assert.Contains("if.then", dump);
        Assert.Contains("if.else", dump);
        Assert.Contains("if.end", dump);
    }

    [Fact]
    public void FunctionsAreInternalQualifiedAndEmittedAfterMain()
    {
        var dump = Compile(Dump.Llvm, "functions");

        // The C entry point first, so the IR opens with where the program starts.
        Assert.True(
            dump.IndexOf("define i32 @main()", StringComparison.Ordinal)
            < dump.IndexOf("define internal", StringComparison.Ordinal));

        // A nested function is qualified by the one around it, and nothing a Suru program
        // declares is visible outside the module.
        Assert.Contains("define internal i64 @hypotenuseSquared.sq(i64 %x)", dump);
        Assert.DoesNotContain("define i64", dump);

        // A parameter is stored into its own slot, so it can be loaded and assigned like a 'let'.
        Assert.Contains("store i64 %n, ptr %n.addr", dump);

        // 'sign' returns from every arm; the block left open after them is declared unreachable.
        Assert.Contains("unreachable", dump);
    }

    [Fact]
    public void MainIsAnInternalFunctionTheEntryPointCalls()
    {
        var dump = Compile(Dump.Llvm, "main");

        // The C entry point is still the module's first function, and Suru's 'main' is an
        // ordinary internal one under a symbol no identifier can spell.
        Assert.True(
            dump.IndexOf("define i32 @main()", StringComparison.Ordinal)
            < dump.IndexOf("define internal void @suru.main()", StringComparison.Ordinal));
        Assert.Contains("call void @suru.main()", dump);
    }

    [Fact]
    public void AndOrBranchToAPhiInsteadOfComputingBothSides()
    {
        var dump = Compile(Dump.Llvm, "short-circuit");

        Assert.Contains("and.rhs", dump);
        Assert.Contains("and.end", dump);
        Assert.Contains("or.rhs", dump);
        Assert.Contains("or.end", dump);
        Assert.Contains("phi i1", dump);

        // The bitwise forms evaluate both operands, which is what short-circuiting rules out.
        Assert.DoesNotContain("= and i1", dump);
        Assert.DoesNotContain("= or i1", dump);
    }

    private string Compile(Dump stages, string fixture = "print")
    {
        var writer = new StringWriter();
        var compiler = new SuruCompiler(CompiledFixtures.FixturePath(fixture), new DumpOptions(stages, writer));

        var result = compiler.Compile(_buildDir);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        return writer.ToString();
    }
}
