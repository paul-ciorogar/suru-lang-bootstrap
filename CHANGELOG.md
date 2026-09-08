# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed — the test project mirrors `src/`
- `tests/Suru.Tests` was a flat directory of eighteen files; it now has a folder per project, and inside the compiler's, a folder per source folder. `Compiler/` holds `Lex/`, `Parse/`, `Semantic/`, `Debug/` and `Testing/` — the same names `src/Suru.Compiler` uses, with `ScopeStackTests` at its root where `ScopeStack.cs` sits at the project root. `Lib/` holds the `Suru.Lib` tests, and `Integration/` the ones that compile, link and run a real binary, which belong to no single source file. Namespaces follow the folders
- The shared helpers — `Source`, `CompiledFixtures`, `Executable` — stay at the root in `Suru.Tests`. Every nested namespace encloses it, so nothing needed a new `using`: the move is folders and `namespace` lines and nothing else, and all 292 tests pass unchanged
- `TestModeTests` is the one file placed by what it tests rather than by how it runs: it drives real binaries but it is the `Testing/` feature's end-to-end case, so it sits there and joins the `"Integration"` collection from across the namespace, which xUnit names globally

### Added — `Suru.Lib`
- A fifth project, `src/Suru.Lib`, for code with no stage of the pipeline to belong to. It depends on nothing — not LLVM, not the compiler — and both `Suru.Compiler` and `Suru.CLI` reference it, which is the whole reason it is not a folder inside `Suru.Compiler`: the CLI should be able to use a shared type without the compiler being the thing that defines it
- `Result<T, S>`, an interface over `Ok<T, S>` and `Error<T, S>`. `Map` / `MapError` change one side and carry the other through untouched; `AndThen` is `Map` for a step that can fail too, and is what keeps a chain flat rather than nesting a result inside a result. `Result.Ok` / `Result.Error` are static entry points purely so both type arguments infer
- `Match` is the one both halves are named in, and everything else could be written in terms of it. The rest are comforts on top: `IsOk` / `IsError`, `TryGetValue` / `TryGetError` for the C# `out` idiom, and `UnwrapOr` / `UnwrapOrElse` / `ErrorOr`. **Every way back to a bare value takes a fallback** — there is deliberately no throwing `Unwrap()`, because a `Result` whose failure case can be skipped with one word is an exception with extra steps
- **Nothing uses it yet.** `CompilationResult` is the obvious first caller and is deliberately left alone: it carries an error *list* rather than one failure, and folding that in is a change to what the compiler reports, not to how it is spelled

### Changed — the test channel, end to end
- **`suru test` reports over a unix socket, and stdout is only the program's own output again.** Everything built over the last three changes is now wired together: codegen emits frames instead of `printf` records, `cc` links `runtime/suru_rt.c` into every test build, and `TestChannel` listens on the other end. The `\x1e`-on-stdout channel is **deleted, with no fallback** — `RecordProtocol`, `RecordReader` and `TestRecord` are gone. Two writers that both work is two writers that drift, and sieving records back out of a stream the user also writes to was only ever going to hold while Suru had no strings
- The reason to spend a runtime on this is not clean stdout, which was cheap. It is that a socket **reads as well as writes**, which is what lets one linked binary be driven through many test cases without a `cc` per case. That is Phase B; this is the road to it, and it ends at exactly the behaviour it started with on a transport that can go further
- `CodeGenerator` declares `suru_frame_begin` / `suru_field` / `suru_frame_end` **in `BuildMode.Test` only** — the thing the mode was threaded in for — and `WriteRecord` became `WriteFrame`: begin, a `suru_field` per keyed field, end. A production module's function list is unchanged. `Render` is untouched, so `printLn` and a `#view` still cannot disagree about what a value looks like; what changed is who consumes the format string. An assert's outcome now ships as the word `pass` or `fail`, picked at runtime the way a `bool` already picks `true` or `false`, rather than as a `%d` the reader would have to know meant something
- The emitted program announces `run-started` at the top of its body and `run-finished` before its `ret`, and **that is what stops `undefined` being inferred from absence.** A directive that reported nothing after a finish is `undefined` as before — the run reached the end and never took that branch. A directive that reported nothing because no finish ever came is a run that died on the way, and its line is **blanked** rather than annotated: a stale value from an earlier run is a fresh-looking lie, and `undefined` would be the same lie in the compiler's own words. `TestResult` grows `Crashed` and `Unreported`, kept apart from `Undefined` so the two claims never blend
- **Two deadlines, not one**, because "the binary never connected" and "the program hung" are different failures with different causes: `--connect-timeout` (200 ms) and `--timeout` (1000 ms), both on `suru test` and both rejected on `suru build` rather than accepted and ignored. The run deadline is generous on purpose — an integration test is allowed to take a second — and in Phase B it becomes per request rather than per process
- Every channel failure is **its own sentence**: never connected, connect timed out, run timed out, ended mid-frame, an unreadable frame, an `overflow`, an assert outcome that is neither word. The failure this exists to prevent is the shim's own default — records dropped silently, every directive annotated `undefined`, nothing said about why — so a run that hits one of these leaves the source file **unwritten** rather than filling it with disclaimers
- The socket and the child's stdout are drained **concurrently**. Reading one to the end before starting the other deadlocks the moment the other fills at ~64 KB, and this is the first time the compiler has had two pipes to lose that race with. The socket path is kept short and checked against a conservative bound, because `sun_path` is 108 bytes and the shim's own over-length guard is a silent return — which would surface as "never connected" and send you looking at the linker
- `TestModeTests` and the `directives` fixture pass **unchanged**, which was the point of splitting the reader from the reporter three changes ago. Had either needed editing, the seam was not where it was supposed to be. Still unexercised: the deadline and crash paths, which need a program that can hang or die mid-run — Suru has neither a loop nor a way to block, so those want small C children and are deferred with the rest of the failure fixtures

