using Suru.Compiler.Testing;

namespace Suru.Tests.Compiler.Testing;

/// <summary>
/// That the C shim shipped. Not that it works — nothing calls it yet, and the C is first
/// exercised when codegen emits calls to it and the driver reads the other end.
/// <para>
/// This is the half of the shim that can break with no C# changing at all: a content item
/// dropped from the csproj leaves every test passing and every test build failing in
/// <c>cc</c>. Asserting it from the test assembly's own output directory is what proves the
/// item copies through a project reference, which is how it will reach a shipped compiler.
/// </para>
/// </summary>
public class RuntimeShimTests
{
    [Fact]
    public void FindsTheShimBesideTheAssembly()
    {
        var path = RuntimeShim.Locate();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), RuntimeShim.MissingMessage);
    }

    /// <summary>
    /// The shim is handed to <c>cc</c>, so what shipped has to be the source and not a stale
    /// name. Reading the first line of it is the cheapest statement of that.
    /// </summary>
    [Fact]
    public void ShipsTheSourceItself()
    {
        var text = File.ReadAllText(RuntimeShim.Locate()!);

        Assert.Contains("void suru_frame_begin(void)", text);
        Assert.Contains("void suru_field(const char *key, const char *format, ...)", text);
        Assert.Contains("void suru_frame_end(void)", text);
    }
}
