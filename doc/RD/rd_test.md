# R&D: Where Test Records Travel

Suru's tests live inside the program they test. `#view` and `#assert` are compiled
into the binary, and what they saw has to get back out of a running process and
into the source file. Today that happens through `printf` on stdout: codegen
emits one line per directive, prefixed with a byte no Suru program can produce,
and `TestRun` sieves those lines back out of the program's own output.

```
\x1esuru\x1eview\x1e0\x1e42\n
\x1esuru\x1eassert\x1e1\x1e0\x1e40\x1e12\n
```

That was the right first answer, and its reasoning is written down at
[TestRecord.cs:8-13](src/Suru.Compiler/Testing/TestRecord.cs) — it needs no extern
beyond the `printf` codegen already declares, and it cannot be forged because a
Suru program has no strings and can only ever print a number or `true`/`false`.
Both halves of that argument are dated. The forgery half expires the day string
literals land. The stdout half is already visible: a program's output and the
compiler's telemetry arrive on one wire, interleaved, and `TestRun` un-interleaves
them by prefix scan.

This note surveys where else the records could travel. It separates two questions
that are almost always conflated:

- **Transport** — which channel carries a record from the running program to
  whoever is reading. This is the "keep stdout clean" question.
- **Protocol** — what a record actually says. This is the "carry line and column"
  question, and the "be useful to an LSP" question.

They are independent. Every transport below works with every protocol below, and
either can be changed without touching the other. The one thing they share is the
seam: [CodeGenerator.Printf](src/Suru.Compiler/Codegen/CodeGenerator.cs) on the
writing side, and the `stdout.Split('\n')` loop in
[TestRun.Report](src/Suru.Compiler/Testing/TestRun.cs) on the reading side.

Two facts constrain everything that follows, and both are worth stating before the
options rather than inside them.

**The emitted binary's entire extern surface is `printf`.** All of it, at
[CodeGenerator.cs:38-41](src/Suru.Compiler/Codegen/CodeGenerator.cs):

```csharp
// Declare printf: i32 (ptr, ...)
var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
_printfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [ptrType], true);
_printfFn = _llvmModule.AddFunction("printf", _printfType);
```

There is no `extern` syntax in the language, no pointer type, no struct, no
`usize`, no strings. `socket(2)` and `connect(2)` cannot be called from Suru code,
and `struct sockaddr_un` cannot be spelled. Per [rd_ffi.md](rd_ffi.md), `extern`
sits behind structs, `usize` and pointer types in the planned ordering.

**.NET's `Process` exposes three streams and no more.**
[Compiler.Run](src/Suru.Compiler/Compiler.cs) starts the executable with
`RedirectStandardOutput = true` and reads it to EOF. There is no supported way to
hand the child an inherited fd 3; that needs a `posix_spawn` P/Invoke with a
`file_actions` list, or a launcher process. stderr is the only additional channel
that comes for free.

---

## 1. Transports

### 1.1 stdout, today

The baseline. One `printf` call per directive, one line per record, framed by
`\n` and identified by the `\x1e suru \x1e` prefix.

```csharp
private static string Record(string kind, int id, params string[] formats) =>
    TestRecord.Prefix + string.Join(TestRecord.Separator, [kind, id.ToString(), .. formats]) + "\n";

private void EmitView(ViewDirective view)
{
    var (format, argument) = Render(EmitExpr(view.Subject), TypeOf(view.Subject));
    Printf(Record(TestRecord.View, view.Id, format), argument);
}
```

**What this costs:**

- **The program's output is not the program's output.** `TestResult.Output` is
  stdout minus whatever looked like a record. A program that printed a record's
  bytes would have them silently eaten, and today only the absence of strings
  prevents that.
- **The forgery guarantee has a fixed expiry.** String literals, `printLn` of a
  string, or any escape syntax ends it. At that point the prefix is a convention,
  not a guarantee, and the failure is silent — a swallowed line, not an error.