### Added — the test channel's program side
- `runtime/suru_rt.c`: **the compiler acquires a runtime.** A C translation unit linked into test builds and absent from production ones, holding everything about the channel that the emitted program must not know — a constructor that connects to `SURU_TEST_CHANNEL`, a destructor that closes, and `suru_frame_begin` / `suru_field` / `suru_frame_end`, which accumulate one frame and send it. It is the third implementation of `doc/test-protocol.md` and is written from that document rather than from the C#
- **With no `SURU_TEST_CHANNEL` in its environment every one of those is a no-op.** A test binary run by hand still runs and simply says nothing, which is what keeps the shim from being a thing you have to arrange in order to execute what you just built
- A frame accumulates whole because `Content-Length` has to precede its body, and a fixed buffer can be too small. **An overflowed frame is replaced, never truncated:** a fifth event `kind`, `overflow`, carries the `key` of the field that did not fit, and the payload is dropped. A frame shorter than its own declared length is precisely what framing exists to prevent — a reader that met one would not know where the next frame began and could only stop — so one frame admitting defeat is cheaper than a channel that can no longer be read
- The shim ignores `SIGPIPE` at startup. A harness that has gone away must not kill the child with a signal the driver would then have to read as a crash; `MSG_NOSIGNAL` is not portable off Linux and `SO_NOSIGPIPE` not portable onto it, and this translation unit exists only in test builds
- `RuntimeShim` locates the file — beside the assembly first, then up the ancestors so a compiler run out of the source tree works — and names the failure. A shim that did not ship is a broken installation and says so, rather than being reported as `cc` complaining about a path to nothing. It ships as **source** rather than as an object file: the second compile costs ~50 ms and nobody has to decide who builds the `.o`, or for which target
- **Nothing calls any of it yet.** Codegen still emits `printf` records on stdout and the driver still reads stdout to EOF; `RuntimeShimTests` asserts only that the file shipped and that it still exports the three names, which is the half of this that can break with no C# changing. The C is first exercised when codegen emits calls to it and the driver reads the other end

