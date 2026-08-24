using System.Runtime.InteropServices;
using System.Text;

namespace Suru.Compiler.Testing;

/// <summary>
/// Turns bytes off the test channel into <see cref="Frame"/>s.
/// <para>
/// A streaming cursor, because a socket read ends wherever the kernel says it does: chunks are
/// fed in as they arrive and whole frames come back out. A partial frame is held until the rest
/// of it turns up, which is what <c>Content-Length</c> is for.
/// </para>
/// <para>
/// Nothing here knows what a directive is, or which kinds exist. A frame whose <c>kind</c> means
/// nothing to the consumer is still a well-formed frame and is still yielded — skipping it is
/// the consumer's decision, and is how the event set grows without a flag.
/// </para>
/// </summary>
public sealed class FrameReader
{
    private readonly List<byte> _buffer = [];

    /// <summary>
    /// Whether everything fed so far was a whole number of frames. False means a frame was
    /// started and not finished, which at end of stream is a program that died mid-frame rather
    /// than one that simply stopped talking.
    /// </summary>
    public bool AtFrameBoundary => _buffer.Count == 0;

    /// <summary>
    /// Takes the next chunk and returns the frames it completed — none, when the chunk was only
    /// part of one, and more than one when several arrived together.
    /// </summary>
    public IReadOnlyList<Frame> Feed(ReadOnlySpan<byte> chunk)
    {
        _buffer.AddRange(chunk);

        var frames = new List<Frame>();
        while (TryTakeFrame(out var frame))
            frames.Add(frame);

        return frames;
    }

    private bool TryTakeFrame(out Frame frame)
    {
        frame = default;

        RequireHeaderStart();

        var headerEnd = IndexOfHeaderEnd();
        if (headerEnd < 0)
            return false;

        var length = DeclaredLength(headerEnd);
        var bodyStart = headerEnd + FrameProtocol.HeaderEnd.Length;

        // The whole body before any of it is parsed: a read boundary inside a frame is then a
        // non-event rather than a special case in every field.
        if (_buffer.Count - bodyStart < length)
            return false;

        frame = ReadBody(bodyStart, length);
        _buffer.RemoveRange(0, bodyStart + length);
        return true;
    }

    /// <summary>
    /// Rejects anything that is not a header as soon as enough bytes exist to tell, rather than
    /// waiting for a <c>\r\n\r\n</c> that is never coming.
    /// </summary>
    private void RequireHeaderStart()
    {
        var prefix = FrameProtocol.ContentLength;

        for (var i = 0; i < prefix.Length && i < _buffer.Count; i++)
            if (_buffer[i] != (byte)prefix[i])
                throw new FrameException(
                    $"the test channel sent a frame that does not begin with '{prefix.TrimEnd()}'");
    }

    private int IndexOfHeaderEnd()
    {
        var end = FrameProtocol.HeaderEnd;

        for (var i = FrameProtocol.ContentLength.Length; i + end.Length <= _buffer.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < end.Length && matched; j++)
                matched = _buffer[i + j] == (byte)end[j];

            if (matched)
                return i;
        }

        return -1;
    }

    private int DeclaredLength(int headerEnd)
    {
        var start = FrameProtocol.ContentLength.Length;
        var digits = Text(start, headerEnd - start);

        if (!int.TryParse(digits, out var length) || length < 0)
            throw new FrameException(
                $"the test channel sent a frame whose length is not a number: '{digits}'");

        return length;
    }

    private Frame ReadBody(int start, int length)
    {
        var fields = new List<(string Key, string Value)>();
        var end = start + length;
        var position = start;

        while (position < end)
            fields.Add(ReadField(ref position, end));

        if (fields is not [(Frame.KindKey, var kind), ..])
            throw new FrameException(
                $"the test channel sent a frame whose first field is " +
                $"'{(fields.Count == 0 ? "" : fields[0].Key)}', not '{Frame.KindKey}'");

        return new Frame(kind, fields);
    }

    /// <summary>
    /// One <c>key ":" byte-length ":" bytes "\n"</c>. Every step is checked against the end of
    /// the body, so a length that disagrees with the bytes around it is caught where it happens
    /// rather than turning the rest of the frame into a different frame.
    /// </summary>
    private (string Key, string Value) ReadField(ref int position, int end)
    {
        var keyEnd = IndexOfColon(position, end);
        var key = Text(position, keyEnd - position);
        RequireKey(key);

        var lengthStart = keyEnd + 1;
        var lengthEnd = IndexOfColon(lengthStart, end);
        var digits = Text(lengthStart, lengthEnd - lengthStart);

        if (!int.TryParse(digits, out var length) || length < 0)
            throw new FrameException(
                $"the test channel sent the field '{key}' with a length that is not a number: '{digits}'");

        // The delimiter has to sit inside the body too, so the value has one byte less room
        // than the body has left. Compared this way round rather than by adding, because a
        // length the frame made up can be large enough to wrap.
        var valueStart = lengthEnd + 1;
        if (length >= end - valueStart)
            throw new FrameException(
                $"the test channel sent the field '{key}' with a length that runs past the end of its frame");

        // The '\n' is a delimiter to check, not one to scan for: not finding it here is how a
        // length one byte out is caught, instead of being read as a shorter value.
        if (_buffer[valueStart + length] != FrameProtocol.FieldEnd)
            throw new FrameException(
                $"the test channel sent the field '{key}' with a length of {length} " +
                "that does not match its bytes");

        var value = Text(valueStart, length);
        position = valueStart + length + 1;
        return (key, value);
    }

    private int IndexOfColon(int from, int end)
    {
        for (var i = from; i < end; i++)
            if (_buffer[i] == FrameProtocol.Colon)
                return i;

        throw new FrameException("the test channel sent a field with no ':' before the end of its frame");
    }

    private static void RequireKey(string key)
    {
        var legal = key.Length > 0 && key[0] is >= 'a' and <= 'z';

        foreach (var c in key)
            legal &= c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';

        if (!legal)
            throw new FrameException($"the test channel sent a field with an unreadable key: '{key}'");
    }

    private string Text(int start, int length) =>
        Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_buffer).Slice(start, length));
}