- **Framing is by newline.** A record can never contain one. That is free today
  because every value renders as a token without whitespace; it stops being free
  for a multi-line `#view` of a large value, which
  [Parser.RequireOneLine](src/Suru.Compiler/Parse/Parser.cs) already anticipates.
- **It is one-way and batched.** `ReadToEnd` then `WaitForExit` means nothing is
  observable until the process ends. A program that hangs reports nothing at all,
  not even the directives it already passed.
- **Nothing to build.** It works, it is portable, it has no dependency beyond
  libc, and it is the only option here that is already finished.

### 1.2 stderr

The smallest possible step off stdout. `fprintf(stderr, …)` instead of
`printf(…)`, which is what `TestRecord`'s own comment nominates as the successor.

Codegen declares one more function and one external global — `stderr` is a
`FILE *` variable in libc, not a function, so it needs `AddGlobal` and a load at
each call site:

```csharp
var filePtr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

_stderrGlobal = _llvmModule.AddGlobal(filePtr, "stderr");
_stderrGlobal.Linkage = LLVMLinkage.LLVMExternalLinkage;

_fprintfType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [filePtr, ptrType], true);
_fprintfFn = _llvmModule.AddFunction("fprintf", _fprintfType);

// at each record site
var stream = _builder.BuildLoad2(filePtr, _stderrGlobal, "");
_builder.BuildCall2(_fprintfType, _fprintfFn, [stream, String(format), .. values], "");
```

`dprintf(2, fmt, …)` is the variant that avoids the global entirely — an ordinary
varargs function taking an `int` fd, so the declaration is `printf`'s with one
leading `i32` and the call site needs no load. It is POSIX 2008, not C, and absent
on Windows.

The driver side is one flag and one more reader:

```csharp
RedirectStandardOutput = true,
RedirectStandardError = true,
```

Both streams must then be drained concurrently — `ReadToEnd` on one while the
other fills its pipe buffer deadlocks the child at around 64 KB.

**What this costs:**

- **stdout becomes clean, exactly.** The program's output is passed through
  untouched, never scanned, never sieved. This is the whole of what the "clean
  output" requirement asks for.
- **The forgery question moves rather than closing.** Once Suru can write to
  stderr itself, a program can forge a record there too. The separation is
  conventional, not enforced.
- **`stderr` is not portable as a symbol.** glibc and musl export it as a global;
  the MSVC runtime does not, exposing `__acrt_iob_func(2)` instead. `dprintf` has
  the mirror problem. Either choice pins the record channel to POSIX, which the
  compiler is not otherwise pinned to.
- **stderr is now a shared channel.** The program's own diagnostics, when it grows
  a way to write them, and libc's own messages land there too. The prefix sieve
  does not go away; it moves to a quieter wire.
- **Still one-way, still batched, still newline-framed.** Nothing about the shape
  of the conversation changes.

### 1.3 A linked C runtime shim

Linking already shells out to `cc`
([Compiler.Link](src/Suru.Compiler/Compiler.cs)), which means a C translation unit
can join the binary without the language gaining a single feature. The compiler
emits calls to one symbol; everything about the channel lives in C.

```c
/* suru_rt.c — linked into every test build, absent from production builds. */
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>

static int chan = -1;

__attribute__((constructor))
static void suru_rt_open(void)
{
    const char *path = getenv("SURU_TEST_CHANNEL");
    if (!path) return;                      /* no harness listening; records are dropped */

    struct sockaddr_un addr = {0};
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof addr.sun_path - 1);

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd >= 0 && connect(fd, (struct sockaddr *)&addr, sizeof addr) == 0)
        chan = fd;
    else if (fd >= 0)
        close(fd);
}

__attribute__((destructor))
static void suru_rt_close(void)
{
    if (chan >= 0) close(chan);
}

/* Same varargs shape as printf, so codegen's format strings are unchanged. */
void suru_record(const char *format, ...)
{
    if (chan < 0) return;
    va_list ap;
    va_start(ap, format);
    vdprintf(chan, format, ap);
    va_end(ap);
}
```

