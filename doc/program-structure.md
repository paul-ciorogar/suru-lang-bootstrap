# Program structure

A Suru program is a single file. The file holds **declarations only**: file-level
[constants](bindings.md#constants) and [functions](functions.md). A program starts at the
function named `main`:

```suru
let limit i64: 3

fn main() void {
  let count i64: 1
  while count <= limit {
    printLn(count)
    count: count + 1
  }
}
```

```
1
2
3
```

`main` runs once, from top to bottom. Every other function runs only when it is called. A
function and a constant can be declared anywhere in the file, in any order, because all of a
file's declarations are known before any code runs.

## `main`

```
fn main() void { body }
```

`main` takes no parameters and returns nothing, and the program exits successfully when it
returns. Any other signature is an error, not just another function, because a near-miss on
the entry point would otherwise build a program that silently does nothing:

```suru
fn main() i64 {
  return 0
}
```

```
error: hello.suru(1,1): 'main' must be declared as 'fn main() void'
```

**`main` is optional.** A file without one compiles to a program that does nothing and exits
successfully. An empty file is legal, and so is a file of constants and functions with no
`main`, which [`suru test`](testing.md) can still build.

**The name is reserved.** The file-level `fn main` is the only thing `main` can ever mean, which
guarantees that it runs exactly once. A `fn main` anywhere but the file level is an error:

```suru
fn helper() void {
  fn main() void { }
}
```

```
error: hello.suru(2,3): 'main' can only be declared at file level
```

So is a `let` or a parameter named `main`, at any level:

```suru
fn main() void {
  let main i64: 1
}
```

```
error: hello.suru(2,7): 'main' is reserved
```

`main` cannot be called, from another function or from itself, whether or not the file declares
one:

```suru
fn main() void {
  main()
}
```

```
error: hello.suru(2,3): 'main' cannot be called
```

## What a file may hold

A file may hold `let` and `fn`, and nothing else. A file-level `let` is a
[constant](bindings.md#constants). Code (a call, an assignment, a block, `if`, `while`,
`break`, `continue` or `return`) belongs in a function. At file level each of these is
reported once:

```suru
printLn(1)
let count i64: 1
count: 2

fn main() void {
  printLn(count)
}
```

```
error: hello.suru(1,1): only 'let' and 'fn' may appear outside a function
error: hello.suru(3,1): only 'let' and 'fn' may appear outside a function
```

A [test directive](testing.md) also belongs in a function.

## Reading the examples

Most examples on these pages are the **body of a function**. Every statement in them sits
inside a function, and they leave the `fn main() void { … }` around them unwritten:

```suru
printLn(1)
printLn(2)
```

To run one, put it in `main`. An example that declares a function or a constant, or that shows
an error message, is written as a **whole file**, because then it matters what sits at file
level and on which line.

## Statements

These are the statements a function body is made of:

| Form | Example | Page |
| --- | --- | --- |
| A binding | `let count i64: 1` | [Bindings](bindings.md) |
| An assignment | `count: 2` | [Bindings](bindings.md) |
| A call | `printLn(count)` | [Printing](printing.md), [Functions](functions.md) |
| A block | `{ printLn(count) }` | [Blocks](blocks.md) |
| A branch or a loop, and `break`/`continue` | `if ready { printLn(count) }` | [Control flow](control-flow.md) |
| A function declaration | `fn double(n i64) i64 { return n * 2 }` | [Functions](functions.md) |
| A return | `return count` | [Functions](functions.md) |

An expression statement must be a **call**. A bare value is parsed fine but rejected:

```suru
fn main() void {
  1
}
```

```
error: hello.suru(2,3): only call expressions are allowed as statements
```

This is deliberate: a value computed and then dropped is almost always a mistake. Bind
it or print it.

## No statement terminator

Statements are not separated by semicolons or by anything else. The parser knows a
statement has ended because the next one begins.

## Whitespace is insignificant

Spaces, tabs and newlines are all just separators, and none of them are required between
statements. These two programs are identical:

```suru
printLn(true)
printLn(1)
```

```suru
printLn(true) printLn(1)
```

Newlines carry no meaning, so a call can be split across lines freely. Line and column
numbers are still tracked for error messages.

With one consequence worth knowing: because nothing terminates a statement, an
expression keeps going while the next token is a binary operator — even across a
newline. A line that starts with an operator continues the line above rather than
beginning a new statement. See [Splitting an expression across
lines](expressions.md#splitting-an-expression-across-lines).

## Comments

`//` starts a comment that runs to the end of the line. It is the only comment syntax —
there is no block comment. `#` also runs to the end of the line, but it starts a
[test directive](testing.md), which `suru build` skips exactly as it skips a comment.

```suru
// Comments are whitespace: the lexer drops them and the parser never sees them.
printLn(1) // A comment can follow code on the same line.
// printLn(2)
printLn(3)
```

```
1
3
```

Because a comment ends at the newline, it can sit inside a call that is split across
lines, and a comment on the last line does not need a trailing newline. A file that is
nothing but comments is an empty program.

## Errors

The compiler reports errors as `file(line,column): message` on stderr and exits without
writing an executable.

Lexing and parsing stop at the first error. Semantic analysis does not — it reports
every problem it finds in one run:

```suru
fn main() void {
  print(1) printLn(1, 2)
}
```

```
error: hello.suru(2,3): unknown function 'print'
error: hello.suru(2,12): 'printLn' expects 1 argument, got 2
```

Any character that is not part of a literal, an identifier, a comment, an operator (see
[Expressions](expressions.md)), `(`, `)`, `{`, `}`, `,`, `:` or whitespace is rejected by
the lexer:

```suru
fn main() void {
  printLn(1) @
}
```

```
error: hello.suru(2,14): unexpected character '@'
```

## Not yet supported

Imports and multi-file programs. Each will get its own page here when it lands.
`main` cannot return an exit code or read command-line arguments.
