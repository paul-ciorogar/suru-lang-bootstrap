# Suru Lang Documentation

Suru is a minimalist, library-driven, data oriented, general-purpose language with static
typing and no garbage collection.

**This documents the language as the bootstrap compiler implements it today, and nothing
more.** That language is very small: a program is a sequence of `printLn` calls on
literal values. There are no variables, no operators, no user-defined functions and no
strings yet. Every page here describes something you can compile and run right now; a
feature gets a page when it lands, not when it is designed.

## Your first program

```suru
printLn(true)
printLn(1)
printLn(1.2)
```

Save it as `hello.suru` and build it:

```bash
suru build hello.suru
```

The compiler writes an object file and a native executable into a `build/` directory next
to the source:

```
hello.suru
build/
  hello.o
  hello
```

Run it:

```bash
./build/hello
```

```
true
1
1.2
```

There is no runtime and no interpreter — `build/hello` is an ordinary native executable.

## Language reference

| Page | Covers |
| --- | --- |
| [Program structure](program-structure.md) | What a program is: statements, whitespace, the absence of a terminator |
| [Literals and types](literals-and-types.md) | `bool`, `i64`, `f64`, and how literals are written |
| [Printing](printing.md) | `printLn`, the only builtin function |

## Elsewhere in this repository

- [README.md](../README.md) — building the compiler itself, and the stage dumps used to
  debug it
- [CHANGELOG.md](../CHANGELOG.md) — what has landed, in order
- [CLAUDE.md](../CLAUDE.md) — the compiler's architecture, for contributors
