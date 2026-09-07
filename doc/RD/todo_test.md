# Test channel: a linked shim over a framed socket

The chosen combination out of [rd_test.md](rd_test.md): **transport 1.3** — a C runtime shim
linked into test builds only, talking over a unix socket — with **protocol 2.3** — length-framed
structured messages, *not* JSON.

The reason to spend a runtime on this is not clean stdout. Clean stdout is 1.2 and it is cheap.
The reason is that a socket **reads as well as writes**: one linked binary can be driven through
many test cases without a recompile per case, which is the whole difference between a test loop
that costs a `cc` invocation per run and one that costs a round trip. That is Phase B below, and
everything in Phase A is the road to it.

## Decisions

**Portability: POSIX only, and said out loud.** `AF_UNIX` on both sides. `suru build` is
unaffected — the shim exists only in test builds — so the only claim being narrowed is that
`suru test` needs a POSIX host with a `cc`, which it already needed for the `cc`. The driver
keeps the endpoint behind a seam so a TCP-on-loopback path can be added without touching the
protocol, but no such path is written now, and a bare `AF_UNIX` shim is ~80 lines rather than two
wire paths to keep honest.

**Two deadlines, not one.** "The binary never connected" and "the program hung" are different
failures with different causes — a shim that failed to link versus a loop that never ended — and
collapsing them into one timeout means every diagnostic has to hedge. So:

| | default | flag | means |
|---|---|---|---|
| connect deadline | 200 ms | `--connect-timeout=<ms>` | the child started but never reached the channel |
| run deadline | 1000 ms | `--timeout=<ms>` | the child connected and then did not finish |

The run deadline is generous on purpose and configurable because an integration test is allowed
to take a second or several. In Phase B the run deadline becomes **per request** rather than per
process, with the same flag and the same default — one slow case does not spend the budget of
the case after it.

**One directive per line, enforced.** A line carries at most one `#view`, `#assert` or `#mock`,
and a second one on the same line is a parse error rather than a thing that happens to work.
That keeps `doc/testing.md`'s central promise — *the colon and everything after it belong to the
compiler* — literally true, with the newline as the terminator and nothing else. The alternative,
ending an annotation at the next `#`, makes `#` a character the compiler must never emit in a
value, and that constraint gets harder rather than easier: it is fine while every value is a
number or `true`/`false`, and it is a trap the day string literals land. A record's destination
is also, again, exactly a line.

**Records stop riding on stdout entirely.** No fallback path. A binary with no
`SURU_TEST_CHANNEL` in its environment drops its records, and the driver — which always sets it —
treats a missing connection as a run failure. Two writers that both work is two writers that
drift.

---

## The wire format

A frame is an LSP-shaped header block followed by a body of length-prefixed fields:

```
Content-Length: 33\r\n
\r\n
kind:4:view
id:1:0
value:2:42
```

Each field is

```
<key> ":" <byte-length> ":" <bytes> "\n"
```

- `key` matches `[a-z][a-z0-9-]*`.
- `byte-length` is ASCII decimal, counting **bytes**, not characters.
- The trailing `\n` is a **checkable delimiter, not a separator**. A reader that has consumed
  `byte-length` bytes and does not then find a `\n` knows the length was wrong, and says so. The
  alternative — a format where a bad length silently reinterprets the rest of the frame — is the
  failure mode framing exists to prevent.
- `kind` is always the first field of a frame.

**Why not JSON.** Values ship as raw bytes, so `1e+20`, embedded newlines, and eventually string
literals need no escaping and no quoting rules. More to the point, nothing has to write JSON *in
C*: `suru_field` is `vsnprintf` into a buffer and three `memcpy`s. A JSON writer in the shim is
real C to maintain forever, and [rd_test.md §2.3](rd_test.md) already names it as the cost that
buys least.

**Why `Content-Length` on top of already-measured fields.** It is not redundant for the reader:
it lets the driver take an entire frame off the socket before parsing any of it, which is what
makes a partial read at a buffer boundary a non-event, and it lets a reader skip a frame whose
`kind` it does not recognise — which is how the event set grows without a flag day.

### Events, program → harness

| `kind` | fields | |
|---|---|---|
| `run-started` | `run` | one per execution of the program body |
| `view` | `id`, `value` | what a `#view` saw |
| `assert` | `id`, `outcome` (`pass`/`fail`), `actual`, `expected` | outcome computed in the emitted code, not by comparing text |
| `run-finished` | `run`, `exit` | the body ran to the end |