### Added — the test channel's wire format
- `Frame`, `FrameWriter` and `FrameReader` in `Suru.Compiler.Testing`: an LSP-shaped `Content-Length` header over a body of `key:byte-length:bytes\n` fields, with `kind` always first. **Nothing emits or reads one yet** — records still ride on stdout behind a `\x1e` prefix. The format is built before the socket that will carry it and the C shim that will write it because it is provable on its own: `FrameTests` needs no LLVM, no `cc` and no process, and a grammar that is wrong is far cheaper to find here than inside a diff that also introduces C and a socket
- **A field's trailing `\n` is a delimiter to check, not one to scan for.** A reader that has consumed a field's declared byte count and does not then find it knows the length was wrong and says so. The alternative — reading a shorter value and then reading the tail of it as the next field — is a bad length silently reinterpreting the rest of the frame, which is the failure framing exists to prevent and the one that yields a plausible wrong answer instead of an error. For the same reason a malformed frame throws `FrameException` and is never resynchronised past: after a disagreement the reader no longer knows where anything starts
- Values are measured rather than delimited, so one can hold a `\n` or a `:` and needs no escape — `1e+20` today, a string literal later. That is also **why not JSON**: nothing has to write JSON in C, where the third implementation of this format lives, and raw bytes need no quoting rules for two writers to agree on
- `Content-Length` is not redundant beside the per-field lengths. It lets a reader take a whole frame off the socket before parsing any of it — which makes a read boundary landing mid-frame a non-event rather than a special case in every field — and lets it skip a frame whose `kind` it does not recognise, which is how the event set grows without a flag day. Skipping is the consumer's decision: `FrameReader` yields every well-formed frame and knows no kinds at all
- `FrameReader` is a streaming cursor, since a socket read ends where the kernel says: chunks go in, whole frames come out, and `AtFrameBoundary` reports whether a partial one is held — at end of stream the difference between a program that stopped talking and one that died mid-sentence
- The four events the channel will carry: `run-started`, `view`, `assert` and `run-finished`. `run-finished` is the one that earns the exercise on the reading side — `undefined` is inferred today from the *absence* of a record, which cannot tell an untaken branch from a process that died before reaching it
- `doc/test-protocol.md`, the spec the C shim will be written from rather than from the C#, with a pointer from `doc/testing.md` and a row under "Elsewhere in this repository" in `doc/README.md` — it is a contributor document, since nothing in it is written in a `.suru` file

### Changed — seams for the test channel
- The reading half of a test run split out of `TestRun` into `RecordReader`, with a `TestRecord` struct — kind, id, values — between them. `TestRun` is now only the reporting half: it matches records to directives and knows nothing about how one travelled. The encoding itself moved to `RecordProtocol`, the one place in the compiler that spells a separator, and both codegen and the reader ask it rather than writing `\x1e` themselves
- Codegen's `WriteRecord` split from `Printf`. `#view` and `#assert` write records, `printLn` writes output, and the two are only accidentally the same `printf` call today — the emitted IR is byte for byte what it was. When the records move to a channel of their own, `printLn` is not touched
- `CodeGenerator.Generate` takes the `BuildMode`. Nothing reads it yet: the directives are already gone from a production module by the time codegen runs, but the test channel's runtime declarations must appear in a test module and only there, and that is a decision this stage has to be able to make for itself

### Added — `if`, `else` and `else if`
- `if <condition> <block>`, with an optional `else` — the language's first control flow. The condition must already be a `bool`: nothing converts implicitly, so a number is not a truth value and `if 1 { }` is `'if' cannot branch on a value of type 'i64'; expected 'bool'`. A comparison already yields a `bool`, which is what makes it the usual way to write one
- **`else if` is not a form of its own.** `else` is followed by a block *or by another `if`*, and the second spelling is the whole of what `else if` is. It costs zero cases past the parser — every later stage just calls its existing statement walker — and it is what makes a trailing `else` belong to the nearest `if`: the chain is nested, not flat. The alternative, desugaring it into `else { if … }`, would invent a scope and a node at a position no `{` occupies
- Both arms are always braced. There is no single-statement form, so there is no dangling `else` to have a rule about, and an empty arm is legal and does nothing
- An arm **is** a block, so it is a scope and needed no rule of its own: shadowing, bindings dying at the `}`, and assignment to an outer binding surviving the branch all fall out of the block that was already there. Two arms may bind the same name because neither can see the other's
- The condition needs no terminator and gets no special whitespace rule: `{` is not a binary operator, so the existing left-to-right fold stops at it by itself. That means the ordinary line-continuation rule applies to a condition exactly as it does to a `let` initialiser, and `else` need not share a line with the `}` before it
- The first codegen to emit more than one basic block: `if.then`, an `if.else` only when there is one, and an `if.end` that exists in both shapes because it is where execution continues rather than where two live paths merge. The enclosing function is read back from `_builder.InsertBlock.Parent` rather than held in a field, after the condition is emitted, so it stays right the day an expression can move the insert point. Both arms branch to the end unconditionally — nothing can leave one early, since there is no `return`, no `break` and no diverging call, which is the assumption to revisit when one exists
- The first piece of `undefined` **a directive the run never reached annotates `undefined`**, which a branch is the first thing able to cause. An unreached `#assert` is a third outcome — counted on its own, no terminal diagnostic, exit code unaffected — because an assertion in a branch this run did not take is honest rather than broken, and counting it as a pass would be the dishonest option. The mechanism knows nothing about branches (every collected directive starts out `undefined` and a record overwrites it), so it is already the right answer for an unreached `#view-step-N` and an unmocked parameter
- `doc/control-flow.md`, and the fixture `tests/fixtures/if` — it prints 1 to 10 in order, and only in order if every edge lands in the block it should, with a `bool` printed inside a branch and again after it to prove the interned `true`/`false` global is reachable from either