Codegen changes at exactly one place — the declaration, and the function the
existing `Printf` helper calls:

```csharp
_recordType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [ptrType], true);
_recordFn = _llvmModule.AddFunction("suru_record", _recordType);
```

and linking gains one argument:

```csharp
Arguments = mode == BuildMode.Test
    ? $"\"{objectPath}\" \"{RuntimePath}\" -o \"{executablePath}\""
    : $"\"{objectPath}\" -o \"{executablePath}\"",
```

The driver creates the listener before starting the child, passes the path in the
environment, and accepts one connection:

```csharp
var socketPath = Path.Combine(Path.GetTempPath(), $"suru-{Guid.NewGuid():N}.sock");
using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
listener.Listen(1);

var accepted = listener.AcceptAsync();
using var process = Process.Start(/* … with SURU_TEST_CHANNEL = socketPath … */)!;
using var channel = await accepted;
```

**What this costs:**

- **This is the option that actually delivers a socket, and it is available now.**
  Nothing in the language changes. The FFI ordering in [rd_ffi.md](rd_ffi.md) is
  not a prerequisite, because no Suru code ever touches the socket.
- **The compiler acquires a runtime.** A `.c` file that has to be found at build
  time, shipped with the compiler, kept in sync with the protocol on both sides,
  and compiled by whatever `cc` happens to be. That is a genuinely new kind of
  artefact for this project, and it is a one-way door: once there is a runtime,
  more things move into it.
- **A second compile on every test build.** Or a pre-built `suru_rt.o` per target,
  which is worse — it has to be produced somewhere.
- **The channel can be bidirectional.** A socket reads as well as writes, which is
  what makes `#mock` answerable by the harness rather than baked into the binary,
  and what makes streaming results possible before the process exits.
- **Failure has to be designed.** No listener, a refused connect, a harness that
  dies mid-run: the shim above drops records silently, which turns every directive
  `undefined` with no explanation. stdout has no equivalent failure mode.
- **The socket path is not portable.** `AF_UNIX` on Windows exists but is
  awkward; TCP on loopback is the fallback and brings a port to choose and an
  authentication question with it.
- **It sidesteps forgery permanently.** A Suru program cannot reach the channel
  even after strings land, because reaching it needs a symbol the language cannot
  name.

### 1.4 A socket called from Suru

The version where the emitted code opens the socket itself, with no C written by
hand — the records are produced by ordinary library code compiled from Suru
source.

```suru
// Not expressible today; every line needs something the language does not have.
extern socket(domain i32, type i32, protocol i32) i32
extern connect(fd i32, addr *SockAddrUn, len usize) i32
extern write(fd i32, buf *u8, count usize) isize

struct SockAddrUn {
    family u16
    path   [108]u8
}
```

**What this costs:**

- **It is blocked behind the whole FFI stack.** `extern` declarations, a raw
  pointer type, `usize`, C struct layout, fixed-size arrays, and `u8`/`u16`
  integer widths. [rd_ffi.md](rd_ffi.md) puts `extern` third in its ordering,
  after structs with a known layout and after pointer types.
- **It also needs strings and byte buffers.** Formatting a record without
  `printf` means the language can build text, which is a larger question than the
  channel it was meant to serve.
- **It is the same wire as 1.3.** The harness side is identical; only the
  authorship of the client moves. Nothing about the observable behaviour of a test
  run distinguishes the two.
- **It is a proof of the FFI, not a way to get one.** As a milestone it is
  excellent — a socket client written in Suru would exercise structs, pointers,
  externs and buffers at once. As a route to clean output it is the longest
  possible one.

### 1.5 No channel: run in-process

