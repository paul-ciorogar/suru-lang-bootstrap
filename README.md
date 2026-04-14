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
