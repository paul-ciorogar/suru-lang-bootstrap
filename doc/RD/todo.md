# Reactive test directives

Suru wants programming in it to feel interactive: poke a value, see what it becomes, pin
the result — without leaving the file you are editing and without writing a test harness
that lives somewhere else.

The mechanism is a family of directives introduced by `#`. **A production build never sees
them. A test build executes them.** They let you stub an input, print a value back into
your own source, and assert on a result, all as ordinary lines of the program they are
testing.

```suru
let width  i64: 3
let height i64: 4
#mock width: 10                  // test builds only: width is 10 from here on
let area   i64: width * height
#view area: 40                   // ': 40' was written by the compiler
#assert(area, 40): pass          // ': pass' was written by the compiler
printLn(area)
```

```bash
suru build main.suru   # the '#' lines are not even lexed; prints 12
suru test  main.suru   # the '#' lines run; prints 40, annotates the file
```

---

## The contract

`#` runs to the end of the line, exactly like `//`.

In a **production build** the lexer skips a `#` line the way it skips a comment — the
parser is never told it existed. That is the strong form of "ignored in production": a
directive written in syntax this compiler build does not understand still cannot break a
release build. The price is that a typo in a directive is only ever reported by
`suru test`, which is the right trade for something that must never cost anything shipped.

In a **test build** the same line is lexed, parsed, typed and emitted like any other
statement. Directives are subject to every rule the rest of the language is: `#mock` on an
undeclared name is `unknown variable`, `#view` of a `void` is the same diagnostic
`printLn` would give, and a `#mock` inside a block sees that block's scope.

`view`, `mock` and `assert` are **not keywords**. They are ordinary identifiers that the
parser recognises after a `#`, so nothing is taken out of the namespace — `let view i64: 1`
stays legal.

---

## The three directives

| Directive | Reads | Writes |
| --- | --- | --- |
| `#mock <name>: <expr>` | an expression | the binding, at the point it is written |
| `#view <expr>:` | an expression | its own source line, after the `:` |
| `#assert(<expr>, <expr>):` | two expressions | its own source line, **and** the terminal on failure |

### `#mock` — stub an input

`#mock x: v` means the assignment `x: v`, emitted only in test builds. It is a statement,
not a change to the declaration: it takes effect *from the line it is written on*, and
everything above it still runs.

```suru
let seed i64: 7
printLn(seed)        // 7 — the mock is below this line
#mock seed: 1
printLn(seed)        // 1
```

Being an ordinary assignment gets scope for free:

```suru
let seed i64: 7
{
  #mock seed: 1
  printLn(seed)      // 1
}
printLn(seed)        // 1 — a block assigns the binding it can see, it does not copy it
```

It also gets the type rules for free. A mock must match the binding's declared type,
because it *is* an assignment to it:

```suru
let seed i64: 7
#mock seed: 1.5
```

```
error: main.suru(2,13): cannot assign a value of type 'f64' to 'seed' of type 'i64'
```

> **`#mock` is the one directive that changes what the program computes.** A test build
> can pass on values production never produces. `#view` and `#assert` only observe; `#mock`
> substitutes. That is the whole point of it, and the whole hazard of it.

### `#view` — see a value

`#view <expr>:` prints the value of an expression *into your source file*, after the colon.

```suru
let a i64: 6
let b i64: 7
#view a * b:
```

After `suru test`:

```suru
let a i64: 6
let b i64: 7
#view a * b: 42
```

It takes an **expression**, not just a variable name — `#view a * b:` costs nothing over
`#view a:` because the expression parser is already there.

The colon and everything after it belong to the compiler. Write `#view total:` and it fills
the value in; run it again and it overwrites the same place. A second `suru test` on an
unchanged file produces a byte-identical source.

`#view` prints the same way `printLn` does, so it can view exactly what `printLn` can
print: `bool`, `i64`, `f64`.

### `#assert` — pin a result

`#assert(<actual>, <expected>)` compares two expressions and records the outcome in the
line itself, the same way `#view` does:

```suru
#assert(area, 40): pass
#assert(area, 12): fail, got 40
```

A failure also reaches the terminal, positioned like every other Suru diagnostic, and makes
`suru test` exit non-zero:

```
main.suru(6,1): assert failed: expected 12, got 40
```

Two channels on purpose. The annotation is for **reading the code** — you open the file and
see at a glance which expectations hold. The diagnostic and exit code are for the **editor
and CI**, which should not have to diff your source to learn that a test failed.

Both sides must have the same type, drawn from `bool`, `i64` and `f64` — the same rule the
`=` operator already follows.

---

## How it runs

`suru test` is not a build flag, it is a *mode*, because `#view` and `#assert` observe
values that only exist while the program is running. The cycle is:

```
lex (# lines kept) → parse → analyse → codegen → link → RUN → collect → annotate → report
```

The emitted binary prints a record for every `#view` and `#assert` it reaches. Records ride
on stdout alongside the program's own output, marked with a `\x1e` prefix:

```
\x1esuru\x1eview\x1e0\x1e42
\x1esuru\x1eassert\x1e1\x1e0\x1e40\x1e12
```