### Changed — `if`, `else` and `else if`
- Stack slots are **hoisted**: `main` is now an `entry` block holding nothing but `alloca`s and a `body` block holding the code, with a second builder parked at `entry` for the whole run. Two blocks rather than one because only that guarantees nothing gets in ahead of the alloca point once a branch terminates a block. The `store` stays where the binding sits, so initialisation is still ordered — it is the slot that belongs to the frame, not the value
- `Parser.ParseBlock` returns `BlockStatement` rather than `Statement`, so `IfStatement.Then` can be typed as the block the grammar already guarantees it is
- `TestRun.CollectDirectives` walks an `if`'s arms. Without it a `#view` or `#assert` in a branch emitted its record at runtime, missed the id lookup, and was **silently dropped** — a failing assertion inside a branch would have been neither annotated, nor counted, nor reported
- `TestResult` gained `Undefined`, and the `suru test` summary a fourth figure: `5 passed, 1 failed, 2 undefined, 6 views written to …`
- Four documentation pages claimed Suru had no control flow, and `doc/blocks.md` said there was nothing yet for a block to be the body of

### Added — reactive test directives and `suru test`
- `#` test directives, the first step towards Suru's interactive ambitions: a test lives in the program it tests rather than in a file somewhere else. `#mock <name>: <value>` stubs a binding, `#view <expression>:` prints a value **into your own source line**, and `#assert(<actual>, <expected>)` compares two values and records the outcome in its line too. `#view` and `#assert` share one rule — everything after the colon on a directive line belongs to the compiler, and rewriting is idempotent, so an unchanged file is rewritten byte for byte
- `suru test <file.suru>`, a second CLI command beside `build`. It is a mode rather than a flag because `#view` and `#assert` observe values that exist only while the program is running: it compiles with the directives live, links, **runs the executable**, collects what the directives reported, annotates the source, and exits non-zero if any assertion failed. A failure is also printed as an ordinary positioned diagnostic — the annotation is for reading the code, the diagnostic and exit code are for the editor and for CI
- `BuildMode`, and the decision that a production build discards a `#` line **in the lexer**, on the same path as `//`. That is the strong form of "ignored in production": a directive written in syntax a given compiler build does not understand still cannot break a release build. The cost, taken deliberately, is that a mistyped directive is only ever reported by a test build
- `#mock x: v` is the assignment `x: v` restricted to a test build — it takes effect where it is written rather than replacing the binding's initialiser. That is what makes it free: no new scoping, no new codegen, and `AnalyzeStore` is shared with `AssignmentStatement` so the two cannot drift apart in what they accept or in what they say when they refuse. `#mock` is also the only directive that changes what the program computes, so a test build can pass on values production never produces
- `mock`, `view` and `assert` are recognised by the parser after a `#` rather than being keywords, so nothing is taken out of the namespace; a directive must be written on one line, since the expression-continuation rule has no business applying to something a production build treats as a comment
- A test build's executable reports each directive on stdout as a `\x1e`-prefixed record beside the program's own output. No new extern is needed, and there is no interleaving hazard while Suru has no strings: a `printLn` can only emit a number or `true`/`false` and cannot forge a record. That stops holding the day string literals land, which is when this should move to `fprintf(stderr, …)`