The option that dissolves the question. LLVM can execute the module in the
compiler's own address space through an ORC or MCJIT execution engine, and a
symbol the JIT cannot resolve can be bound to a managed delegate:

```csharp
using var engine = _llvmModule.CreateMCJITCompiler();
engine.AddGlobalMapping(_recordFn, Marshal.GetFunctionPointerForDelegate(onRecord));
engine.RunFunction(mainFn, []);
```

There is no process, no pipe, no socket, and no serialisation — `onRecord` is
called with the values while the program runs.

**What this costs:**

- **It is the shape an LSP wants.** No object file, no `cc`, no link, no spawn.
  The per-keystroke cost of a test run collapses to codegen plus execution, which
  is the difference between an interactive language server and a batch one.
- **The program runs inside the compiler.** A Suru program that faults takes the
  language server with it, and there is no isolation, no timeout, and no way to
  kill a loop once loops exist.
- **`printLn` needs somewhere to go too.** With no child process there is no
  stdout to capture; `printf` writes into the host's own. Capturing it means
  binding `printf` to a managed delegate as well, which is arguably cleaner than
  everything above — the program's output and its records become two callbacks,
  cleanly separated at the source with no sieve anywhere.
- **It is a second execution path to keep honest.** `suru test` on the command
  line and `suru test` inside the editor would agree only as far as they are
  tested to agree, and the JIT and the object-file paths differ in target triple,
  optimisation and ABI details.
- **It does not remove the need for the others.** A released binary still has to
  be runnable, and CI wants the linked path.

---

## 2. Protocols

### 2.1 Id-keyed — today

A record carries an integer assigned by the parser in source order
([Parser.cs](src/Suru.Compiler/Parse/Parser.cs) increments `_directives` as it
builds each `ViewDirective` and `AssertDirective`). The reader resolves it against
a dictionary walked out of the AST:

```csharp
var directives = new Dictionary<int, Directive>();
CollectDirectives(module.Statements, directives);
…
if (fields.Length < 2 || !int.TryParse(fields[1], out var id)
    || !directives.TryGetValue(id, out var directive))
    continue;
…
annotations[directive.Position.Line] = value;
```

**What this costs:**

- **The wire is minimal.** Two fields of overhead. Position is resolved by the one
  party that certainly knows it.
- **The reader must hold the AST that produced the binary.** That is exactly true
  for `Compiler.Test`, which is why `Build` returns the module alongside the
  result. It is false for anyone else — a CI parser, a watch process, an editor
  reading a run it did not start.
- **A missed directive is silent.** `CollectDirectives` recurses through
  `BlockStatement` and both arms of `IfStatement`; anything it does not know about
  produces records that fail the lookup and are dropped at `continue`. Its own
  doc comment names this as the failure mode it exists to avoid — and every new
  statement form is a new chance to reintroduce it.
- **Annotations collide on a line.** `annotations` is `Dictionary<int, string>`
  keyed by `Position.Line`, so two directives on one line resolve to one entry.
  `RequireOneLine` keeps a directive from spanning lines, but nothing keeps two
  directives from sharing one.

### 2.2 Position-tagged

The idea already sitting as a TODO in
[TestRecord.cs:43-44](src/Suru.Compiler/Testing/TestRecord.cs) — put the
destination in the record:

```
\x1esuru\x1eview\x1e5\x1e42\x1e40\n     kind, line, column, value
```

`SourcePosition` already carries both numbers, and both are already known at
codegen time, so `Record` gains two arguments and nothing else moves:

```csharp
private static string Record(string kind, SourcePosition at, params string[] formats) =>
    TestRecord.Prefix
    + string.Join(TestRecord.Separator,
        [kind, at.Line.ToString(), at.Column.ToString(), .. formats])
    + "\n";
```

**What this costs:**

- **The record becomes self-describing.** Any reader can annotate a file without
  parsing it. That is the property that matters for a consumer which is not the
  compiler that built the binary.
