# Suru Lang

> A minimalist, library-driven, general-purpose programming language with static typing and no garbage collection.

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

Available types: `Bool`, `Int32`, `Int64`, `Float64`, `String`, `Array<T>`, and any declared named type (e.g. `Point`). The `Struct` keyword is no longer valid — every struct value must use a named type.

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

`match` has two forms depending on context.

**Match statement** — used at statement level; each arm has a `{ }` block body that can contain multiple statements, `let` bindings, and early `return`. An empty arm is written `{}` or left empty after `:`.

```suru
fn classify(n Int64) String {
    match n {
        0: { return "zero" }
        1: {
            let msg String: "one"
            return msg
        }
        _: {}   // empty arm — falls through to the statement below
    }
    return "many"
}
```

A match statement satisfies the non-void return requirement when every arm (including the wildcard) contains a return or exit.

**Match expression** — used on the right-hand side of `let`, `return`, or any expression position; each arm body is a single expression that produces the result value.

```suru
let y Int64: match x { true: 1, _: 0 }
printLn(y)
```

Both forms use the same arm syntax (`pattern: body`) and support the same pattern types: `Bool` literals, integer/float literals (including negative), string literals, and identifier patterns (variable or constant lookup).

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
let p Point: { x: 2283, y: 2281 }
printLn(p.x)   // 2283
printLn(p.y)   // 2281

fn makePoint(x Int64, y Int64) Point {
    return { x: x, y: y }
}
```

> Named types resolve to `SuruType.Struct` at the semantic layer — they use the same heap-allocated linked-list runtime representation. Duplicate type names are compile-time errors.

---

### Structs

Declare a named struct type (required before instantiation), then create a value with `{ field: value, ... }`. Fields are separated by `,` or newlines. **No per-field type annotations** — types come from the `type` declaration:

```suru
type Person: { tall Bool, height Int64 }

let person Person: { tall: true, height: 2283 }
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
let copy Person: clone(person)
```

Free a struct's memory with `drop`:

```suru
drop(person)
```

Pass structs to and from functions using the named type. Extract fields to typed `let` bindings before using them:

```suru
type Point: { x Int64, y Int64 }

fn makePoint(x Int64, y Int64) Point {
    return { x: x, y: y }
}

fn getX(p Point) Int64 {
    return p.x
}