`run-finished` is the point of the exercise on the reading side. Today `undefined` is inferred
from the *absence* of a record ([TestRun.cs:23-29](src/Suru.Compiler/Testing/TestRun.cs)), which
cannot tell "the branch was not taken" from "the process died before it got there". With a
run-finished event, absence-after-finish is `undefined` and absence-without-finish is a crash,
and they stop sharing an annotation.

Harness → program is Phase B. Nothing in Phase A ever writes to the socket.

---

## Phase A — one-shot over the socket

Ends at exact parity with today's behaviour, on the new transport and the new protocol.

### 0. Seams first — no behaviour change

Straight out of [rd_test.md §0](rd_test.md). Doing these first is what makes every later task a
small diff; doing them later means doing them inside a diff that is already large.

- [x] **Split `WriteRecord` from `Printf`** *(~1h)* —
      [CodeGenerator.cs](src/Suru.Compiler/Codegen/CodeGenerator.cs). `EmitView` and `EmitAssert`
      call `WriteRecord`; `EmitPrintLn` keeps `Printf`. Both still emit the same `printf` call
      today. *Done when:* the emitted IR is byte-identical and `dotnet test` is untouched.
      *Done:* `WriteRecord(kind, id, params (format, argument)[])` — the field list Phase A.3
      turns into begin/field×N/end. `--dump-llvm` on the `directives` fixture diffs clean
      against the previous commit in both modes.