- **Two directives on a line stop colliding**, once `annotations` is rekeyed on
  `SourcePosition` rather than `int`.
- **The position is stale by construction.** It describes the source as it was
  when the binary was built. In an editor the buffer has moved on, so the LSP has
  to map it forward anyway — which is precisely what it does with every other
  diagnostic, and precisely what the id would have avoided.
- **It still does not carry a replacement range.** `SourcePosition` is a start
  point with no end and no offset, so `Annotate` keeps finding the colon by
  `IndexOf` and rewriting the tail. A `TextEdit` needs a start *and* an end, which
  means the directive nodes need a span before an editor can apply a record
  directly.
- **Both keys can coexist.** Carrying the id as well costs one field and keeps the
  strict resolution for the compiler's own path while making the record usable
  without the AST.

### 2.3 Framed structured messages

The protocol an LSP would want if it were designed for one: length-prefixed
frames carrying typed events rather than a value per line.

```
Content-Length: 96\r\n
\r\n
{"kind":"view","id":0,"range":{"line":5,"col":40,"endCol":42},"type":"i64","value":"42"}
```

**What this costs:**

- **Framing stops depending on the value.** A length prefix carries newlines,
  separators, and eventually a multi-line `#view` of a large structure, none of
  which line framing survives.
- **Events, not just values.** `run-started`, `directive-reached`,
  `run-finished`, `panicked` — the difference between "these six directives
  reported and the rest are undefined" and knowing *why* the rest are undefined.
  Today `undefined` is inferred from absence
  ([TestRun.cs:23-29](src/Suru.Compiler/Testing/TestRun.cs)), which cannot
  distinguish "never reached" from "the process died first".
- **It presumes a transport with a shape.** Length-prefixed frames on stdout mean
  the program's own output can no longer share the channel at all — this protocol
  effectively selects 1.2 or later.
- **Typed values need a type system on the wire.** `%g` rounds, and the record
  currently ships rendered text precisely so the compiler never has to re-parse a
  value. A JSON `"value"` field either keeps that (a string, and the type beside
  it) or reopens the question `EmitAssert`'s comment closes by comparing in the
  emitted code rather than by comparing printed text.
- **It is the only option that supports a reply.** Pairing it with a
  bidirectional transport is what turns `#mock` from a compile-time substitution
  into something the harness can drive per run — the deferred `#spec` design in
  [todo.md](todo.md) is the case that would use it.
- **A JSON writer has to exist somewhere.** In C, in the shim, by hand — or the
  frames are emitted as a separator-delimited payload with a length prefix, which
  is most of the benefit for none of the cost.

---

## Comparison

### Transports

| | 1.1 stdout | 1.2 stderr | 1.3 C shim | 1.4 Suru socket | 1.5 in-process |
|---|---|---|---|---|---|
| New language features | none | none | none | `extern`, `*T`, `usize`, structs, strings | none |
| New externs in codegen | none | `fprintf` + `stderr` global | `suru_record` | many | `suru_record` (mapped) |
| Driver change | none | redirect + drain both | listener, env var, accept | same as 1.3 | replace `Run` with a JIT |
| Build inputs | `.o` | `.o` | `.o` + `suru_rt.c` | `.o` | none — no link at all |
| Program's stdout untouched | no | yes | yes | yes | needs `printf` bound too |
| Bidirectional | no | no | yes | yes | yes (a call, not a channel) |
| Results before exit | no | no | yes | yes | yes |
| Survives string literals | no | no | yes | yes | yes |
| Portability | anywhere | POSIX-shaped | POSIX + a `cc` at hand | POSIX | wherever LLVM JITs |
| Buildable today | done | yes | yes | no | yes |

### Protocols

