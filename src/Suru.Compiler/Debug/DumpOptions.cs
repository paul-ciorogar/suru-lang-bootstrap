namespace Suru.Compiler.Debug;

/// <summary>
/// Where stage dumps go and which stages are enabled. The compiler never picks a
/// destination itself — the CLI passes one in, so the "only the CLI prints"
/// convention still holds.
/// </summary>
public sealed class DumpOptions(Dump enabled, TextWriter writer)
{
    /// <summary>The default: nothing is enabled, so every check is a flag test and no printer ever runs.</summary>
    public static readonly DumpOptions Off = new(Dump.None, TextWriter.Null);

    public Dump Enabled { get; } = enabled;

    public bool IsEnabled(Dump stage) => (Enabled & stage) != 0;

    /// <summary>
    /// Writes one titled section. <paramref name="body"/> is a callback so the
    /// printer is never invoked for a disabled stage. Line endings are always
    /// <c>\n</c> so dumps are byte-identical across platforms.
    /// </summary>
    public void Section(Dump stage, string title, Func<string> body)
    {
        if (!IsEnabled(stage))
            return;

        writer.Write($"===== {title} =====\n");
        writer.Write(body().TrimEnd('\n', '\r'));
        writer.Write("\n\n");
        writer.Flush();
    }
}
