namespace Suru.Compiler.Testing;

/// <summary>
/// How a <see cref="TestRecord"/> is spelled on the wire. The only place in the compiler
/// that knows: codegen asks for a line to print and <see cref="RecordReader"/> reads one
/// back, and neither spells a separator itself.
/// <para>
/// Records ride on stdout beside the program's own output rather than on a second channel,
/// which needs no extern beyond the <c>printf</c> codegen already declares. They are safe
/// to tell apart by their prefix because Suru has no strings: a <c>printLn</c> can only
/// ever emit a number or <c>true</c>/<c>false</c>, so a program cannot forge one. That
/// stops being true the day string literals land, which is what the framed channel in
/// <c>todo_test.md</c> replaces this with.
/// </para>
/// </summary>
internal static class RecordProtocol
{
    /// <summary>ASCII record separator — not producible by any Suru program today.</summary>
    internal const char Separator = '\x1e';

    internal const string Prefix = "\x1esuru\x1e";

    /// <summary>
    /// The line a record is printed as. The values are given as <c>printf</c> specifiers
    /// rather than as text, because it is the emitted program that fills them in.
    /// </summary>
    internal static string Line(string kind, int id, params string[] formats) =>
        Prefix + string.Join(Separator, [kind, id.ToString(), .. formats]) + "\n";
}
