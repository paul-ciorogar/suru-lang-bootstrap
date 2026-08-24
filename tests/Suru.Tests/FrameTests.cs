using System.Text;
using Suru.Compiler.Testing;

namespace Suru.Tests;

/// <summary>
/// The wire the test channel will carry, both ways round: what <see cref="FrameWriter"/>
/// produces, and what <see cref="FrameReader"/> makes of bytes arriving however they arrive.
/// <para>
/// No LLVM, no <c>cc</c>, no process — the format is provable on its own, which is why it is
/// built before the socket that carries it. The C shim is written from the same grammar, so the
/// exact bytes asserted here are what it has to produce.
/// </para>
/// </summary>
public class FrameTests
{
    private const string ViewFrame =
        "Content-Length: 30\r\n\r\nkind:4:view\nid:1:0\nvalue:2:42\n";

    [Fact]
    public void WritesAFrame()
    {
        Assert.Equal(ViewFrame, Text(FrameWriter.Write(Frame.View, ("id", "0"), ("value", "42"))));
    }

    [Fact]
    public void ReadsAFrameArrivingInOneChunk()
    {
        var frame = Assert.Single(new FrameReader().Feed(Bytes(ViewFrame)));

        Assert.Equal(Frame.View, frame.Kind);
        Assert.Equal("0", frame["id"]);
        Assert.Equal("42", frame["value"]);
    }

    [Fact]
    public void ReadsAFrameArrivingOneByteAtATime()
    {
        // The case the length header exists for: a read boundary falls wherever the kernel says.
        var reader = new FrameReader();
        var bytes = Bytes(ViewFrame);
        var frames = new List<Frame>();

        foreach (var b in bytes)
            frames.AddRange(reader.Feed([b]));

        var frame = Assert.Single(frames);
        Assert.Equal("42", frame["value"]);
        Assert.True(reader.AtFrameBoundary);
    }

    [Fact]
    public void HoldsAPartialFrameAndKnowsItIsHoldingOne()
    {
        // At end of stream this is a program that died mid-frame, not one that finished.
        var reader = new FrameReader();

        Assert.Empty(reader.Feed(Bytes(ViewFrame[..^5])));
        Assert.False(reader.AtFrameBoundary);
    }

    [Fact]
    public void ReadsTwoFramesFromOneChunk()
    {
        var frames = new FrameReader().Feed(Bytes(
            ViewFrame + Text(FrameWriter.Write(Frame.RunFinished, ("run", "0"), ("exit", "0")))));

        Assert.Equal(2, frames.Count);
        Assert.Equal(Frame.View, frames[0].Kind);
        Assert.Equal(Frame.RunFinished, frames[1].Kind);
        Assert.Equal("0", frames[1]["exit"]);
    }

    [Fact]
    public void ReadsAValueContainingANewline()
    {
        // The whole reason a field is measured rather than delimited: line framing loses this
        // one, and a string literal is the day it stops being hypothetical.
        var frame = Assert.Single(
            new FrameReader().Feed(FrameWriter.Write(Frame.View, ("value", "one\ntwo"))));

        Assert.Equal("one\ntwo", frame["value"]);
    }

    [Fact]
    public void AnswersNullForAFieldTheFrameDoesNotCarry()
    {
        var frame = Assert.Single(new FrameReader().Feed(Bytes(ViewFrame)));

        Assert.Null(frame["outcome"]);
    }

    [Fact]
    public void RejectsALengthOneByteShort()
    {
        // Not a resync: the reader no longer knows where anything starts, and guessing is the
        // failure framing exists to prevent.
        var error = Assert.Throws<FrameException>(() => new FrameReader().Feed(
            Bytes("Content-Length: 30\r\n\r\nkind:3:view\nid:1:0\nvalue:2:42\n")));

        Assert.Equal(
            "the test channel sent the field 'kind' with a length of 3 that does not match its bytes",
            error.Message);
    }

    [Fact]
    public void RejectsALengthPastTheEndOfTheFrame()
    {
        var error = Assert.Throws<FrameException>(() => new FrameReader().Feed(
            Bytes("Content-Length: 13\r\n\r\nkind:99:view\n")));

        Assert.Equal(
            "the test channel sent the field 'kind' with a length that runs past the end of its frame",
            error.Message);
    }

    [Fact]
    public void RejectsAMissingHeader()
    {
        var error = Assert.Throws<FrameException>(
            () => new FrameReader().Feed(Bytes("kind:4:view\nid:1:0\n")));

        Assert.Equal(
            "the test channel sent a frame that does not begin with 'Content-Length:'", error.Message);
    }

    [Fact]
    public void RejectsALengthThatIsNotANumber()
    {
        var error = Assert.Throws<FrameException>(
            () => new FrameReader().Feed(Bytes("Content-Length: many\r\n\r\n")));

        Assert.Equal(
            "the test channel sent a frame whose length is not a number: 'many'", error.Message);
    }

    [Fact]
    public void RejectsAFrameThatDoesNotStartWithItsKind()
    {
        var error = Assert.Throws<FrameException>(
            () => new FrameReader().Feed(Bytes("Content-Length: 7\r\n\r\nid:1:0\n")));

        Assert.Equal(
            "the test channel sent a frame whose first field is 'id', not 'kind'", error.Message);
    }

    [Fact]
    public void RejectsAnUnreadableFieldKey()
    {
        var error = Assert.Throws<FrameException>(
            () => new FrameReader().Feed(Bytes("Content-Length: 12\r\n\r\nKind:4:view\n")));

        Assert.Equal("the test channel sent a field with an unreadable key: 'Kind'", error.Message);
    }

    [Fact]
    public void PassesOnAFrameWhoseKindItDoesNotKnow()
    {
        // Skipping an unrecognised event is the consumer's decision, not the reader's — which is
        // what lets the event set grow without a flag day.
        var frames = new FrameReader().Feed(Bytes(
            Text(FrameWriter.Write("panicked", ("why", "unspecified"))) + ViewFrame));

        Assert.Equal(2, frames.Count);
        Assert.Equal("panicked", frames[0].Kind);
        Assert.Equal("unspecified", frames[0]["why"]);
        Assert.Equal(Frame.View, frames[1].Kind);
    }

    [Fact]
    public void RoundTripsEveryEvent()
    {
        var written = new[]
        {
            FrameWriter.Write(Frame.RunStarted, ("run", "0")),
            FrameWriter.Write(Frame.View, ("id", "0"), ("value", "1e+20")),
            FrameWriter.Write(Frame.Assert, ("id", "1"), ("outcome", "fail"), ("actual", "40"), ("expected", "12")),
            FrameWriter.Write(Frame.RunFinished, ("run", "0"), ("exit", "1")),
        };

        var reader = new FrameReader();
        var frames = new List<Frame>();
        foreach (var bytes in written)
            frames.AddRange(reader.Feed(bytes));

        Assert.Equal(
            [Frame.RunStarted, Frame.View, Frame.Assert, Frame.RunFinished],
            frames.Select(frame => frame.Kind));
        Assert.Equal("1e+20", frames[1]["value"]);
        Assert.Equal("fail", frames[2]["outcome"]);
        Assert.True(reader.AtFrameBoundary);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
