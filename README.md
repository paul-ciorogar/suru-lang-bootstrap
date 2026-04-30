# Suru Lang

> A minimalist, library-driven, general-purpose programming language with structural typing and no garbage collection.

## Overview

Suru Lang prioritizes interactive development, transforming editors into REPL-like environments through LSP integration. The language emphasizes minimal syntax with maximum expressiveness, enabling developers to write clear, readable code without unnecessary ceremony.

## Language Guide

### Printing

```suru
printLn(true)
printLn(42)
printLn(3.14)
```

### Variables

Declare with `let`. The type annotation is **mandatory** and appears between the variable name and the `:`:

```suru
let x Int64: 42
let ratio Float64: 1.5
let flag Bool: true
let name String: "suru"
```

Reassign with `name: value` (no `let`):

```suru
flag: false
```

A `let` declared at module level (outside any function) is a **constant** — reassignment is a compile error:

```suru
let MAX_SIZE Int64: 1024

fn main(args Array<String>) {
    MAX_SIZE: 2048  // error: cannot reassign constant 'MAX_SIZE'
}
```

Available scalar types: `Bool`, `Int32`, `Int64`, `Float64`, `String`, `Struct`, `Array<T>`, and any declared named type (e.g. `Point`).

### Comments

Use `//` for line comments. Everything from `//` to the end of the line is ignored:

```suru
// full-line comment
let x Int64: 42  // inline comment
```

### Arithmetic

Arithmetic is expressed as method calls on values:

| Method | Description | Example |
|---|---|---|
| `add(n)` | addition | `3.add(1)` → `4` |
| `take(n)` | subtraction | `5.take(2)` → `3` |
| `multiply(n)` | multiplication | `3.multiply(2)` → `6` |
| `split(n)` | division | `9.split(3)` → `3` |
| `invert()` | negation | `5.invert()` → `-5` |

Methods chain naturally:

```suru
let result Int64: 2.add(3).multiply(4)
printLn(result)
```

### Boolean operators

```suru
printLn(true and false)   // false
printLn(true or false)    // true
printLn(not true)         // false
```

### Comparison methods

| Method | Description | Example |
|---|---|---|
| `equals(n)` | equality | `3.equals(3)` → `true` |
| `lt(n)` | less-than | `5.lt(10)` → `true` |
| `gt(n)` | greater-than | `10.gt(5)` → `true` |
| `lte(n)` | less-than-or-equal | `5.lte(5)` → `true` |
| `gte(n)` | greater-than-or-equal | `5.gte(3)` → `true` |
| `compare(n)` | three-way comparison | `2.compare(5)` → `-1` |

`equals`, `lt`, `gt`, `lte`, `gte` work on `Int64`, `Float64`, and `Bool`; always return `Bool`. `compare` works on `Int64` and `Float64`; returns `Int64` (`-1` = less, `0` = equal, `1` = greater).

### Control flow — match

`match` is an expression that tests a condition against a list of arms. Each arm is `pattern: body`. The wildcard `_` catches any unmatched case.

Statement form (arms produce side effects):

```suru
let x Bool: true
match x { true: printLn(1), _: printLn(0) }
```

Expression form (arms produce a value):

```suru
let y Int64: match x { true: 1, _: 0 }
printLn(y)
```

Match on integers (negative literals supported):

```suru
let n Int64: 0.take(1)
match n { -1: printLn("negative"), 0: printLn("zero"), 1: printLn("positive"), _: printLn("other") }
```

Match on strings:

```suru
let day String: "Monday"
match day { "Monday": printLn("start"), "Friday": printLn("end"), _: printLn("middle") }
```

Match on variables or constants (any identifier in pattern position is loaded and compared at runtime):

```suru
let THRESHOLD Int64: 10

fn main(args Array<String>) {
    let score Int64: 10
    match score {
        THRESHOLD: printLn("exact")
        _: printLn("other")
    }
}
```

This works for both module-level constants and local variables.

Arms are separated by `,` or newlines. The condition must be `Bool`, `Int64`, `Float64`, or `String`.

### Functions

Declare with `fn`. Parameters are `name Type` pairs. The return type follows the parameter list:

```suru
fn add(a Int64, b Int64) Int64 {
  return a.add(b)
}

printLn(add(3, 4))
```

Use `void` for functions that return no value:

```suru
fn printDouble(n Int64) void {
  printLn(n.add(n))
}
```

Recursion is supported:

```suru
fn fibonacci(n Int64) Int64 {
  return match n.lt(2) {
    true: n,
    _: fibonacci(n.take(1)).add(fibonacci(n.take(2)))
  }
}

printLn(fibonacci(10))
```

### Named Types

Declare a named struct type with `type Name: { field Type, ... }`. Fields use only a name and a type — no value. Inline and multiline forms are both valid:

```suru
// inline
type Point: { x Int64, y Int64 }

// multiline
type Person: {
    name String
    age  Int64
}
```

Use the type name in `let` declarations, function parameters, and return types:

```suru
let p Point: { x Int64: 2283, y Int64: 2281 }
printLn(p.x)   // 2283
printLn(p.y)   // 2281

fn makePoint(x Int64, y Int64) Point {
    return { x Int64: x, y Int64: y }
}
```

