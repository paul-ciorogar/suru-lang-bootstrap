using Suru.Compiler.Debug;
using SuruCompiler = Suru.Compiler.Compiler;

namespace Suru.Tests;

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
        var dump = Compile(Dump.Llvm, "if");

        // 'entry' is the frame and nothing else: allocas, then the jump into the code.
        var entry = dump[dump.IndexOf("entry:", StringComparison.Ordinal)..];
        entry = entry[..entry.IndexOf("body:", StringComparison.Ordinal)];

        Assert.Contains("alloca", entry);
        Assert.Contains("br label %body", entry);
        Assert.DoesNotContain("call", entry);
        Assert.DoesNotContain("alloca", dump[dump.IndexOf("body:", StringComparison.Ordinal)..]);
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

    private string Compile(Dump stages, string fixture = "print")
    {
        var writer = new StringWriter();
        var compiler = new SuruCompiler(CompiledFixtures.FixturePath(fixture), new DumpOptions(stages, writer));

        var result = compiler.Compile(_buildDir);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        return writer.ToString();
    }
}
