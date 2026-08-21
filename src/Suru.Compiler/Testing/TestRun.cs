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

        // Every directive starts out undefined, and a record overwrites it. That is the whole
        // of what 'undefined' means here — the directive was compiled and never reported —
        // so it needs no notion of a branch, and will be the same answer for an unreached
        // '#view-step-N' or an unmocked parameter when those exist.
        var pending = new HashSet<int>(directives.Keys);
        foreach (var directive in directives.Values)
            annotations[directive.Position.Line] = TestRecord.Undefined;

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
                pending.Remove(id);
                views++;
            }
            else if (fields is [TestRecord.Assert, _, var outcome, var actual, var expected])
            {
                var held = outcome == "1";
                annotations[directive.Position.Line] = held ? "pass" : $"fail, got {actual}";
                pending.Remove(id);

                if (held)
                    passed++;
                else
                    failures.Add(
                        $"{sourcePath}({directive.Position.Line},{directive.Position.Column}): " +
                        $"assert failed: expected {expected}, got {actual}");
            }
        }

        Annotate(sourcePath, annotations);

        return TestResult.Ran(
            string.Join('\n', output), exitCode, failures, passed, views, pending.Count);
    }

    private static void CollectDirectives(
        IReadOnlyList<Statement> statements, Dictionary<int, Directive> into)
    {
        foreach (var statement in statements)
            CollectDirectives(statement, into);
    }

    /// <summary>
    /// Finds every directive that can report, however deeply nested. A record carries only an
    /// id, so a directive this misses is a record with nothing to annotate: it is dropped at
    /// the lookup and the run goes quiet about it, which is the failure mode this has to be
    /// exhaustive to avoid.
    /// </summary>
    private static void CollectDirectives(Statement statement, Dictionary<int, Directive> into)
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
            case IfStatement branch:
                CollectDirectives(branch.Then, into);
                if (branch.Else is not null)
                    CollectDirectives(branch.Else, into);
                break;
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