> Named types resolve to `SuruType.Struct` — they use the same heap-allocated linked-list runtime as anonymous `Struct` values. Duplicate type names are compile-time errors.

---

### Structs

Create a struct with a `{ field Type: value, ... }` literal. Every field requires a **mandatory type annotation** between the field name and the `:`. Fields are separated by `,` or newlines:

```suru
let person Struct: { tall Bool: true, height Int64: 2283 }
```

Read a field with `.field` (no parentheses). Extract to a typed `let` before using the value:

```suru
let isTall Bool: person.tall
let h Int64: person.height
printLn(isTall)  // true
printLn(h)       // 2283
```

Write a field with `receiver.field: value`:

```suru
person.tall: false
let isTall Bool: person.tall
printLn(isTall)  // false
```

Deep-copy a struct with `clone`:

```suru
let copy Struct: clone(person)
```

Free a struct's memory with `drop`:

```suru
drop(person)
```

Pass structs to and from functions using the `Struct` type. Extract fields to typed `let` bindings before using them — the type annotation is the authoritative source when the struct crosses a function boundary:

```suru
fn makePoint(x Int64, y Int64) Struct {
    return { x Int64: x, y Int64: y }
}

fn getX(p Struct) Int64 {
    return p.x
}

fn main(args Array<String>) {
    let pt Struct: makePoint(3, 4)
    let px Int64: pt.x
    printLn(px)            // 3
    printLn(getX(pt))      // 3
    drop(pt)
}
```

> **Implementation note:** Structs are heap-allocated linked lists of Field nodes (`%suru.Field = { i64 type_tag=4, ptr name, i32 field_tag, i64 val, ptr next }`). The `field_tag` uses the unified type enum (0=Bool, 1=Int32, 2=Int64, 3=Float64, 4=Struct, 5=Array, 6=String); field values are stored as `ptrtoint(ptr)`. `type_tag=4` at offset 0 identifies the node as a Struct to runtime inspection.

### Arrays

Create an array with `[e1, e2, ...]`. The element type is specified with the `Array<T>` generic annotation:

```suru
let nums Array<Int64>: [10, 20, 30]
let words Array<String>: ["hello", "world"]
let items Array<Struct>: []
```

The type parameter `T` can be any Suru type: `Bool`, `Int32`, `Int64`, `Float64`, `String`, `Struct`.

| Method | Description | Example |
|---|---|---|
| `len()` | number of elements | `nums.len()` → `3` |
| `at(i)` | element at index | `nums.at(0)` → `10` |
| `set(val, i)` | update element in-place | `nums.set(99, 1)` |
| `add(v)` | append element (mutates, uses `realloc`) | `nums.add(40)` |
| `equals(other)` | element-wise equality | `nums.equals(other)` → `Bool` |
| `slice(from, to)` | new array copy of `[from, to)` | `nums.slice(1, 3)` |

```suru
let nums Array<Int64>: [10, 20, 30]
printLn(nums.len())      // 3
nums.add(40)
printLn(nums.at(3))      // 40
let part Array<Int64>: nums.slice(0, 2)
printLn(part.len())      // 2
```

Pass arrays to and from functions using the `Array<T>` type:

```suru
fn tokenize(source String) Array<Struct> {
    let tokens Array<Struct>: []
    // ... build tokens ...
    return tokens
}

fn processAll(items Array<Int64>) void {
    let i Int64: 0
    while i.lt(items.len()) {
        printLn(items.at(i))
        i: i.add(1)
    }
}
```

Use `clone(arr)` to deep-copy and `drop(arr)` to free the array and its data.

### Strings

String literals are written with double quotes. Supported escapes: `\n`, `\t`, `\\`, `\"`.

```suru
let s String: "hello\nworld"
printLn(s)
```

| Method | Description | Example |
|---|---|---|
| `len()` | byte length | `s.len()` → `5` |
| `at(i)` | single-char `String` at index | `s.at(0)` → `"h"` |
| `equals(other)` | string equality | `s.equals("hello")` → `true` |
| `append(other)` | concatenate, new string | `s.append(" world")` |
| `slice(from, to)` | substring copy of `[from, to)` | `s.slice(1, 3)` → `"el"` |
| `ord()` | ASCII code of first byte | `"A".ord()` → `65` |
| `toString()` | identity — returns itself | `s.toString()` |

```suru
let s String: "hello"
printLn(s.len())            // 5
printLn(s.equals("hello"))  // true
let s2 String: s.append(" world")
printLn(s2)                 // hello world
printLn(s.at(0))            // h
```

### Type conversions

Convert a `String` to a number with the static `from` method:

```suru
let n Int64: Int64.from("42")
let f Float64: Float64.from("3.14")
```

Convert any primitive to a `String` with `toString()`:

```suru
let s String: 42.toString()
let b String: true.toString()
printLn(s)  // 42
printLn(b)  // true
```

### File I/O

Read an entire file as a `String`:

```suru
let content String: readFile("input.txt")
printLn(content)
```

Write a `String` to a file (overwrites if the file exists):

