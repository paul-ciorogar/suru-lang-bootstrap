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

Optional type annotation between name and colon:

```suru
let count Int64: 0
```

Reassign with `name: value` (no `let`):

```suru
flag: false
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
| `lessThan(n)` | less-than | `5.lessThan(10)` → `true` |

Works on `Int64`, `Float64`, and `Bool`. Always returns `Bool`.

### Control flow — match

`match` is an expression that tests a `Bool` condition against a list of arms. Each arm is `pattern: body`. The wildcard `_` catches any unmatched case.

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

Arms can be separated by `,` or newlines. The condition must be `Bool`.

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

> **Implementation note:** Structs are heap-allocated linked lists of Field nodes (`%suru.Field = { ptr name, i32 tag, i64 val, ptr next }`). Field access is resolved at compile time by index.
