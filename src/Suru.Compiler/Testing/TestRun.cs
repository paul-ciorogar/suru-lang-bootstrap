using Suru.Compiler.Parse.Ast;

namespace Suru.Compiler.Testing;

/// <summary>
/// Turns what a test build reported back into source annotations and diagnostics: it
/// matches each <see cref="Frame"/> to the directive that emitted it and writes the
/// results into the file the directives came from.
/// <para>
/// The reporting half of a test run. Bytes become frames in <see cref="FrameReader"/> and
/// reach here through <see cref="TestChannel"/>; nothing here knows how a frame travelled,
/// which is what lets the channel change underneath it.
/// </para>
/// </summary>
internal static class TestRun
{
    /// <summary>
    /// Written back into a directive's line when the run finished without reaching it. Not part
    /// of the protocol — no frame ever carries it — because it means the absence of a frame:
    /// the directive was compiled and the program never got there.
    /// </summary>
    internal const string Undefined = "undefined";

    /// <summary>
    /// Written back instead when the run <i>did not</i> finish. A stale value from an earlier
    /// run is a fresh-looking lie about a run that never reached the line, and
    /// <see cref="Undefined"/> would be the same lie in the compiler's own words: it means
    /// "reached the end, never hit this", which is precisely what did not happen. Blanking says
    /// only that nothing is known, and re-annotating a blank line reproduces it.
    /// </summary>
    private const string Unknown = "";

    internal static TestResult Report(Module module, string sourcePath, ChannelRun run)
    {
        var directives = new Dictionary<int, Directive>();
        CollectDirectives(module.Statements, directives);

        // Keyed by position rather than by line, though a line carries one directive: the
        // column is what finds the '#' once a frame carries a position of its own, and the
        // key costs nothing until then.
        var annotations = new Dictionary<SourcePosition, string>();

        // Keyed by directive id rather than counted as frames arrive, because a loop is the
        // first construct that can report the same directive more than once: counting frames
        // would report a ten-iteration loop as ten passes with ten identical diagnostics.
        var viewed = new HashSet<int>();
        var outcomes = new Dictionary<int, bool>();
        var failureText = new Dictionary<int, string>();
        bool started = false, finished = false;

        var pending = new HashSet<int>(directives.Keys);

        foreach (var frame in run.Frames)
        {
            switch (frame.Kind)
            {
                case Frame.RunStarted:
                    started = true;
                    continue;
                case Frame.RunFinished:
                    finished = true;
                    continue;
                case Frame.Overflow:
                    // The one thing a directive must never be annotated for: the program had a
                    // value and could not send it. Writing 'undefined' here would be the
                    // compiler disclaiming knowledge it was explicitly told exists.
                    throw new TestChannelException(
                        $"The test channel overflowed on the field '{frame["key"]}': " +
                        "the value was dropped rather than sent short.");
                case Frame.View:
                case Frame.Assert:
                    break;
                default:
                    // A kind this build does not know is skipped, per the protocol — which is
                    // how the event set grows without a flag day. The reader yields it; whether
                    // it means anything is this consumer's business.
                    continue;
            }

            var directive = Directive(directives, frame);
            var position = directive.Position;
            var id = Id(frame);
            pending.Remove(id);

            if (frame.Kind == Frame.View)
            {
                // Last hit wins: a '#view' shows a value, and in a loop the last one is the
                // value the line ends up holding.
                annotations[position] = Field(frame, "value");
                viewed.Add(id);
                continue;
            }

            var outcome = Field(frame, "outcome");
            var actual = Field(frame, "actual");
            var expected = Field(frame, "expected");

            // Two words, agreed between the emitted code and here. A third means one of the
            // two is broken, and guessing which would hide it.
            var held = outcome switch
            {
                "pass" => true,
                "fail" => false,
                _ => throw new TestChannelException(
                    $"The test channel sent an assert with an outcome of '{outcome}', " +
                    "which is neither 'pass' nor 'fail'."),
            };

            // An assertion is sticky, where a '#view' is last-hit-wins: one that ever failed
            // reads 'fail', keeping the first failing iteration's value. Last-hit-wins here
            // would erase a failure on iteration 3 that happened to pass on iteration 10, and
            // an assertion that did not hold is not one that held.
            //
            // It is also the forward-compatible answer. The intended end state is fail-fast — a
            // failed assertion stops the loop and the rest of the run — under which the first
            // failure is the only one, and sticky and fail-fast agree on every program.
            outcomes[id] = outcomes.GetValueOrDefault(id, true) && held;

            if (held)
            {
                if (!failureText.ContainsKey(id))
                    annotations[position] = "pass";
            }
            else
            {
                if (failureText.TryAdd(
                        id,
                        $"{sourcePath}({position.Line},{position.Column}): " +
                        $"assert failed: expected {expected}, got {actual}"))
                    annotations[position] = $"fail, got {actual}";
            }
        }

        if (!started && !finished)
            throw new TestChannelException(
                "The program connected to the test channel and never began its body.");

        // A directive that reported nothing means two different things, and the run-finished
        // event is what tells them apart. After a finish it is 'undefined' — the branch was not
        // taken — and that needs no notion of a branch to say, so it will be the same answer
        // for an unreached '#view-step-N' or an unmocked parameter when those exist. Without a
        // finish the program died on the way, and nothing is known about the line at all.
        foreach (var id in pending)
            annotations[directives[id].Position] = finished ? Undefined : Unknown;

        Annotate(sourcePath, annotations);

        // Ordered by id, which is source order — the parser numbers directives as it meets
        // them. For straight-line and branching code that is also arrival order, so this
        // reproduces what appending as frames arrived produced; a loop is the only thing that
        // can revisit a directive, and reporting those in source order is the readable answer.
        var failures = failureText.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToList();

        return TestResult.Ran(
            run.Output, run.ExitCode, failures,
            passed: outcomes.Values.Count(held => held),
            views: viewed.Count,
            undefined: finished ? pending.Count : 0,
            crashed: !finished,
            unreported: finished ? 0 : pending.Count);
    }

