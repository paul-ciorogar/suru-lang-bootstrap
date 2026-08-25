namespace Suru.Compiler.Testing;

/// <summary>
/// One message off the test channel: an event kind and the fields that came with it, in the
/// order the wire carried them.
/// <para>
/// This is the seam between the wire and whoever consumes it. <see cref="FrameReader"/> turns
/// bytes into these and is the only thing that knows the grammar; a consumer reads fields by
/// name and knows nothing about lengths or delimiters. Values are text here because the
/// program renders them — the compiler never re-parses a value it was told.
/// </para>
/// </summary>
/// <param name="Kind">The first field of every frame, and what says how to read the rest.</param>
/// <param name="Fields">Every field, <c>kind</c> included, in wire order.</param>
public readonly record struct Frame(string Kind, IReadOnlyList<(string Key, string Value)> Fields)
{
    /// <summary>The key of the field that is always first.</summary>
    public const string KindKey = "kind";

    /// <summary>One execution of the program body began.</summary>
    public const string RunStarted = "run-started";

    /// <summary>What a <c>#view</c> saw.</summary>
    public const string View = "view";

    /// <summary>
    /// What an <c>#assert</c> decided. The outcome is computed in the emitted code rather than
    /// by comparing the two rendered values here, so <c>f64</c> equality is the one the program
    /// itself computed.
    /// </summary>
    public const string Assert = "assert";

    /// <summary>
    /// The program body ran to the end. Its absence is what tells a run that died apart from a
    /// run that simply never reached a directive.
    /// </summary>
    public const string RunFinished = "run-finished";

    /// <summary>
    /// The program had more to say than one frame could hold, and <b>dropped the payload rather
    /// than truncating it</b> — carrying the <c>key</c> of the field that did not fit. A frame
    /// short of its own declared length is the failure framing exists to prevent, and a reader
    /// that met one could only stop; this one costs a frame and leaves the channel usable.
    /// </summary>
    public const string Overflow = "overflow";

    /// <summary>
    /// The value of the first field with this key, or <c>null</c> when the frame does not carry
    /// one. A frame whose <c>kind</c> a reader does not recognise is skipped rather than
    /// rejected, so a missing field is an ordinary answer and not an error.
    /// </summary>
    public string? this[string key]
    {
        get
        {
            foreach (var (candidate, value) in Fields)
                if (candidate == key)
                    return value;

            return null;
        }
    }
}