### Changed — reactive test directives and `suru test`
- `Tokens` gained `LastConsumed` and `SkipRestOfLine`. The latter discards source text without lexing it, which is what lets an already-annotated directive be read back: `fail, got 2` is not an expression, and a large `f64` prints as `1e+20`, which the literal scanner rejects. The parser stops at the colon with nothing buffered and throws the rest of the line away
- `Compiler.Compile` is a thin wrapper over a private `Build(buildDir, mode)` that returns the `Module` alongside the result, since a test run needs it to map a record back to the directive that asked for it
- Codegen's per-type printf specifier moved out of `EmitPrintLn` into `Render`, so `printLn` and the test records share one source of truth for what a value looks like
- `CompiledFixtures.GetTestRun` copies a fixture into the temp build root before running it — a test run rewrites the source it is given, and the file under version control must not be mutated. It runs twice, so the second run has to re-parse what the first one wrote

### Added — blocks and block scope
- `{}` is a fourth kind of statement: it runs the statements inside it in order and, more to the point, is a **scope**. A binding made in a block is visible until the closing `}` and no further, while everything already in scope stays readable and assignable from inside it. Blocks nest to any depth
- Shadowing: a `let` inside a block may reuse a name bound further out, and since it is a new binding rather than an assignment it declares its own type — an `i64` `x` can be shadowed by an `f64` one, with the outer binding untouched and back in scope at the `}`. Shadowing crosses nesting levels only; two `let`s of one name in the same block are still `'x' is already declared`, which is now a per-scope rather than a per-program rule
- `ScopeStack<T>`, one shared innermost-last stack of scopes, replaces the flat symbol table in *both* semantic analysis (keyed to types) and codegen (keyed to stack slots). Codegen needed the same treatment rather than just the analyzer: its table was keyed on the bare name, so a shadowing binding would otherwise have overwritten the outer slot and never given it back. `EnterNew`/`Exit` rather than push/pop — a caller enters a scope, it has nothing to hand in or take back out
- A block is purely lexical: no branch and no new LLVM basic block, so statements keep emitting straight-line into `main`'s single `entry` block and a shadowing binding is simply a second `alloca`. Hoisting allocas to the entry block still only becomes necessary when real control flow arrives
- A block is a statement, not an expression — it yields no value and cannot appear where one is expected. That, and the absence of anything that takes a block as a *body*, is the whole of what it does today; it lands now because control flow and user-defined functions both need this scope stack first
- `doc/blocks.md`, and the fixture `tests/fixtures/blocks` — eight nested scopes each shadowing the same name, printing on the way in and again on the way out, so every level is proved to keep its own slot

### Added — numeric literal separators and base prefixes
- Digit separators: `1_000_000`, and `1_000.000_1` on either side of a float's `.`. A `_` must sit between two digits, so `1_`, `1__0` and `0x_ff` are errors — the separator groups digits, it never stands in for one
- Base prefixes for integers: `0x` hexadecimal, `0b` binary and `0o` octal, each accepting separators too. The prefix must be lowercase — `0X`, `0B` and `0O` are errors (`base prefix 'X' must be lowercase`), so a literal has one spelling and `0o` never has to be told apart from `00`; the hexadecimal digits themselves may still be written in either case. A float is always decimal. Every base shares the one `i64` range, so `0xFFFFFFFFFFFFFFFF` is out of range rather than `-1` — there are no unsigned types to reinterpret it as
- Signed literals: a `-` directly in front of a number is part of the literal rather than the negation operator applied to it, so `-1` is the literal −1 and the `i64` minimum, −9223372036854775808, can be written at last. The fold happens only where an operand is expected, so `1 - 2` still subtracts and `-count` is still the operator
- A literal is now decoded by the lexer rather than the parser: only the lexer knows the base, and it has the line and column to report a bad digit against. `Token` carries the decoded `IntMagnitude`/`FloatValue` alongside the raw lexeme, which stays in `Text` so token dumps and diagnostics still show what was written. The magnitude is unsigned and the sign is applied by the parser, which is the only stage that can tell the negated minimum from an out-of-range positive

### Changed — numeric literal separators and base prefixes
- An integer literal out of range for `i64` is reported as `integer literal is out of range for 'i64'`, closing the documented gap where it crashed the compiler with an unhandled overflow. It comes from the lexer for a magnitude past 9223372036854775808 and from the parser for that magnitude written without its sign
- A letter, digit or `_` butted up against the end of a literal is a lex error (`invalid digit 'i' in decimal literal`) instead of lexing as a separate identifier. `1i64` used to reach semantic analysis as `1` and an unknown variable `i64`; the same rule is what keeps `0xffz` from splitting into `0xff` and `z`

