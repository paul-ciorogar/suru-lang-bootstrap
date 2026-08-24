namespace Suru.Compiler.Testing;

/// <summary>
/// One thing a test build reported: which kind of directive spoke, which directive it was,
/// and the values it saw already rendered as text.
/// <para>
/// This is the seam between reading and reporting. <see cref="RecordReader"/> turns bytes
/// into these and knows the encoding; <see cref="TestRun"/> turns these into annotations
/// and diagnostics and knows nothing about how they travelled.
/// </para>
/// </summary>
/// <param name="Kind"><see cref="View"/> or <see cref="Assert"/>; anything else is skipped.</param>
/// <param name="Id">The directive that emitted it, assigned by the parser in source order.</param>
/// <param name="Values">The fields after the id, in the order the record carried them.</param>
internal readonly record struct TestRecord(string Kind, int Id, IReadOnlyList<string> Values)
{
    internal const string View = "view";
    internal const string Assert = "assert";

    /// <summary>A <c>#view</c> reports one value: the rendered subject.</summary>
    internal const int ViewValueCount = 1;

    /// <summary>
    /// An <c>#assert</c> reports the 0/1 outcome and both rendered values — the outcome is
    /// decided in the emitted code rather than by comparing the two strings here, so
    /// <c>f64</c> equality is the one the program itself computed.
    /// </summary>
    internal const int AssertValueCount = 3;
}
