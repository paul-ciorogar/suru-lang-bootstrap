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

Declare with `let`. Type is inferred from the right-hand side:

```suru
let x: 42
let ratio: 1.5
let flag: true
```

Reassign with `name: value` (no `let`):

```suru
flag: false
```

A `let` declared at module level (outside any function) is a **constant** — reassignment is a compile error:

```suru
let MAX_SIZE: 1024

fn main(args Array) Int64 {
    MAX_SIZE: 2048  // error: cannot reassign constant 'MAX_SIZE'
    return 0
}
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
let result: 2.add(3).multiply(4)
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
let x: true
match x { true: printLn(1), _: printLn(0) }
```

Expression form (arms produce a value):

```suru
let y: match x { true: 1, _: 0 }
printLn(y)
```

Match on integers (negative literals supported):

```suru
let n: 0.take(1)
match n { -1: printLn("negative"), 0: printLn("zero"), 1: printLn("positive"), _: printLn("other") }
```

Match on strings:

```suru
let day: "Monday"
match day { "Monday": printLn("start"), "Friday": printLn("end"), _: printLn("middle") }
```

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
  return match n.lessThan(2) {
    true: n,
    _: fibonacci(n.take(1)).add(fibonacci(n.take(2)))
  }
}

printLn(fibonacci(10))
```

### Structs

Create a struct with a `{ field: value, ... }` literal. Fields are separated by `,` or newlines. Type is inferred from the fields:

```suru
let person: { tall: true, height: 2283 }
```

Read a field with `.field` (no parentheses):

```suru
printLn(person.tall)    // true
printLn(person.height)  // 2283
```

Write a field with `receiver.field: value`:

```suru
person.tall: false
printLn(person.tall)  // false
```

Deep-copy a struct with `clone`:

```suru
let copy: clone(person)
```

Free a struct's memory with `drop`:

```suru
drop(person)
```

Pass structs to and from functions using the `Struct` type:

```suru
fn identity(d Struct) Struct {
  return d
}

let result: identity(person)
```

> **Implementation note:** Structs are heap-allocated linked lists of Field nodes (`%suru.Field = { ptr name, i32 tag, i64 val, ptr next }`). Field values carry a runtime tag (0=Bool, 1=Int64, 2=Float64, 3=pointer) used when the compile-time type is unknown.

### Arrays

Create an array with `[e1, e2, ...]`. All elements must have the same type:

```suru
let nums: [10, 20, 30]
```

| Method | Description | Example |
|---|---|---|
| `len()` | number of elements | `nums.len()` → `3` |
| `at(i)` | element at index | `nums.at(0)` → `10` |
| `set(val, i)` | update element in-place | `nums.set(99, 1)` |
| `add(v)` | append element (mutates, uses `realloc`) | `nums.add(40)` |
| `equals(other)` | element-wise equality | `nums.equals(other)` → `Bool` |
| `slice(from, to)` | new array copy of `[from, to)` | `nums.slice(1, 3)` |

```suru
let nums: [10, 20, 30]
printLn(nums.len())      // 3
nums.add(40)
printLn(nums.at(3))      // 40
let part: nums.slice(0, 2)
printLn(part.len())      // 2
```

Pass arrays to and from functions using the `Array` type:

```suru
fn first(arr Array) Int64 {
  return arr.at(0)
}
```

Use `clone(arr)` to deep-copy and `drop(arr)` to free the array and its data.

### Strings

String literals are written with double quotes. Supported escapes: `\n`, `\t`, `\\`, `\"`.

```suru
let s: "hello\nworld"
printLn(s)
```

| Method | Description | Example |
|---|---|---|
| `len()` | byte length | `s.len()` → `5` |
| `at(i)` | single-char `String` at index | `s.at(0)` → `"h"` |
| `equals(other)` | string equality | `s.equals("hello")` → `true` |
| `append(other)` | concatenate, new string | `s.append(" world")` |
| `slice(from, to)` | substring copy of `[from, to)` | `s.slice(1, 3)` → `"el"` |
| `toString()` | identity — returns itself | `s.toString()` |

```suru
let s: "hello"
printLn(s.len())            // 5
printLn(s.equals("hello"))  // true
let s2: s.append(" world")
printLn(s2)                 // hello world
printLn(s.at(0))            // h
```

### Type conversions

Convert a `String` to a number with the static `from` method:

```suru
let n: Int64.from("42")
let f: Float64.from("3.14")
```

Convert any primitive to a `String` with `toString()`:

```suru
let s: 42.toString()
let b: true.toString()
printLn(s)  // 42
printLn(b)  // true
```

### File I/O

Read an entire file as a `String`:

```suru
let content: readFile("input.txt")
printLn(content)
```

Write a `String` to a file (overwrites if the file exists):

```suru
writeFile("output.txt", content)
```

### Exit

Terminate the process with a specific exit code. `exit` is a terminal statement — a non-void function does not need an explicit `return` after it:

```suru
fn main(args Array) Int64 {
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

fn main(args Array) Int64 {
    let result: lib.double(21)
    printLn(result)   // 42
    return 0
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
- Only functions are imported — constants and top-level statements from the included file are ignored.

### Main function and CLI arguments

Every Suru program defines `fn main(args Array) Int64` as its entry point. `args.at(0)` is the program name; `args.at(1)` is the first user argument, and so on. The return value becomes the process exit code.

```suru
fn main(args Array) Int64 {
    let path: args.at(1)
    let content: readFile(path)
    printLn(content)
    return 0
}
```
