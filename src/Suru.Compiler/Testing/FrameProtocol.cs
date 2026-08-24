namespace Suru.Compiler.Testing;

/// <summary>
/// The spelling of a <see cref="Frame"/> on the wire, in the one place both halves ask.
/// <para>
/// The grammar is written out in <c>doc/test-protocol.md</c>, because a third implementation of
/// it — the C shim linked into test builds — is written from that document rather than from
/// this file.
/// </para>
/// </summary>
internal static class FrameProtocol
{
    /// <summary>
    /// The one header, counting the body's bytes. It is not redundant beside the per-field
    /// lengths: it lets a reader take an entire frame off the socket before parsing any of it,
    /// which is what makes a read boundary landing mid-frame a non-event, and it lets a reader
    /// skip a frame whose <c>kind</c> it does not know — which is how the event set grows
    /// without a flag day.
    /// </summary>
    internal const string ContentLength = "Content-Length: ";

    /// <summary>Ends the header block, LSP-shaped.</summary>
    internal const string HeaderEnd = "\r\n\r\n";

    /// <summary>
    /// Ends a field. A <b>checkable delimiter, not a separator</b>: a reader that has consumed
    /// a field's declared byte count and does not then find this knows the length was wrong and
    /// says so. A format where a bad length silently reinterprets the rest of the frame is the
    /// failure framing exists to prevent.
    /// </summary>
    internal const byte FieldEnd = (byte)'\n';

    /// <summary>Separates a field's key from its length, and its length from its bytes.</summary>
    internal const byte Colon = (byte)':';
}