### Added — binary expressions, bindings and assignment
- Operators: `+ - * / %`, the comparisons `= <> < <= > >=`, and the word operators `and`, `or`, `not`. Equality is a single `=`, not-equal is `<>`, and prefix `-` negates. Integer `/` is truncating division yielding an `i64`; `%` is its remainder
- **No operator precedence.** Every binary operator folds left to right, so `1 + 2 * 3` is `(1 + 2) * 3` — a Suru expression is a recipe, a list of steps in order, not a formula to untangle. Parentheses now group an expression and are the only way to say otherwise
- Line continuation falls out of the same rule: an expression continues while the next token is a binary operator, wherever it sits, so a line beginning with an operator joins the line above. No newline tracking is needed in the lexer, since no statement can begin with a binary operator
- Bindings: `let <name> <type>: <expression>`, with the type written out — never inferred. Assignment is `<name>: <expression>`. The parser tells an assignment from a call by the `:` that follows the name, using the existing one-token lookahead
- Semantic analysis gains a symbol table (one flat scope — there are no blocks yet) and the diagnostics for it: `unknown type`, `already declared`, `cannot bind a value of type`, `cannot assign a value of type`, `unknown variable`, and `operator '<op>' cannot be applied to` for both binary and unary operands. No type converts implicitly, so both operands of an operator must already agree
- Codegen gains stack slots: a binding is an `alloca` plus a `store`, a use is a `load`. `and`/`or` evaluate both sides — no expression can have a side effect yet, so short-circuiting waits for user-defined functions
- `doc/expressions.md` and `doc/bindings.md`, and the fixture `tests/fixtures/expressions` covering the lot end to end

### Changed — binary expressions, bindings and assignment
- A bare identifier is now an expression (a variable use), so `printLn 1` parses as two statements instead of failing with "expected LeftParen"; the missing parentheses are reported by semantic analysis instead. A call is still an identifier followed by `(`
- A lone `/` is division rather than a lex error; `//` is still a comment
- `PrintTests`' process runner moved to a shared `Executable.Run`, now that a second integration test needs it

### Added — line comments
- `//` starts a comment that runs to the end of the line. It is the only comment syntax: there is no block comment, and a lone `/` is still an unexpected character
- The lexer treats a comment as whitespace and never produces a token for it, so the parser is unchanged. The terminating newline is left for the whitespace path, keeping line and column numbers correct after a comment
- `doc/program-structure.md` documents the syntax and drops the "no comment syntax yet" note

### Added — language documentation
- `doc/` — a language reference written for people writing `.suru` programs, kept strictly to what the compiler implements: `doc/README.md` (entry page, first program, table of contents), `doc/program-structure.md` (statements, no terminator, insignificant whitespace, error format), `doc/literals-and-types.md` (`bool`, `i64`, `f64`, `void` and literal syntax) and `doc/printing.md` (`printLn` and each of its diagnostics). Every example and every quoted diagnostic was produced by compiling and running the snippet
- Compiler internals stay in `README.md` and `CLAUDE.md`; `doc/` links out to them rather than repeating them

### Added — stage dumps and IR verification
- Stage dumps, the compiler equivalent of application logging: each stage can print its whole intermediate form, so a bug is found by diffing what the program looked like before and after a stage. Stages are `tokens`, `ast` (after parse), `typed-ast` (after semantic analysis) and `llvm`
- `suru build --dump=<stages>` (also `--dump-<stage>` and a bare `--dump` for all), plus the `SURU_DUMP` environment variable — always compiled in, off by default, so a release build can be asked for a dump without a rebuild. Dumps go to stderr, leaving stdout for the build result
- `DumpOptions` carries the enabled stages and the destination writer; the compiler never chooses a destination itself, so only the CLI prints. `DumpOptions.Off` is the default and makes every stage check a flag test
- `AstPrinter` renders a `Module` as an indented tree, optionally annotating each expression with its resolved type. Semantic analysis annotates in place, so the after-parse and after-analysis dumps diff line for line. The typed dump is written before the error check, so a failed analysis still shows how far typing got
- `TokenPrinter` lexes the source a second time rather than teeing the parser's cursor: lexing is context-free here, so the result is identical while the pull-based path stays untouched. A lex error is reported inline and ends the dump
- LLVM module verification after codegen, run unconditionally — malformed IR is now reported as an internal compiler error at the stage that produced it instead of crashing the emitted executable. The LLVM dump is written before verification, since a module that fails is the one worth reading
- `DumpTests` asserts the full text of every dump, and `DumpIntegrationTests` covers the LLVM dump and stage ordering over a real compile

