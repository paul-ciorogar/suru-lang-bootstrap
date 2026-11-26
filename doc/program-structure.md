# Program structure

A Suru program is a single file. The file is a sequence of statements, executed top to
bottom, and of [function](functions.md) declarations, which run only when called.

```suru
printLn(1)
printLn(2)
printLn(3)
```

```
1
2
3
```

There is no `main` function to declare — the statements in the file *are* the program. A
function can be declared anywhere in the file, including below its first call: all of a
file's functions are known before any of its statements run.
Compiling an empty file is legal and produces an executable that does nothing and exits
successfully. (TODO: add a main function, let statements outside a functio are constants and need to be resolved at compile time.)

## Statements

These are the statements:

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
1
```

```
error: hello.suru(1,1): only call expressions are allowed as statements
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
print(1) printLn(1, 2)
```

```
error: hello.suru(1,1): unknown function 'print'
error: hello.suru(1,10): 'printLn' expects 1 argument, got 2
```

Any character that is not part of a literal, an identifier, a comment, an operator (see
[Expressions](expressions.md)), `(`, `)`, `{`, `}`, `,`, `:` or whitespace is rejected by
the lexer:

```suru
printLn(1) @
```

```
error: hello.suru(1,12): unexpected character '@'
```

## Not yet supported

Imports and multi-file programs. Each will get its own page here when it lands.