Each carries the id the parser assigned to the directive, which the driver maps back to the
source position. There is no interleaving hazard today: Suru has no strings, so a user's
`printLn` can only ever emit a number or `true`/`false` and cannot forge a record. (Moving
records to `fprintf(stderr, …)` is the obvious later hardening, at the cost of a
platform-dependent extern.)

The driver splits the records out, annotates each directive's line once, writes the file
once, and reports.

### Re-reading an annotated line

This is the one genuinely fiddly part, and it is worth writing down because a naive
implementation passes every easy test and then falls over.

`#view x: 1e+20` and `#assert(x, 1): fail, got 2` have to parse *again* on the next run. The
tail is compiler output, so it must be **discarded, not parsed** — and it cannot even be
lexed. `fail, got 2` is not an expression, and `printf("%g")` renders a large `f64` as
`1e+20`, which the literal scanner rejects at `1e`.

So the parser stops at the colon and tells the cursor to throw the rest of the line away
without lexing it. That works because the parser is sitting exactly on the `:` with nothing
buffered ahead of it: `#view`'s expression stops there (`:` is not a binary operator, so the
fold loop ends), and `#assert` has just consumed its `)`.

*Rejected alternative:* spell the output `#view x => 42`, so a "raw tail after `=>`" rule
could live in the lexer with no knowledge of directive names. Cleaner separation of
concerns, but `:` is the right symbol — Suru already reads `name: value` as "this name gets
this value", and `#view total: 42` is the same sentence with the compiler filling in the
right-hand side.

---

## Not yet: `#view-step-N` and `undefined`

`#view` shows a value once. A value inside a loop is not one value, it is one per
iteration, and printing all of them into a source line is unreadable the moment the loop
runs more than a handful of times. `#view-step-N` picks one:

```suru
#view-step-3 total: 12
```

**`N` counts hits of the directive itself, not iterations of anything around it.** Each
`#view-step-N` line carries its own counter, incremented every time execution reaches that
line; on the hit where the counter equals `N`, that hit's value is the one written back.
Everything else is `#view`: it takes an expression, not just a name, and the compiler owns
everything after the colon.

```suru
let total i64: 0
loop item in items {
  total: total + item
  #view-step-1 total: 3
  #view-step-2 total: 7
  #view-step-3 total: undefined      // the line was only reached twice
}
```

Counting hits rather than iterations is what makes the directive local. It needs no notion
of "the enclosing loop", so it behaves identically inside a nested loop, inside a branch
that only sometimes runs, or inside a function body called from several places — in every
case `#view-step-3` means *the third time this line ran*, which is what you were counting
when you wrote it. It also means `#view-step-1 x:` and `#view x:` mean the same thing on a
line reached once.

A step that never happens is not an error. The directive was compiled, the program simply
never reached it that many times, and the honest thing to write back is `undefined`.

### `undefined`

> **Partly built.** One narrow piece of this landed with `if`: **a directive the run never
> reached annotates `undefined`**, and an unreached `#assert` is a third outcome — counted on
> its own, no terminal diagnostic, exit code unaffected. That refines the rule below rather
> than contradicting it: "`#assert` on `undefined` is a failure" is about asserting on an
> undefined *value*, which still cannot happen, not about an assert that never ran.
>
> The mechanism is deliberately ignorant of branches — every collected directive starts out
> `undefined` and a record overwrites it — so it is already the right answer for an unreached
> `#view-step-N` and for an unmocked parameter. What is still designed and not built is
> everything below: `undefined` as a *value*.

`undefined` is a **test-mode-only value that absorbs every operation it takes part in**.
Any operation with an `undefined` operand is `undefined`; it inhabits every type, so it
never provokes a type error of its own.

It exists because a test build is allowed to be partial. `#mock` stubs the inputs you care
about, and the ones you did not stub have no value to speak of — today that is impossible
to reach, but the moment a function can be entered with only some of its parameters
mocked it is the normal case:

```suru
#mock param1: 2                      // param2 was not mocked
#view param1 + param2: undefined
```

The alternatives are worse. Refusing to run until every input is mocked makes the
interactive loop — poke one value, look at the result — impossible. Defaulting the rest to
zero produces a number that looks like an answer and is not one.

Rules:

- **Poisoning is total.** `undefined + 1`, `undefined = 1` and `-undefined` are all
  `undefined`. In particular a comparison against `undefined` is *not* `false`; treating it
  as a `bool` is the mistake this whole design exists to prevent.
- **It never reaches production.** `undefined` is not spellable in source. It is produced
  only by an unmocked input or an unreached step, both of which are test-mode constructs,
  so `suru build` has nothing to represent.
- **`#view` prints it as `undefined`**, the same way it prints `42`.
- **`#assert` on it is a failure**, recorded as `#assert(x, 1): undefined` and reported on
  the terminal. An assertion that could not be computed is not an assertion that held;
  counting it as a pass would make an under-mocked test silently green, which is the one
  outcome worse than a red one.

**This part is designed, not built.** It waits on loops — there is no loop for `-step-N` to
count — and on functions, since no parameter can go unmocked without one. The open questions:

