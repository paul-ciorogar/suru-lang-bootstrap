namespace Suru.Compiler.Testing;

/// <summary>
/// The line protocol a test build's executable uses to report what its <c>#view</c> and
/// <c>#assert</c> directives saw. Codegen writes these lines; <see cref="TestRun"/> reads
/// them back.
/// <para>
/// Records ride on stdout beside the program's own output rather than on a second channel,
/// which needs no extern beyond the <c>printf</c> codegen already declares. They are safe to
/// tell apart by their prefix because Suru has no strings: a <c>printLn</c> can only ever
/// emit a number or <c>true</c>/<c>false</c>, so a program cannot forge one. That stops being
/// true the day string literals land, which is when this should move to
/// <c>fprintf(stderr, …)</c>.
/// </para>
/// </summary>
internal static class TestRecord
{
    /// <summary>ASCII record separator — not producible by any Suru program today.</summary>
    internal const char Separator = '\x1e';

    internal const string Prefix = "\x1esuru\x1e";

    internal const string View = "view";
    internal const string Assert = "assert";

    /// <summary>Fields after the prefix: <c>view</c>, the directive's id, the rendered value.</summary>
    internal const int ViewFieldCount = 3;

    /// <summary>
    /// Fields after the prefix: <c>assert</c>, the id, the 0/1 outcome, and both rendered
    /// values — the outcome is decided in the emitted code rather than by comparing the two
    /// strings here, so <c>f64</c> equality is the one the program itself computed.
    /// </summary>
    internal const int AssertFieldCount = 5;

    // TODO: consider generating output tagged with the line and column it should be printed to 
    // ex: for line 5 column 42 `5:42 <print output>`
}
