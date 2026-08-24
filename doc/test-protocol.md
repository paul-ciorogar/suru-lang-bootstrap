# The test channel protocol

This is a **contributor document**, not a language one. Nothing here is written in a `.suru`
file — this is the wire between a binary the compiler built and the compiler that is watching
it run. If you are looking for `#mock`, `#view` and `#assert`, that is [Testing](testing.md).

A test build reports what its directives saw. Those reports have to leave a running process and
get back into your source file, and this is the format they travel in. It is specified here
because it has **three implementations**: the writer and reader in
`src/Suru.Compiler/Testing/`, and — once it lands — the C shim linked into test builds, which is
written from this document rather than from the C# .

> **Status.** The format is built and tested; nothing emits or reads one yet. Records still ride
> on stdout behind a `\x1e` prefix while the shim, the socket and the driver are built around
> this. The plan is [todo_test.md](../todo_test.md); the survey it came out of is
> [rd_test.md](../rd_test.md).

## A frame

An LSP-shaped header block, then a body of length-prefixed fields:

```
Content-Length: 30\r\n
\r\n
kind:4:view
id:1:0
value:2:42
```

That is one frame, 52 bytes, and the `\n` at the end of `value:2:42` is part of it.

## The grammar

```
frame  = "Content-Length: " length "\r\n\r\n" body
body   = field*
field  = key ":" length ":" bytes "\n"
key    = [a-z][a-z0-9-]*
length = [0-9]+          ; ASCII decimal
```

- **`length` counts bytes, not characters.** Both of them: the header's counts the body, and a
  field's counts its value.
- **`kind` is always the first field.** A body whose first field is anything else is malformed,
  not a frame with a missing kind.
- **The trailing `\n` is a checkable delimiter, not a separator.** A reader that has consumed a
  field's declared byte count and does not then find a `\n` **knows the length was wrong, and
  says so**. This is the whole point. A format where a bad length silently reinterprets the rest
  of the frame — reading a shorter value and then finding a "field" that is really the tail of
  the last one — is precisely the failure framing exists to prevent, and it is the one that
  produces a plausible wrong answer rather than an error.
- **A malformed frame is fatal, never resynchronised past.** Once a length has disagreed with the
  bytes around it the reader no longer knows where anything begins, and guessing is worse than
  stopping.
- **A value may contain anything**, `\n` and `:` included, because it is measured rather than
  scanned for. This is what line framing cannot do, and it is why `1e+20` needs no escape today
  and a string literal will need none later.

## The events

Program → harness. Every frame carries `kind` plus the fields in its row.

| `kind` | fields | |
| --- | --- | --- |
| `run-started` | `run` | one execution of the program body began |
| `view` | `id`, `value` | what a `#view` saw |
| `assert` | `id`, `outcome` (`pass`/`fail`), `actual`, `expected` | what an `#assert` decided |
| `run-finished` | `run`, `exit` | the body ran to the end |

`id` is the per-module number the parser assigns each `#view` and `#assert` in source order,
which is how the driver finds the line to annotate.

An `assert`'s **`outcome` is computed in the emitted code**, not by comparing `actual` against
`expected` here. Both are rendered text by the time they reach the wire, and `f64` equality
decided by comparing two `printf`-rendered strings is not the equality the program computed.

`run-finished` is what tells a run that ended apart from a run that died. Today `undefined` is
inferred from the *absence* of a record, which cannot distinguish "the branch was not taken"
from "the process never got there". With this event, absence-after-finished is `undefined` and
absence-without-finished is a crash.

Harness → program — a request to run the body, so one linked binary can serve many test cases —
is Phase B, and adds `kind`s rather than changing anything above.

## Why not JSON

Because values ship as **raw bytes**: `1e+20`, an embedded newline and eventually a string
literal need no escaping, no quoting rules, and no agreement about which of them is in force.

And because of where the writer has to live. Nothing above has to write JSON *in C*: a field is
a `vsnprintf` into scratch and three `memcpy`s. A JSON writer in the shim is real C to maintain
forever, and [rd_test.md §2.3](../rd_test.md) already names it as the cost that buys least — the
alternative there, "a separator-delimited payload with a length prefix", is exactly this format.

## Why `Content-Length` on top of already-measured fields

It looks redundant and is not, for the reader:

- It lets the driver take an **entire frame off the socket before parsing any of it**, which is
  what makes a read boundary landing mid-frame a non-event rather than a special case in every
  field.
- It lets a reader **skip a frame whose `kind` it does not recognise**, which is how the event
  set grows without a flag day. A reader that had to parse a frame to know its length could not
  skip one it did not understand.

## Where it lives

| | |
| --- | --- |
| [`Frame`](../src/Suru.Compiler/Testing/Frame.cs) | a frame in hand: its kind, its fields, and the event names |
| [`FrameProtocol`](../src/Suru.Compiler/Testing/FrameProtocol.cs) | the header, the delimiters — the one place both halves ask |
| [`FrameWriter`](../src/Suru.Compiler/Testing/FrameWriter.cs) | frame → bytes |
| [`FrameReader`](../src/Suru.Compiler/Testing/FrameReader.cs) | bytes → frames, streaming; throws `FrameException` |
| [`FrameTests`](../tests/Suru.Tests/FrameTests.cs) | the grammar above, asserted byte for byte |

`FrameReader` is a cursor rather than a parser over a buffer, because a socket read ends wherever
the kernel says it does: chunks go in as they arrive, whole frames come out, and
`AtFrameBoundary` says whether a partial one is being held — which at end of stream is the
difference between a program that stopped talking and a program that died mid-sentence.