- **1-based counting.** Chosen for reading, against the compiler's own 0-based directive
  ids. It has to be documented loudly or it will be guessed wrong.
- **Where does the counter live?** One `alloca` per `#view-step-N` directive, next to the
  ones bindings already get, incremented and compared on every hit. That is the obvious
  shape and it is test-mode-only code, so its cost is not a concern; what needs deciding is
  whether the record is emitted on the matching hit (one `printf` under a branch) or the
  value is stashed and printed at exit (a slot to hold it, no branch). The first is
  simpler and matches how `#view` already emits.
- **Repeated `N` on one line.** Two directives with the same `N` are independent counters
  and both fire, which is right. But a single line reached zero times and a single line
  reached fewer than `N` times are indistinguishable in the output — both write
  `undefined`. That is probably fine, and is worth confirming against a real session before
  adding a second spelling for "never reached".
- **How is the poison carried?** An `undefined` value needs a tag next to its bits. That
  cost belongs to test builds only — a production layout must not gain a word because a
  feature exists that production never sees, so the tagged representation has to be a
  codegen-mode decision, not a type-system one.
- **Where does `undefined` come from first?** Unmocked parameters presume functions.
  Unreached steps presume loops. Whichever lands first should be the only source, so the
  rules above get exercised on one narrow case before they apply everywhere.

---

## Not yet: `#spec` and `#save`

A **spec** is a named bundle of directives, so one file can carry several test cases
without all of them being live at once.

```suru
#spec negatives              // loads the directives saved under that name
```

`#save <name>` lifts every directive in the enclosing unit into a `.spec` file and rewrites
itself to `#spec <name>` — you tweak values interactively until the program does what you
want, then pin the whole arrangement as a named case.

**This part is designed, not built.** It waits on user-defined functions, and the open
questions are the reason:

- **What is the unit?** A spec is a test case, and a test case is per-function. Suru has no
  functions, so today the only unit is the file. Building it file-scoped now means
  reworking it the moment functions land. The `test:<function>:<case>` selector in the
  original sketch presumes functions outright.
- **How are directives anchored?** A spec has to put each directive back where it came
  from. By line number is trivial and goes stale the instant the code moves. By statement
  index within the unit survives edits inside a line but not reordering. Whichever is
  chosen, the compiler must *detect* a stale spec and say so, rather than silently applying
  a mock to the wrong statement — that failure mode is much worse than no feature at all.
- **`#save` is a command that deletes itself.** Two runs of `suru test` over the same file
  are therefore not the same operation, which no other directive is guilty of. Constrain
  it: only honoured on a run that otherwise succeeded.
- **Selection.** Prefer ordinary arguments — `suru test <file> [<function>[:<case>]]` — to a
  compound mode name like `test:fn:case`, so the CLI keeps exactly two modes.

---

## Work items

### 1. Build modes
- [x] `enum BuildMode { Production, Test }`, defaulting to `Production` everywhere so no
      existing call site changes meaning
- [x] Thread through `Lexer`, `Compiler`, `TokenPrinter` and the `Source` test helper

### 2. Lex and parse
- [x] `TokenKind.Hash`; `case '#'` in the lexer skips to end of line in `Production` and
      returns a token in `Test`
- [x] `Tokens.SkipRestOfLine()`, asserting its lookahead queue is empty
- [x] `MockDirective`, `ViewDirective`, `AssertDirective` statements; `ViewDirective` and
      `AssertDirective` carry a per-module id assigned by the parser
- [x] `ParseDirective`, dispatching on the identifier after the `#`, rejecting unknown
      names, requiring the whole directive to sit on one line, and consuming an optional
      trailing `:` annotation

### 3. Semantic analysis
- [x] `#mock` reuses `AnalyzeAssignment`'s body, so the diagnostics are literally the same
- [x] `#view` reuses the `printLn` printable-type rule and its wording
- [x] `#assert` reuses the operand rule `=` already applies

### 4. Codegen
- [x] Extract the per-type format selection out of `EmitPrintLn` so `printLn` and the record
      emitters share one source of truth for how a value is rendered
- [x] `#mock` emits identically to an assignment
- [x] `#view` and `#assert` emit record `printf`s; `#assert` compares with the existing
      `IntPredicate` / float-predicate helpers

### 5. The `suru test` driver
- [x] `Compiler.Test(buildDir)` → `TestResult`: compile, link, run, collect, annotate,
      report. Only the CLI prints, per the existing convention
- [x] One `Annotate(line, text)` step shared by both directives — find the `:` or append
      one, replace everything after it. Idempotent by construction
- [x] `suru test` in the CLI, non-zero exit on a failed assertion or a non-zero program exit

### 6. Tests and docs
- [x] Unit tests first: directives parse in test mode and vanish in production mode; the
      re-parse cases `#view x: 1e+20` and `#assert(x, 1): fail, got 2`; every new diagnostic
- [x] Fixtures, **copied into the temp build root before compiling** — test mode rewrites
      the source in place and fixtures under version control must not be mutated
- [x] `doc/testing.md` and a row in `doc/README.md`, once it lands; `CHANGELOG.md`;
      `CLAUDE.md`