fn main(args Array<String>) {
    let pt Point: makePoint(3, 4)
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
```

The type parameter `T` can be any Suru type: `Bool`, `Int32`, `Int64`, `Float64`, `String`, or any named type (e.g. `Array<Token>`).

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
type Token: { kind Int64, text String, line Int64, col Int64 }

fn tokenize(source String) Array<Token> {
    let tokens Array<Token>: []
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

When the element type is a named struct, fields can be accessed directly on `.at()` results — no intermediate variable or wrapper function needed:

```suru
type Token: { kind Int64, text String, line Int64, col Int64 }

fn getKind(tokens Array<Token>, i Int64) Int64 {
    return tokens.at(i).kind      // Int64 field resolved correctly
}

fn getText(tokens Array<Token>, i Int64) String {
    return tokens.at(i).text      // String field resolved correctly
}
```

This works because the compiler tracks the element type from the `Array<T>` annotation and annotates every AST expression with its resolved type during semantic analysis.

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
- Named types (`type` declarations) from the included file are available by name in the importing module — no need to re-declare them. Re-declaring an imported type is a compile error.
- Scalar constants (`let NAME Type: literal`) from the included file are also available in the importing module.
- Circular includes are detected and reported as a compile error.
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
| 12.5g | **Semantic analysis re-enabled** — block-level scope stack; semantic errors on every compile path | ✅ Complete |
| 13a | **Semantic analyzer data structures in Suru** (`tests/fixtures/suru-semantic/`) — scope chain, symbol lookup, error accumulation | ✅ Complete |
| 13b | **Declaration pre-passes in Suru** (`suru-semantic-passes.suru`) — type resolution, type/function table population | ✅ Complete |
| 13c–e | **Semantic Analyzer in Suru** (statement, function, and expression analysis) | ✅ Complete |
| 13f | **Semantic cross-validation CLI** (`tests/fixtures/suru-check/`) — `suru-check` validates Suru programs; output matches C# reference | ✅ Complete |
| 14 | Code Generator in Suru | ⬜ Planned |
| 15 | Bootstrap: Suru compiler compiles itself | ⬜ Planned |

### Stage 13a — Semantic Analyzer Data Structures

`tests/fixtures/suru-semantic/suru-semantic.suru` defines the scope-chain types and helpers that the Suru semantic analyzer (Stages 13b–e) will build on.

**Types:**

```suru
type SymbolEntry:   { name String, typeName String }
type Scope:         { symbols Array<SymbolEntry>, parent Int64 }
type FunctionSig:   { name String, paramTypes Array<String>, returnType String }
type AnalysisError: { message String }
type AnalyzerState: {
    scopes            Array<Scope>
    functions         Array<FunctionSig>
    typeNames         Array<String>
    errors            Array<AnalysisError>
    currentReturnType String
    insideFunction    Int64
    constants         Array<String>
}
```

The scope chain is a flat `Array<Scope>` with integer `parent` indices (simulating a stack). `parent = -1` marks the module scope. The current scope is always the last element; `pushScope` appends and `popScope` slices off the last.

**Helpers:** `makeAnalyzerState`, `pushScope`, `popScope`, `declareSymbol`, `lookupSymbol` (walks parent chain), `existsInCurrentScope` (current frame only, for duplicate-let detection), `addError`.

```bash
# Compile and run all semantic unit tests (Stage 13a + 13b)
dotnet run --project src/Suru.CLI -- build tests/fixtures/suru-semantic/main.suru
./tests/fixtures/suru-semantic/build/main
# PASS: push_pop_roundtrip
# PASS: lookup_finds_nearest
# PASS: lookup_walks_parent
# PASS: lookup_returns_empty
# PASS: add_error
# PASS: resolveTypeName_builtin
# PASS: resolveTypeName_user_declared
# PASS: collectTypeDecls_registers
# PASS: collectTypeDecls_duplicate
# PASS: collectFnDecls_registers
# PASS: collectFnDecls_duplicate
# PASS: collectFnDecls_unknown_param
# PASS: runPrePasses_cross_pass
```

### Stage 13b — Declaration Pre-passes

`tests/fixtures/suru-semantic/suru-semantic-passes.suru` implements the two declaration pre-passes that populate the type and function tables before any statement analysis runs. It includes `suru-semantic.suru` (for `AnalyzerState` and helpers) and `suru-parser.suru` (for `AstNode`, `Param`, and the `NODE_*` constants).

**Pass 1 — `collectTypeDeclarations`:** walks the module's statement list, finds every `NODE_TYPE_DECL` node, and adds its `name` to `state.typeNames`. Reports a duplicate-type error if the name was already seen.

**Pass 2 — `collectFunctionDeclarations`:** walks the statement list, finds every `NODE_FN_DECL` node, and registers its signature in `state.functions`. Validates each parameter type and the return type via `resolveTypeName`; reports an error for any unknown type. Reports a duplicate-function error if the name was already registered.

**Type resolution:** `resolveTypeName(state, name)` returns `true` for the six built-in type names (`Bool`, `Int32`, `Int64`, `Float64`, `String`, `Array`) or any name already in `state.typeNames`. Because pass 1 runs first, a user-declared type (e.g. `type Point: { ... }`) is in `typeNames` before pass 2 validates function parameter types — so `fn usePoint(p Point)` resolves correctly without any extra wiring.

```suru
// Example: resolveTypeName recognises built-ins and user-declared names
let state AnalyzerState: semantic.makeAnalyzerState()
let typeNames Array<String>: state.typeNames
typeNames.add("Point")
passes.resolveTypeName(state, "Int64")  // true — built-in
passes.resolveTypeName(state, "Point")  // true — user-declared
passes.resolveTypeName(state, "Nope")   // false — unknown
```

### Stage 13f — Semantic Cross-Validation CLI (`suru-check`)

`tests/fixtures/suru-check/main.suru` is a complete semantic-analysis CLI written in Suru. It wires together all earlier semantic stages into a single tool that can report semantic errors on any Suru source file.

```bash
# Compile suru-check
dotnet run --project src/Suru.CLI -- build tests/fixtures/suru-check/main.suru

# Check a Suru source file for semantic errors
./tests/fixtures/suru-check/build/main path/to/file.suru
# (prints errors to stdout in "<path>: <message>" format; exit code 1 on errors)

# Example: valid program
./tests/fixtures/suru-check/build/main tests/fixtures/arithmetic/main.suru
# (no output, exit code 0)

# Example: undefined variable
./tests/fixtures/suru-check/build/main tests/fixtures/suru-check/corpus/invalid_undef_var.suru
# .../invalid_undef_var.suru: undefined variable 'undeclared'
# (exit code 1)
```

**Pipeline:** `lexer.tokenize(source)` → `parser.parse(tokens)` → `passes.runPrePasses(state, stmts)` → `analyzeModuleStatements(state, stmts)` → print errors → exit 1 if any.

**Cross-validation:** `IRSuruSemanticCrossValidationTests.cs` runs `suru-check` and the C# compiler on the same programs and asserts that both agree on validity and, for invalid programs, that the first error message body matches exactly.

**Corpus** (`tests/fixtures/suru-check/corpus/`):

| File | Expected |
|---|---|
| `valid_hello.suru` | No errors |
| `valid_functions.suru` | No errors |
| `invalid_undef_var.suru` | `undefined variable 'undeclared'` |
| `invalid_dup_type.suru` | `type 'Point' is already declared` |
| `invalid_arity.suru` | `function 'greet' called with 2 argument(s), expected 1` |

> **Limitation:** The Suru lexer does not support `//` line comments. Source files containing comments cannot be analysed by `suru-check`. All corpus files and the three milestone fixture files (`arithmetic`, `fibonacci`, `control-flow`) are comment-free.

### Stage 12 — Suru Parser

`tests/fixtures/suru-parser/suru-parser.suru` is a complete recursive-descent parser written in Suru. It accepts a token array (produced by the Stage-9 suru-lexer) and returns a Suru struct representing the AST, then pretty-prints it in the same indented-tree format as the C# `AstPrinter`.

```bash
# Compile the suru-parser fixture
dotnet run --project src/Suru.CLI -- build tests/fixtures/suru-parser/main.suru

# Parse a .suru file and print its AST
./tests/fixtures/suru-parser/build/main path/to/file.suru
```

Cross-validation tests in `IRSuruParserTests.cs` compare the Suru parser's output byte-for-byte against `AstPrinter.Print()` from the C# compiler on the same input files.
