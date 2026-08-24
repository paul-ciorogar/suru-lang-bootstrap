using System.Text;

namespace Suru.Compiler.Testing;

/// <summary>
/// Renders a <see cref="Frame"/> to the bytes that carry it.
/// <para>
/// The emitted program does not use this — it writes its own frames, in C, from the same
/// grammar. This exists so the grammar has an executable statement on the .NET side to test
/// <see cref="FrameReader"/> against, and for the harness-to-program direction, where the
/// driver is the one writing.
/// </para>
/// </summary>
public static class FrameWriter
{
    /// <summary>
    /// A frame of <paramref name="kind"/> carrying <paramref name="fields"/> after it. The
    /// <c>kind</c> field is written here rather than passed in, because it is first in every
    /// frame and a caller that could place it could place it wrongly.
    /// </summary>
    public static byte[] Write(string kind, params (string Key, string Value)[] fields)
    {
        var body = new List<byte>();

        Field(body, Frame.KindKey, kind);
        foreach (var (key, value) in fields)
            Field(body, key, value);

        var header = Encoding.UTF8.GetBytes(
            FrameProtocol.ContentLength + body.Count + FrameProtocol.HeaderEnd);

        return [.. header, .. body];
    }

    /// <summary>
    /// <c>key ":" byte-length ":" bytes "\n"</c>. The length counts the value's bytes, which is
    /// what lets a value hold a newline, a separator, or anything else the reader would
    /// otherwise have to scan for.
    /// </summary>
    private static void Field(List<byte> body, string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        body.AddRange(Encoding.UTF8.GetBytes($"{key}:{bytes.Length}:"));
        body.AddRange(bytes);
        body.Add(FrameProtocol.FieldEnd);
    }
}