    /// <summary>
    /// The directive a frame is about. A frame carries only an id, so one this does not find is
    /// a frame with nothing to annotate — which would go quiet rather than wrong, and so is
    /// worth saying out loud.
    /// </summary>
    private static Directive Directive(IReadOnlyDictionary<int, Directive> directives, Frame frame)
    {
        var id = Id(frame);

        return directives.TryGetValue(id, out var directive)
            ? directive
            : throw new TestChannelException(
                $"The test channel reported a '{frame.Kind}' for directive {id}, " +
                "which is not in this program.");
    }

    private static int Id(Frame frame) =>
        int.TryParse(Field(frame, "id"), out var id)
            ? id
            : throw new TestChannelException(
                $"The test channel sent a '{frame.Kind}' whose id is not a number: '{frame["id"]}'.");

    private static string Field(Frame frame, string key) =>
        frame[key]
            ?? throw new TestChannelException(
                $"The test channel sent a '{frame.Kind}' with no '{key}' field.");

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
            case WhileStatement loop:
                CollectDirectives(loop.Body, into);
                break;
        }
    }

    /// <summary>
    /// Writes each annotation into its directive's line, reading and writing the file once.
    /// Line endings are preserved by splitting on '\n' and leaving any '\r' where it was.
    /// <para>
    /// A line carries one directive, so a line is rewritten once and no annotation has to
    /// be placed around another.
    /// </para>
    /// </summary>
    private static void Annotate(
        string sourcePath, IReadOnlyDictionary<SourcePosition, string> annotations)
    {
        if (annotations.Count == 0)
            return;

        var lines = File.ReadAllText(sourcePath).Split('\n');

        foreach (var (position, text) in annotations)
        {
            var index = position.Line - 1;
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
    /// Replaces everything after the directive's colon, or appends one if it has never been
    /// annotated. Idempotent: annotating an already-annotated directive reproduces it.
    /// <para>
    /// The first colon after the <c>#</c> is always the right one, because no expression can
    /// contain a colon — it appears only in <c>let</c>, in an assignment and in a directive.
    /// </para>
    /// <para>
    /// Empty text leaves the colon bare rather than trailing a space after it, so a blanked
    /// line reproduces itself the next time round exactly as an annotated one does.
    /// </para>
    /// </summary>
    private static string Annotated(string line, string text)
    {
        var hash = line.IndexOf('#');
        var colon = hash < 0 ? -1 : line.IndexOf(':', hash);
        var head = colon >= 0 ? line[..(colon + 1)] : line.TrimEnd() + ":";
        return text.Length == 0 ? head : $"{head} {text}";
    }
}