| | 2.1 id-keyed | 2.2 position-tagged | 2.3 framed messages |
|---|---|---|---|
| Overhead per record | 2 fields | 3–4 fields | ~40 bytes of frame + JSON |
| Reader needs the AST | yes | no | no |
| Two directives on one line | collide | fine | fine |
| Carries a replacement range | no | not without a span | yes |
| Value can contain `\n` | no | no | yes |
| Distinguishes "unreached" from "died" | no | no | yes |
| Supports a reply | no | no | yes |
| Change is confined to | `Record` + lookup | `Record` + `annotations` key | both sides, plus a writer |

---

## Implications for Suru

1. **Transport and protocol move independently, and the seam already exists.**
   `Printf` is one method and the record loop is one `foreach`. Extracting a
   record-writer abstraction on the codegen side and a record-reader on the driver
   side is worth doing before choosing anything, because it is the change that
   makes all five transports interchangeable and costs nothing to keep.
2. **"Clean output" and "line and column" are separate purchases.** Clean output
   is 1.2, and it is small. Self-describing records are 2.2, and it is smaller.
   Neither requires the other, and neither requires a socket. Buying them together
   because they arrived in the same sentence overpays for both.
3. **A socket is reachable today only through 1.3.** The version the request
   describes — the program opening a socket — is 1.4, and it is behind the entire
   FFI ordering in [rd_ffi.md](rd_ffi.md). 1.3 delivers the identical wire, the
   identical harness, and the identical LSP story, at the price of the compiler
   owning a C runtime. That price is the real decision, and it is not a small one:
   a runtime is where features go to accumulate.
4. **The forgery argument has a deadline, and it is string literals.** Whatever
   the channel is by then, `TestRecord`'s doc comment stops being true. 1.3 and
   later close the question permanently; 1.1 and 1.2 only move it.
5. **`ReadToEnd`-then-`WaitForExit` is itself a design constraint.** Every
   transport that streams — anything that reports a directive before the process
   exits — requires `Run` to stop being a synchronous drain. That is a bigger
   change to `Compiler` than swapping a `printf` for an `fprintf`, and it is the
   thing loops will make urgent, because an infinite loop currently reports
   nothing at all.
6. **`undefined` is inferred, and only 2.3 fixes that.** Treating absence as the
   whole meaning of `undefined` is deliberate and documented, and it is correct
   for an unreached branch. It is wrong for a crash, and it will be wrong more
   often as the language grows ways to fail.
7. **The LSP question is about processes, not channels.** 1.5 answers it by
   removing the process; 1.3 answers it by making the process talk. They are
   different bets — isolation against latency — and the choice between them
   determines more about the language server than the record format does.
8. **Positions need spans before an editor can apply a record.** `SourcePosition`
   is a point. `Annotate` compensates by scanning for the colon, which works
   because no expression can contain one. An LSP `TextEdit` cannot compensate; it
   needs a range. Adding an end position to the directive nodes is a prerequisite
   for the LSP under any transport and any protocol.

---

## Work items

### 0. Shared — needed whichever way this goes

- [ ] Extract a record writer in codegen: give `CodeGenerator` a
      `WriteRecord(string format, params LLVMValueRef[])` distinct from `Printf`,
      so `EmitView`/`EmitAssert` no longer name the same function `EmitPrintLn`
      does. Behaviour identical; this is the seam, not a change.
- [ ] Extract a record reader in `TestRun`: split `Report` into "obtain records"
      and "turn records into annotations and diagnostics", with a `TestRecord`
      struct between them, so the parsing of a wire format is in one place.
- [ ] Re-key `annotations` on `SourcePosition` rather than `int` line, so two
      directives on one line stop colliding regardless of protocol.
- [ ] Add an end position to `ViewDirective`/`AssertDirective` (the parser knows
      where the directive ended; `RequireOneLine` is already watching), so a
      replacement range exists for later.
- [ ] Decide and write down what happens when the channel fails, whatever the
      channel is. Silent `undefined` for every directive is the default failure of
      1.2 onward and is worse than the problem being solved.

