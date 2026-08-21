using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Testing;

/// <summary>
/// Turns what a test build printed back into source annotations and diagnostics: it splits
/// the <see cref="TestRecord"/> lines out of the program's own output, matches each one to
/// the directive that emitted it, and writes the results into the file the directives came
/// from.
/// </summary>
internal static class TestRun
{
    internal static TestResult Report(Module module, string sourcePath, string stdout, int exitCode)
    {
        var directives = new Dictionary<int, Directive>();
        CollectDirectives(module.Statements, directives);

        var output = new List<string>();
        var annotations = new Dictionary<int, string>();
        var failures = new List<string>();
        int passed = 0, views = 0;

        foreach (var raw in stdout.Split('\n'))
        {
            if (!raw.StartsWith(TestRecord.Prefix, StringComparison.Ordinal))
            {
                output.Add(raw);
                continue;
            }

            var fields = raw.TrimEnd('\r')[TestRecord.Prefix.Length..].Split(TestRecord.Separator);
            if (fields.Length < 2 || !int.TryParse(fields[1], out var id)
                || !directives.TryGetValue(id, out var directive))
                continue;

            if (fields is [TestRecord.View, _, var value] )
            {
                annotations[directive.Position.Line] = value;
                views++;
            }
            else if (fields is [TestRecord.Assert, _, var outcome, var actual, var expected])
            {
                var held = outcome == "1";
                annotations[directive.Position.Line] = held ? "pass" : $"fail, got {actual}";

                if (held)
                    passed++;
                else
                    failures.Add(
                        $"{sourcePath}({directive.Position.Line},{directive.Position.Column}): " +
                        $"assert failed: expected {expected}, got {actual}");
            }
        }

        Annotate(sourcePath, annotations);

        return TestResult.Ran(string.Join('\n', output), exitCode, failures, passed, views);
    }

    private static void CollectDirectives(
        IReadOnlyList<Statement> statements, Dictionary<int, Directive> into)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case ViewDirective view:
                    into[view.Id] = view;
                    break;
                case AssertDirective assert:
                    into[assert.Id] = assert;
                    break;
                case BlockStatement block:
                    CollectDirectives(block.Statements, into);
                    break;
            }
        }
    }

    /// <summary>
    /// Writes each annotation into its directive's line, reading and writing the file once.
    /// Line endings are preserved by splitting on '\n' and leaving any '\r' where it was.
    /// </summary>
    private static void Annotate(string sourcePath, IReadOnlyDictionary<int, string> annotations)
    {
        if (annotations.Count == 0)
            return;

        var lines = File.ReadAllText(sourcePath).Split('\n');

        foreach (var (number, text) in annotations)
        {
            var index = number - 1;
            if (index < 0 || index >= lines.Length)
                continue;

            var line = lines[index];
            var carriageReturn = line.EndsWith('\r') ? "\r" : "";
            var body = carriageReturn.Length > 0 ? line[..^1] : line;
            lines[index] = Annotated(body, text) + carriageReturn;
        }

        File.WriteAllText(sourcePath, string.Join('\n', lines));
    }

    /// <summary>
    /// Replaces everything after the directive's colon, or appends one if the line has never
    /// been annotated. Idempotent: annotating an already-annotated line reproduces it.
    /// <para>
    /// The first colon after the <c>#</c> is always the right one, because no expression can
    /// contain a colon — it appears only in <c>let</c>, in an assignment and in a directive.
    /// </para>
    /// </summary>
    private static string Annotated(string line, string text)
    {
        var hash = line.IndexOf('#');
        var colon = hash < 0 ? -1 : line.IndexOf(':', hash);
        var head = colon >= 0 ? line[..(colon + 1)] : line.TrimEnd() + ":";
        return $"{head} {text}";
    }
}