### Changed — stage dumps and IR verification
- `Compiler` takes an optional `DumpOptions`; the existing single-argument constructor still means "no dumps"
- `CompiledFixtures.FindFixturePath` is now the public `CompiledFixtures.FixturePath`, for tests that drive the compiler with their own options

### Added — unit-level front-end tests
- `Source` test helper: `Parse`, `Analyze`, `Analyzed` and `SingleExpression` run lex → parse → analyze over in-memory source text with no file on disk, no LLVM and no C toolchain. The source path is the constant `test.suru`, so diagnostics are asserted in full
- `LexerTests`, `ParserTests` and `SemanticTests` — token shapes, AST shapes and positions, type annotations, and every diagnostic, all in-process
- `LexException`, thrown by the lexer with the standard `file(line,col): message` prefix

### Fixed — unit-level front-end tests
- An unexpected character crashed the CLI with a stack trace: the lexer threw a bare `Exception` that the driver did not catch. It now throws `LexException` and the driver reports it as a compile error

### Changed — unit-level front-end tests
- Semantic diagnostics moved from compiled fixtures to `SemanticTests` over source text; fixtures are reserved for cases that genuinely need files on disk. Removed `SemanticErrorTests` and the `unknown-function`, `wrong-arity`, `unprintable-argument` and `non-call-statement` fixtures. `tests/fixtures/print` remains as the end-to-end codegen → link → run check

### Added
- `SuruType` — a named type representation (`bool`, `i64`, `f64`, `void`) shared by the semantic analyzer and codegen
- `SourcePosition` on every AST node, so errors after parsing can point at a line and column
- Semantic analysis: resolves `printLn` as a builtin and reports unknown functions, wrong arity, unprintable argument types, and non-call statements — all errors in one run, formatted as `file(line,col): message`
- Negative-compilation test path: `CompiledFixtures.GetErrors(name)` compiles a fixture expected to fail; `SemanticErrorTests` covers each diagnostic
- Fixtures `unknown-function`, `wrong-arity`, `unprintable-argument`, `non-call-statement`

### Changed
- `Parser.Parse` takes a `Lexer` instead of a `Tokens` cursor and owns the cursor itself, so tokens are pulled on demand while parsing; `Tokens` is now internal and `Lexer` carries the source path
- `Tokens` lookahead rewritten as a queue of pending tokens; previously `Peek` re-buffered the current token, which made the following `Next` a no-op

- Codegen emits from an expression's resolved type rather than its AST node class, and now interns global string constants
- Codegen throws `CodegenException` instead of silently skipping constructs it does not recognize; the driver reports it as an internal compiler error
- `CodeGenerator` follows the static-entry/private-instance convention used by the other stages

### Added (earlier)
- Integration test infrastructure: `CompiledFixtures` shared fixture compiles `.suru` files from `tests/fixtures/` into temp binaries; `PrintTests` runs the compiled binary and asserts stdout
- Test fixture `tests/fixtures/print/main.suru` covering `bool`, `i64`, and `f64` output via `printLn`
- Replaced placeholder `UnitTest1` with real integration tests

### Added (previous)
- Lexer: identifiers, `true`/`false` keywords, integer literals (i64), float literals (f64), `(`, `)`, `,`
- AST nodes: `BoolLitExpr`, `IntLitExpr`, `FloatLitExpr`, `CallExpr`, `ExprStmt`; `Module` now holds a statement list
- Parser: recursive descent; parses top-level call expressions without a statement terminator
- Built-in `printLn` compiles to a `printf` call; supports `bool`, `i64`, and `f64` arguments

## [0.1.0] - 2026-04-12

### Added
- Initial solution structure with four projects: `Suru.Compiler`, `Suru.CLI`, `Suru.LSP`, `Suru.Tests`
- `LLVMSharp 20.1.2` dependency in `Suru.Compiler`
- All projects target `net10.0`
- Full compiler pipeline: lexer → parser → semantic analysis → LLVM codegen → native executable
- `suru build <file.suru>` CLI command; output placed in `build/` next to the source file
- Empty `.suru` file compiles to a native executable with `main()` returning 0
