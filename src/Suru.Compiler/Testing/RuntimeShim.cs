namespace Suru.Compiler.Testing;

/// <summary>
/// Finds the C runtime linked into test builds — <c>runtime/suru_rt.c</c>, the program side of
/// the test channel.
/// <para>
/// A shipped compiler carries the source beside its assembly, so the ordinary answer is one
/// <see cref="File.Exists(string)"/>. Walking up the ancestors as well is what makes a compiler
/// run out of the source tree work, the way
/// <c>CompiledFixtures.FixturePath</c> walks up for a fixture.
/// </para>
/// <para>
/// The source is passed to <c>cc</c> rather than a prebuilt object file: a second compile costs
/// about 50 ms and removes the question of who builds the <c>.o</c>, and for which target.
/// </para>
/// <para>
/// Public for the same reason <see cref="FrameReader"/> is: there is no
/// <c>InternalsVisibleTo</c>, and whether the shim shipped is worth a test of its own — it is
/// the half of this that can break without any C# changing.
/// </para>
/// </summary>
public static class RuntimeShim
{
    /// <summary>Where the shim sits, relative to whichever directory holds it.</summary>
    private const string RelativePath = "runtime/suru_rt.c";

    /// <summary>
    /// What to report when it is missing. Named here rather than left to <c>cc</c>: a shim that
    /// did not ship is a broken installation, and saying so beats quoting a linker that was
    /// handed a path to nothing.
    /// </summary>
    public const string MissingMessage =
        "Test runtime not found: '" + RelativePath + "' is missing from the compiler's installation";

    /// <summary>
    /// The environment variable that hands the shim its socket path — the .NET side's one
    /// spelling of <c>SURU_CHANNEL_ENV</c> in <c>runtime/suru_rt.c</c>. It lives here rather
    /// than on <see cref="FrameProtocol"/> because it is about how the shim is <i>reached</i>,
    /// not about how a frame is spelled. A binary that finds no such variable drops its
    /// records, so the driver always sets it.
    /// </summary>
    public const string ChannelVariable = "SURU_TEST_CHANNEL";

    /// <summary>The shim's path, or <c>null</c> when it did not ship.</summary>
    public static string? Locate()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "runtime", "suru_rt.c");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