- [x] **Split `TestRun.Report` in two** *(~2h)* —
      [TestRun.cs](src/Suru.Compiler/Testing/TestRun.cs). One half turns bytes into a sequence of
      records, the other turns records into annotations and diagnostics, with a
      `TestRecord` struct (kind, id, values) between them. *Done when:* no code outside the
      reading half mentions `\x1e`, and the existing tests pass unchanged.
      *Done:* `RecordReader` (bytes → records + the program's own output), `TestRecord` the
      struct between them, `TestRun` the reporting half. The encoding sits in
      `RecordProtocol` — the reader and codegen both ask it, and neither spells a separator.

- [x] ~~**Re-key `annotations` on `SourcePosition`**~~ — *superseded by the one-directive-per-line
      rule.* Built, and being partly taken back out by the task below. The `SourcePosition` key
      stays: it costs nothing, and it is the right key once a record carries its own position.
      What comes out is everything that made two directives on a line *work*.

- [x] **Enforce one directive per line** *(~2h)* —
      [Parser.cs](src/Suru.Compiler/Parse/Parser.cs), [Lexer.cs](src/Suru.Compiler/Lex/Lexer.cs).
      Two places see the second `#`, and neither may pass it silently:

      - `Lexer.SkipAnnotation` currently stops at `\n` **or** `#`. Keep the scan — it is what
        makes the second directive visible at all — but when it stops at a `#`, that is the
        error, not a resumption point. Reverting it outright to `SkipRestOfLine` would swallow
        `#view b:` in `#view a: #view b:` with no diagnostic, which is worse than either rule.
      - `Parser.SkipAnnotation(hash)` (Parser.cs:177) returns without consuming anything when a
        directive has no annotation yet, so `#assert(a, 6) #view b:` reaches the statement loop
        with a `#` still on `hash`'s line. Compare lines there and reject.

      Diagnostic, positioned at the *second* `#` and worded like the neighbouring parse errors:
      `main.suru(8,10): only one directive may appear on a line`.
      *Done when:* both spellings are rejected, and the message is asserted whole in
      `ParserTests` — the source path is `Source.Path` there, so `Assert.Equal` on the string
      works.
      *Done:* `Parser.RequireLineToItself(hash)`, called at the end of `ParseDirective` so
      both spellings pass through it. The cases live in `DirectiveTests` beside the other
      directive diagnostics rather than in `ParserTests`.

- [x] **Take the shared-line evidence back out** *(~1h)* — delete `tests/fixtures/shared-line/`
      and `tests/Suru.Tests/SharedLineTests.cs`, both of which assert the behaviour now being
      forbidden. Replace them with two `ParserTests` cases for the diagnostic above; a rejected
      program needs no fixture, no `cc` and no executable. `Lexer.SkipAnnotation`'s doc comment
      loses the paragraph about two directives sharing a line and gains the reason it still
      stops at `#`: to *find* the error, not to permit it.

- [x] **Thread `BuildMode` into `CodeGenerator.Generate`** *(~1h)* — codegen has no idea which
      mode it is in, and the shim declaration must not appear in a production module.
      `Compiler.Build` already has the mode in hand. *Done when:* a production module's function
      list is unchanged and a test module's is too, because nothing uses the mode yet.

### 1. The protocol, both sides, before any socket

Provable entirely in unit tests: no LLVM, no `cc`, no process.

- [x] **Write the spec** *(~1h)* — `doc/test-protocol.md`, with the field grammar, the event
      table, the delimiter rule, and the "why not JSON" and "why Content-Length" paragraphs from
      above. A row in `doc/README.md`.
      *Done:* under "Elsewhere in this repository" rather than in the language-reference table —
      nothing in it is written in a `.suru` file — with a pointer from `doc/testing.md`. It is
      the document the C shim is written from, rather than the C#.

- [x] **`FrameWriter` and `FrameReader`** *(~2h)* — `src/Suru.Compiler/Testing/`. The reader is a
      streaming cursor: fed arbitrary chunks, it yields whole frames. `FrameWriter` exists for
      the tests and for Phase B's request direction; the shim writes its own frames in C.
      *Done:* plus `Frame` (the struct between the wire and its consumer, carrying the four
      event names), `FrameProtocol` (the header and delimiters, the one place both halves ask)
      and `FrameException`. `AtFrameBoundary` on the reader is what §4 needs to tell a clean end
      of stream from a truncated one. **Public, not internal**, unlike `RecordReader`: there is
      no `InternalsVisibleTo`, and a format with three implementations is a contract rather than
      an implementation detail — Phase B hands it to an LSP.

- [x] **Frame reader tests** *(~1.5h)* — `tests/Suru.Tests/FrameTests.cs`: a frame arriving in one
      chunk; the same frame arriving one byte at a time; two frames in one chunk; a value
      containing `\n`; a length one byte short (must fail loudly, not resync); a missing header;
      a frame whose `kind` is unknown (must be skipped, not fatal).
      *Done:* 15 cases, and the skip is asserted at the layer that owns it — the reader yields
      the unknown frame intact and reads the known one after it, since which kinds exist is the
      consumer's business. Every diagnostic is asserted whole. Nothing calls the new code yet,
      so the load-bearing check is that the other 262 tests were not touched.

### 2. The shim

- [x] **Write `runtime/suru_rt.c`** *(~2h)* — a constructor that reads `SURU_TEST_CHANNEL` and
      connects, a destructor that closes, and three exported functions:

      void suru_frame_begin(void);
      void suru_field(const char *key, const char *format, ...);
      void suru_frame_end(void);

      The frame accumulates in a fixed buffer because `Content-Length` has to precede the body.
      `suru_field` is `vsnprintf` into scratch, then key, length, `:`s, bytes and `\n` appended.
      *Overflow emits a frame saying so and drops the payload* — a truncated frame that still
      claims its length is the one thing the format cannot survive. With no channel every
      function is a no-op return, so a test binary run by hand still runs.
      *Done:* the overflow frame is a fifth `kind`, `overflow`, carrying the `key` that did not
      fit — added to `Frame` and to the spec's event table. The constructor also ignores
      `SIGPIPE`: a harness that has gone away must not kill the child with a signal the driver
      would read as a crash, and `MSG_NOSIGNAL` / `SO_NOSIGPIPE` are each unportable to where
      the other one lives. Untested on purpose — §5 is where the C is exercised — though its
      bytes were checked by hand against `FrameReader` and the first frame in
      `doc/test-protocol.md` is what it writes, byte for byte.

- [x] **Ship and locate the shim** *(~1h)* — a content item in `Suru.Compiler.csproj` copied to
      the output directory, found by walking up from `AppContext.BaseDirectory` the way
      [CompiledFixtures.FixturePath](tests/Suru.Tests/CompiledFixtures.cs) already walks up for
      fixtures. A missing shim is a named compiler error, not a link failure quoting `cc`.
      *Done:* `RuntimeShim.Locate` and `RuntimeShim.MissingMessage`, checked beside the assembly
      first and up the ancestors after, so a compiler run out of the source tree works.
      `RuntimeShimTests` asserts it from the *test* assembly's output directory, which is what
      proves the content item copies through a project reference — the half of this that can
      break with no C# changing at all.

### 3. Codegen and link

- [x] **Declare and call the shim** *(~2h)* — the three functions declared in `BuildMode.Test`
      only; `WriteRecord` becomes begin/field×N/end. `Render` stays exactly as it is — the format
      strings do not change, only who consumes them. *Done when:* `--dump-llvm` on a test build
      shows no `printf` for a directive, and a production build's IR is unchanged.
      *Done:* `WriteFrame(kind, (key, format, argument)…)` — keyed, because the wire is. The
      `kind` field is written here rather than by the shim, which has no idea what a kind is.
      `run-started` at the top of `body` and `run-finished` before the `ret`, both carrying
      constants in Phase A. An assert's outcome ships as `pass`/`fail`, selected the way
      `Render` already selects `true`/`false`. A test module's only `printf` call is
      `printLn`'s; a production module declares none of the three.

- [x] **Link the runtime on test builds** *(~1h)* — `Compiler.Link` takes the mode and appends the
      `.c` to the `cc` invocation. Passing the source rather than a prebuilt `.o` costs ~50 ms and
      removes the question of who builds the `.o` and for which target.
      *Done:* `Link` takes the shim's **path**, and `Build` locates it and raises
      `RuntimeShim.MissingMessage` before the link step — inside `Link` it could only come back
      as `Link failed: Test runtime not found: …`, which is the framing that message exists to
      avoid.

### 4. The driver

- [x] **Listen, hand over the path, accept** *(~2h)* — `Compiler.Run` creates a
      `UnixDomainSocketEndPoint` under `Path.GetTempPath()`, `Listen(1)`, starts `AcceptAsync`
      **before** `Process.Start`, sets `SURU_TEST_CHANNEL` in the child's environment, and applies
      the connect deadline to the accept. Socket file removed on the way out, on every path.
      *Done:* `TestChannel`, a file of its own rather than more of `Compiler`. The connect
      deadline is armed *after* `Process.Start`, so 200 ms means 200 ms of the child's life.
      `AcceptAsync(CancellationToken)` rather than `.WaitAsync(ct)`, which would abandon a task
      that then faults unobserved on dispose. The path is checked against a conservative bound
      first: `sun_path` is 108 bytes and the shim's over-length guard is a *silent* return.

- [x] **Drain both channels concurrently** *(~2h)* — the socket and the child's stdout are read at
      the same time; a sequential `ReadToEnd` on one deadlocks the child once the other pipe fills
      at ~64 KB, and this is the first time the compiler has had two pipes to lose that race with.
      The run deadline covers the whole read; on expiry the child is killed.
      *Done:* one `Task.WhenAll` over the socket read, `ReadToEndAsync` and `WaitForExitAsync`,
      all three started before any is awaited. **Untested** — the `directives` fixture writes
      one line of stdout, so only a program that floods it can find this, and that needs a C
      child. It is the first thing §5 should build.

- [x] **Name every failure** *(~1.5h)* — never connected, connect timed out, run timed out,
      truncated frame, unparseable frame: each a distinct `TestResult` error with its own
      sentence. The failure this task exists to prevent is the shim's own default — records
      dropped silently, every directive annotated `undefined`, and nothing said about why. Add
      `--timeout` and `--connect-timeout` to the CLI and to the usage text.
      *Done:* `TestChannelException`, passed through by `Compiler.Test` verbatim — these
      sentences are written to be read and a `Could not run …:` prefix would only get in the
      way. Nine of them, including an `overflow` frame and an assert outcome that is neither
      word. A run that throws one leaves the source file **unwritten**, since `TestRun.Report`
      is never reached. Both flags are rejected on `suru build` rather than ignored.

- [x] **Consume `run-started` / `run-finished`** *(~1.5h)* — `undefined` stops being inferred from
      absence. A directive not reported *before a run-finished* is `undefined` as it is today; a
      directive not reported because no run-finished ever came is a crashed run, reported as one.
      `TestResult` grows the distinction; the CLI prints it.
      *Done:* `TestResult.Crashed` and `Unreported`, kept apart from `Undefined`. A crashed
      run's unanswered lines are **blanked** rather than annotated — a stale value is a
      fresh-looking lie and `undefined` would be the same lie in the compiler's own words — and
      blanking is idempotent, so a rerun reproduces it. **Untested** for the same reason as
      above: no Suru program can die mid-run.

### 5. Tests and docs

- [x] **Prove the seams held** *(~1.5h)* — `TestModeTests.cs` and the `directives` fixture pass
      with no edits. If either needed changing, the record writer and reader were not the seam
      they were supposed to be, and that is worth knowing before the next transport question.
      *Done:* they did, both untouched, all 279 tests green on the new transport.

- [ ] **Fixtures for the new failures** *(~1.5h)* — a program that hangs (run deadline, killed,
      diagnostic); a program that exits mid-run (no run-finished, crash reported, directives
      before the exit still annotated).
      **Neither can be a `.suru` fixture.** Suru has no loop and no way to block, so no program
      it can express hangs, and nothing in it can exit mid-run — a divide by zero is the closest
      and it traps on x86 while yielding 0 on ARM64, i.e. a fixture that passes on one machine.
      So these want small **C children** driven against `TestChannel` directly, compiled at test
      time against `runtime/suru_rt.c` (which `RuntimeShim.Locate` already finds) — also the
      first thing that exercises the shim at all. Worth building: hangs; never connects;
      connects then stalls; exits mid-run; sends garbage; and **floods stdout while framing**,
      which is the only test that can catch the two-pipe deadlock.

- [ ] **Documentation** *(~1h)* — `doc/testing.md` gains the channel and the two flags;
      `CHANGELOG.md` under `## [Unreleased]`; `CLAUDE.md`'s pipeline description gains the
      runtime and the socket; [rd_test.md](rd_test.md)'s work-item list gets its 1.3 and 2.3 boxes
      ticked with a pointer here.
      *Partly done with §3/§4:* `CHANGELOG.md`, `CLAUDE.md` and `doc/test-protocol.md`'s status
      paragraph, all of which had become *false* and could not be left standing. Still open:
      `doc/testing.md`'s "How it runs" section, which still describes the `\x1e` stdout channel,
      and `rd_test.md`'s boxes.

---

## Phase B — one binary, many runs

The reason for all of the above. The binary stops being a program that runs once and becomes a
server for its own body: the shim owns `main`, waits for a request frame, calls the body, reports,
and waits again. Recompiling per test case stops being a thing that happens.

Two new events, harness → program: `run` (execute the body) and `stop` (exit). Mocks travelling
the same way — the harness answering `#mock` per run rather than the value being baked in at
codegen — is the case after this one, and is what turns the deferred `#spec` design in
[todo.md](todo.md) into something buildable.

- [ ] **Emit the body as `suru_run()`** *(~2h)* — codegen in test mode emits the program body as
      `void suru_run(void)` instead of `main`, and declares nothing else new. Production builds
      keep emitting `main` exactly as today. The alloca/body block split is unchanged; it just
      belongs to a different function.

- [ ] **The shim's `main` becomes a loop** *(~2h)* — `runtime/suru_rt.c` gains a frame *reader*,
      a `main` that connects, then loops reading requests: on `run`, emit `run-started`, call
      `suru_run()`, emit `run-finished`; on `stop` or EOF, return. The reader is the same grammar
      as the writer, read the other way; it is the largest piece of C in the project and should
      stay under ~150 lines.

- [ ] **Per-request deadlines in the driver** *(~2h)* — the run deadline moves from "the process"
      to "this request", so one slow case does not consume the next one's budget. A request that
      times out kills the process and fails only the case it was running, since a killed process
      cannot be asked for another.

- [ ] **A session API** *(~2h)* — `Compiler.Test` gains a form that builds once, opens the
      channel, and runs the body N times, returning a result per run. This is the API an LSP
      calls, and the first place the compiler holds a live child across calls.

- [ ] **A fixture that proves it** *(~1.5h)* — one link, several runs, asserting that `cc` ran
      exactly once and that each run reported independently. Without this assertion the feature is
      indistinguishable from Phase A with extra steps.

---

**Phase A: 18 items, ~26 h** — 16 done. What is left is §5's two: the failure fixtures, which
need C children because no Suru program can hang or die mid-run, and the rest of the
documentation. The channel itself is end to end. Standalone — it ends at today's behaviour on
tomorrow's transport, which is the only honest place to stop.
**Phase B: 5 items, ~9.5 h.** The payoff.
