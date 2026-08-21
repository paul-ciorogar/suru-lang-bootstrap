# Testing

Suru's tests live in the program they test. A line starting with `#` is a **test
directive**: `suru build` never sees it, and `suru test` builds the program with the
directives live, runs it, and writes what they saw back into your source.

```suru
let width  i64: 3
let height i64: 4
#mock width: 10
let area   i64: width * height
#view area: 40
#assert(area, 40): pass
printLn(area)
```

```bash
suru build hello.suru   # ./build/hello prints 12
suru test  hello.suru   # prints 40, and annotates the file
```

The `: 40` and the `: pass` above were written by the compiler. You write the left-hand
side; **everything after the colon on a directive line belongs to the compiler.** Running
`suru test` again overwrites the same place, so a file that has not changed is rewritten
byte for byte.

## What `#` means

`#` runs to the end of the line, exactly like `//`.

In a **production build** it *is* a comment — the lexer skips the line and the rest of the
compiler is never told it was there. That means a directive cannot break `suru build`, no
matter what is written on it:

```suru
#anything at all, this is not even Suru
printLn(1)
```

```
1
```

It also means a mistake in a directive is only reported by `suru test`.

In a **test run** the line is code. It is lexed, typed and compiled like any other
statement, and it obeys every rule the rest of the language does.

`mock`, `view` and `assert` are test specific keywords. `let view i64: 1` is still a legal binding.

A directive must be written on one line. The rule that lets an expression continue onto the
next line does not apply to it:

```suru
#view 1
+ 1
```

```
error: hello.suru(2,3): a directive must be written on one line; '#' is on line 1
```

## `#mock` — stub a value

`#mock <name>: <value>` is the assignment `<name>: <value>`, compiled only into a test
build. It takes effect **where it is written**, so everything above it still ran with the
old value:

```suru
let seed i64: 7
printLn(seed)
#mock seed: 1
printLn(seed)
```

```
7
1
```

Being an ordinary assignment, it obeys ordinary scope — a block assigns the binding it can
see rather than making one of its own:

```suru
let seed i64: 7
{
  #mock seed: 1
}
printLn(seed)
```

```
1
```

And it obeys the ordinary type rule, because it is a store into an existing binding:

```suru
let seed i64: 7
#mock seed: 1.5
```

```
error: hello.suru(2,13): cannot assign a value of type 'f64' to 'seed' of type 'i64'
```

Mocking a name that is not bound is the same error an assignment would give:

```suru
#mock seed: 1
```

```
error: hello.suru(1,7): unknown variable 'seed'
```

> `#mock` is the only directive that changes what the program computes. A test build can
> therefore pass on values production never produces. `#view` and `#assert` only look.

## `#view` — see a value

`#view <expression>:` prints a value into its own source line.

Write the directive and leave the line there:

```suru
let a i64: 6
let b i64: 7
#view a * b:
```

Run `suru test`, and the file becomes:

```suru
let a i64: 6
let b i64: 7
#view a * b: 42
```

It takes a whole expression, not just a name. It shows a value the same way `printLn` does
and accepts the same types:

```suru
#view printLn(1):
```

```
error: hello.suru(1,7): '#view' cannot show a value of type 'void'; expected 'bool', 'i64', 'f64'
```

## `#assert` — pin a result

`#assert(<actual>, <expected>)` compares two values and records what happened, in the line
itself:

```suru
#assert(area, 40): pass
#assert(area, 12): fail, got 40
```

A failure is also reported on the terminal, positioned like every other Suru error, and
makes `suru test` exit non-zero:

```
error: hello.suru(6,1): assert failed: expected 12, got 40
4 passed, 1 failed, 0 undefined, 5 views written to hello.suru
```

Two places on purpose: the annotation is for reading the code, the error is for your editor
and for CI.

Both sides must be the same type, and that type must be one `#view` could show — the same
rule `=` follows:

```suru
#assert(1, true)
```

```
error: hello.suru(1,1): '#assert' cannot compare 'i64' with 'bool'
```

## Reading the output of a run

`suru test` prints what the program printed, then a summary:

```
40
4 passed, 1 failed, 0 undefined, 5 views written to hello.suru
```

The program's own output goes to stdout and the summary to stderr, so piping a test run
gives you the program's output alone. Viewed values are reported only as a count — they were
written into your source, which is where they are meant to be read.

## Directives the run never reached

A directive inside an [`if`](control-flow.md) arm is an ordinary statement of that arm: it obeys
the arm's scope, and it only runs when the arm does.

A directive in a branch that is **not** taken reports nothing, so there is no value to write
back. What the compiler writes instead is `undefined`:

```suru
let x i64: 2

if x = 0 {
  #view x: undefined
  #assert(x, 0): undefined
}
```

```
0 passed, 0 failed, 2 undefined, 0 views written to hello.suru
```

An unreached `#assert` is deliberately **neither a pass nor a failure**. It is counted on its
own, produces no error on the terminal, and does not make `suru test` exit non-zero: an
assertion in a branch this run did not take is honest, not broken. Counting it as a pass would
be the dishonest option, which is why it has a figure of its own rather than being folded into
one of the other two.

## Not yet supported

There is no way yet to name a group of directives and switch between them, so a file carries
one set at a time. `#spec` and `#save` are designed for that and wait on user-defined
functions, which is the unit a test case really belongs to.

`#view-step-N <expression>:` — a `#view` that keeps a counter of its own and shows the value
from the Nth time execution reached it — is designed and waits on loops, since there is
nothing yet to reach a line more than once.

`undefined` exists so far only as the annotation above: a directive that did not report. The
rest of it — `undefined` as a *value* that absorbs whatever it takes part in, so
`#view param1 + param2:` with only `param1` mocked writes `undefined` rather than a number that
looks like an answer — waits on user-defined functions, since an input nobody mocked presumes a
parameter.

Both are described in [todo.md](../todo.md).