```suru
writeFile("output.txt", content)
```

### Exit

Terminate the process with a specific exit code. `exit` is a terminal statement — a non-void function does not need an explicit `return` after it:

```suru
fn main(args Array<String>) {
    exit(1)
}
```

### printError

Write to stderr. Accepts the same types as `printLn` (Bool, Int64, Float64, String):

```suru
printError("error: file not found")
printError(42)
```

### Include directive

Split a program across multiple `.suru` files using `include`. Functions from the included file are accessible under a namespace alias:

```suru
include "lib.suru" as lib

fn main(args Array<String>) {
    let result Int64: lib.double(21)
    printLn(result)   // 42
}
```

`lib.suru`:
```suru
fn double(n Int64) Int64 {
    return n.multiply(2)
}
```

- The path is relative to the file that contains the `include`.
- All functions from the included file become available as `ns.fn(args)`.
- Circular includes are detected and reported as a compile error.
- Only functions are imported — constants and top-level statements from the included file are not exported to the importing module.
- Each `.suru` file is compiled to its own object file; the linker resolves cross-module references. The `ns.` prefix is a Suru language concept only — LLVM call sites use the original unqualified function name.
- Include chains are transitive: if `main.suru` includes `a.suru` which includes `b.suru`, all three are compiled to separate objects and linked together.

### Main function and CLI arguments

Every Suru program defines `fn main(args Array<String>)` as its entry point. `args.at(0)` is the program name; `args.at(1)` is the first user argument, and so on. The process always exits with code `0` unless `exit(code)` is called explicitly.

```suru
fn main(args Array<String>) {
    let path String: args.at(1)
    let content String: readFile(path)
    printLn(content)
}
```

## CLI Reference

```
suru <command> <file.suru>
```

### `build`

Compiles a `.suru` file to a native executable. The output is written to a `build/` directory next to the source file.

```bash
dotnet run --project src/Suru.CLI -- build examples/hello.suru
# Produces: examples/build/hello
```

Errors are printed to stderr and the process exits with code 1.

### `lex`

Tokenises the source file and prints every token to stdout, one per line:

```
   1:1   Fn
   1:4   Identifier      main
   1:8   LeftParen
   1:9   Identifier      args
   1:14  Identifier      Array
   1:19  LessThan
   1:20  Identifier      String
   1:26  GreaterThan
   1:27  RightParen
   ...
```

Columns: `line:col`, token kind (padded), source text (where non-empty). Useful for checking that the lexer recognises all tokens before debugging a parse failure.

```bash
dotnet run --project src/Suru.CLI -- lex examples/hello.suru
```

### `parse`

Parses the source file (include directives are expanded) and prints the AST as an indented tree. Each node kind appears on its own line; child nodes are indented by two spaces. Leaf values — names, literals — are shown in `[square brackets]`.

```
Module [examples/hello.suru]
  FunctionDeclaration [main](args : Array<String>) -> void
    ExpressionStatement
      CallExpression [printLn]
        StringLiteral ["hello\n"]
```

```bash
dotnet run --project src/Suru.CLI -- parse examples/hello.suru
```

### `ir`

Runs the full front-end pipeline (lex → parse → semantic analysis → IR codegen) and prints the generated LLVM IR text to stdout. No files are written and `clang` is not invoked. Useful for inspecting or diffing generated IR without a full build.

```bash
dotnet run --project src/Suru.CLI -- ir examples/hello.suru
```

## Self-Hosting Progress

Suru is being implemented in stages toward compiling its own source. Each stage is validated by running the Suru-compiled tool against real inputs and cross-checking against the C# reference implementation.

| Stage | What | Status |
|---|---|---|
| 1–8 | Core language: Hello World → structs, arrays, strings, file I/O, while, comparisons | ✅ Complete |
| 9 | **Lexer in Suru** (`tests/fixtures/suru-lexer/`) — tokenises Suru source; cross-validated against C# lexer | ✅ Complete |
| 10–11 | Language convenience: mandatory type annotations, constants, `include`, negative literals, `printError` | ✅ Complete |
| 12 | **Parser in Suru** (`tests/fixtures/suru-parser/`) — recursive-descent parser; cross-validated against C# parser | ✅ Complete |
| 13 | Semantic Analyzer in Suru | ⬜ Planned |
| 14 | Code Generator in Suru | ⬜ Planned |
| 15 | Bootstrap: Suru compiler compiles itself | ⬜ Planned |

### Stage 12 — Suru Parser

`tests/fixtures/suru-parser/suru-parser.suru` is a complete recursive-descent parser written in Suru. It accepts a token array (produced by the Stage-9 suru-lexer) and returns a Suru struct representing the AST, then pretty-prints it in the same indented-tree format as the C# `AstPrinter`.

```bash
# Compile the suru-parser fixture
dotnet run --project src/Suru.CLI -- build tests/fixtures/suru-parser/main.suru

# Parse a .suru file and print its AST
./tests/fixtures/suru-parser/build/main path/to/file.suru
```

Cross-validation tests in `IRSuruParserTests.cs` compare the Suru parser's output byte-for-byte against `AstPrinter.Print()` from the C# compiler on the same input files.
