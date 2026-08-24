namespace Suru.Compiler.Testing;

/// <summary>
/// The reading half of a test run: it takes what the program wrote and separates the
/// records from the program's own output, decoding each record into a
/// <see cref="TestRecord"/>. It has no idea what a directive is — matching a record to one
/// is <see cref="TestRun"/>'s job.
/// </summary>
internal static class RecordReader
{
    /// <summary>
    /// Splits <paramref name="stdout"/> into the records it carried and the output that was
    /// not one. A line that begins like a record but does not decode is dropped rather than
    /// passed through as output: it came from the compiler, not from the program, so
    /// printing it would be reporting a compiler bug as program output.
    /// </summary>
    internal static (IReadOnlyList<TestRecord> Records, string Output) Read(string stdout)
    {
        var records = new List<TestRecord>();
        var output = new List<string>();

        foreach (var raw in stdout.Split('\n'))
        {
            if (!raw.StartsWith(RecordProtocol.Prefix, StringComparison.Ordinal))
            {
                output.Add(raw);
                continue;
            }

            if (Decode(raw) is { } record)
                records.Add(record);
        }

        return (records, string.Join('\n', output));
    }

    private static TestRecord? Decode(string line)
    {
        var fields = line.TrimEnd('\r')[RecordProtocol.Prefix.Length..]
            .Split(RecordProtocol.Separator);

        if (fields.Length < 2 || !int.TryParse(fields[1], out var id))
            return null;

        return new TestRecord(fields[0], id, fields[2..]);
    }
}