### 1.2 stderr

- [ ] Declare `fprintf` and the external `stderr` global in `CodeGenerator`'s
      constructor; add a `BuildLoad2` of the global at each record site.
- [ ] Point `WriteRecord` at `fprintf`; leave `Printf` on stdout.
- [ ] `Compiler.Run`: add `RedirectStandardError = true`, drain both streams
      concurrently (`Task` per stream, or async reads) — a sequential `ReadToEnd`
      on one deadlocks the child when the other pipe fills.
- [ ] `TestRun.Report`: take stdout and stderr separately; stop sieving stdout,
      parse records only from stderr, pass the program's output through verbatim.
- [ ] Choose `fprintf` + global against `dprintf(2, …)` and record why. The latter
      needs no global and no load; neither is portable to Windows.
- [ ] `TestModeTests` and `CompiledFixtures` should need no change — assert that,
      because it is the evidence the seam is in the right place.

### 1.3 C runtime shim

- [ ] Write `runtime/suru_rt.c` with `suru_record(const char *fmt, ...)`, a
      constructor that connects, and a destructor that closes.
- [ ] Decide how the shim is located and shipped: a content file copied to the
      output directory, resolved relative to `AppContext.BaseDirectory` the way
      `CompiledFixtures` locates fixtures.
- [ ] Declare `suru_record` in `CodeGenerator`; point `WriteRecord` at it. Emit
      the declaration only in `BuildMode.Test`.
- [ ] `Compiler.Link`: take the mode, append the runtime source or object in test
      builds only.
- [ ] `Compiler.Run`: create the `UnixDomainSocketEndPoint` listener, pass the
      path in the child's environment, `AcceptAsync` before `Process.Start`, read
      the channel to EOF concurrently with the child's stdout.
- [ ] Handle the child that never connects — timeout on accept, and report it as a
      run failure rather than as a file full of `undefined`.
- [ ] Decide the TCP-on-loopback fallback for platforms without usable `AF_UNIX`,
      or state that test mode is POSIX-only.

### 1.4 Socket from Suru

- [ ] Blocked. Prerequisites are [rd_ffi.md](rd_ffi.md)'s ordering through
      `extern` declarations, plus strings and byte buffers. Revisit as a proof of
      the FFI once those land — not as a way to reach a socket.

### 1.5 In-process execution

- [ ] Spike `CreateMCJITCompiler` on the existing module and run `main`, with
      `printf` and `suru_record` both bound to managed delegates via
      `AddGlobalMapping`.
- [ ] Establish what a faulting program does to the host process, and whether that
      is survivable for a language server.
- [ ] Keep the object-file path as the shipped one; treat the JIT as a second
      execution mode and test both against the same fixtures.

### 2.2 Position-tagged records

- [ ] Add `SourcePosition` to `Record`'s arguments; emit line and column.
- [ ] Keep the id as well, so the compiler's own path keeps its strict lookup and
      `CollectDirectives`' exhaustiveness stays checkable.
- [ ] Update `TestRun` to annotate from the record's own position and reconcile it
      against the directive's, so a mismatch is an internal error rather than a
      wrong line rewritten.
- [ ] Add a fixture with two directives on one line, which currently annotates
      only one of them.

### 2.3 Framed structured messages

- [ ] Specify the frame — length prefix and payload — and the event set:
      `run-started`, `view`, `assert`, `run-finished`. Write it in `todo.md`'s
      register, next to the deferred `#spec`/`#save` design.
- [ ] Decide where the payload is written. A JSON writer in the shim is real C to
      maintain; a length-prefixed separator-delimited payload keeps the existing
      `Record` shape and gets the framing benefit without one.
- [ ] Make `undefined` explicit: emit a run-finished event carrying which
      directives reported, so absence stops being the only evidence.
- [ ] Only after a bidirectional transport exists: specify the reply direction,
      and revisit `#mock` as something the harness answers per run.
