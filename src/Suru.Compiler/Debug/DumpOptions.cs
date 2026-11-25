namespace Suru.Compiler.Debug;

/// <summary>
/// Where stage dumps go and which stages are enabled. The CLI supplies the writer, so the
/// "only the CLI prints" convention holds.
/// </summary>
public sealed class DumpOptions(Dump enabled, TextWriter writer)
{
    /// <summary>The default: every check is a flag test and no printer ever runs.</summary>
    public static readonly DumpOptions Off = new(Dump.None, TextWriter.Null);

    public Dump Enabled { get; } = enabled;

    public bool IsEnabled(Dump stage) => (Enabled & stage) != 0;

    /// <summary>
    /// <paramref name="body"/> is a callback so a disabled stage never runs its printer. Line
    /// endings are always <c>\n</c> so dumps are byte-identical across platforms.
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
