# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Stage 8 Prep 2 — Language Convenience

- **Removed optional type annotation from `let`** — `let x Int64: expr` is no longer valid syntax; the grammar is now simply `let name: expr`. Types are inferred from the RHS and checked at runtime for struct/array values.
- **Top-level `let` = constant** — a `let` declared at module scope (outside any function) is immutable; reassignment anywhere is a compile-time error: `cannot reassign constant 'name'`.
- **Removed compile-time struct/array type validation** — no more errors for accessing undefined struct fields or heterogeneous array literals; these are checked at runtime via the field tag system.
- SemanticAnalyzer: removed argument-type mismatch checking at call sites for struct/array parameters; internal type inference machinery retained for codegen.
- `LetStatement` AST node: `TypeAnnotation` field removed; constructor now takes `(name, value)`.

### Stage 8 — Suru Lexer Written in Suru
- `tests/fixtures/suru-lexer/main.suru` — full lexer: `tokenize(source String) Array` plus helpers (`isDigit`, `isLetter`, `isWhitespace`, `readIdent`, `readNumber`, `readString`, escape handling, keyword dispatch via `match`)
- Struct field access now uses a runtime linked-list lookup (`suru_find_field` LLVM helper) instead of compile-time index tables — field names are matched by `strcmp` at runtime, matching the stored `name` pointer in each `%suru.Field` node
- Field node `tag` field (slot 1, `i32`) is written at struct construction and read at runtime when the compile-time `ResolvedType` cannot be determined (e.g. `r.token` where `r` comes from a `match`-returning function); tag encodes 0=Bool, 1=Int64, 2=Float64, 3=pointer
- `FieldAccessExpression.ResolvedType` property set by `SemanticAnalyzer`; `CodeGenerator` uses it for `FromI64` cast when known, falls back to runtime tag dispatch otherwise
- `SuruLexerTests` integration tests: exact token stream for the print fixture; self-lex sanity check (>1000 tokens); spot-checks on arithmetic and structs fixtures
- **Milestone:** Suru lexer tokenizes its own source file correctly

### Stage 8 Prep — Language Features for Self-Hosting
- Comparison methods `lt`, `gt`, `lte`, `gte` added to `Int64` and `Float64` (replaced `lessThan`); all existing fixtures and tests updated
- `while` loop statement: `while <Bool expr> { <body> }` — lexer token `While`, AST `WhileStatement`, semantic condition-type check, codegen three-block pattern (`while_cond_N` / `while_body_N` / `while_after_N`)
- `String.ord() Int64` — returns ASCII code of first byte; implemented as GEP into `%suru.Seq` data pointer + `zext i8 to i64`
- `ComparisonTests` integration test covering all four comparison methods and `ord`
- `WhileLoopTests` integration test with counter, conditional accumulation, and nested `while`

### Extended Match & Added `compare`
- Match conditions now accept `Int64`, `Float64`, and `String` in addition to `Bool`
- Match patterns now support string literals (e.g. `"Monday":`) and negative numeric literals (e.g. `-1:`)
- Lexer: added `Minus` (`-`) token; parser: `ParseMatchPattern` handles `TokenKind.Minus` followed by int/float literal
- Codegen: `EmitMatchTestChain` uses `strcmp` for `String` conditions (same approach as `String.equals`)
- New built-in method `compare(n)` on `Int64` and `Float64` — returns `-1`, `0`, or `1` for less-than, equal, greater-than; implemented branchlessly as `(a > b) - (a < b)` via ZExt + Sub

### Refactored
- All fixtures now use explicit `fn main(args Array) Int64` — the standard entry point for every Suru program
- `String.at(i)` now returns a single-character `String` (a heap-allocated `%suru.Seq` with `len=1`) instead of the raw byte value as `Int64`
- Test infrastructure: extracted duplicate `Run`/`RunGetExitCode` helpers into `IntegrationTestBase`; all test classes now inherit from it and use primary constructor syntax; `using System.Diagnostics` removed from individual test files

### Added File I/O & Exit
- Built-in `readFile(path String) String` — wraps `fopen`/`fseek`/`ftell`/`rewind`/`fread`/`fclose`; returns a heap-allocated `%suru.Seq` String header
- Built-in `writeFile(path String, content String)` — wraps `fopen`/`fwrite`/`fclose`
- Built-in `exit(code Int64)` — calls libc `exit`; truncates Int64 to i32
- Explicit `fn main(args Array) Int64` entry point: user-defined `main` is compiled as `suru_main` (internal linkage); a C `int main(int argc, char** argv)` wrapper is emitted that builds a Suru `Array<String>` from argv (one `%suru.Seq` String header per argument) and calls `suru_main`
- Fixed `ToI64`/`FromI64` for pointer types (Struct/Array/String) to use `BuildPtrToInt`/`BuildIntToPtr`, enabling correct round-trip storage of pointer-typed elements in arrays
- New libc externals declared: `fopen`, `fclose`, `fseek`, `ftell`, `rewind`, `fread`, `fwrite`, `exit`
- Test fixtures `tests/fixtures/file_io/main.suru`, `tests/fixtures/file_io_write/main.suru`, `tests/fixtures/exit_test/main.suru`; `FileIoTests` integration tests covering readFile, writeFile, and exit code
- **Milestone:** a Suru program can read `args.at(1)` as a file path, call `readFile`, and print the content; foundation for writing the compiler CLI in Suru

### Added Arrays & Basic Strings
- Lex: `[`, `]` tokens; `"..."` string literals with escape sequences (`\n`, `\t`, `\\`, `\"`)
- AST: `ArrayLiteralExpression`, `StringLiteralExpression` nodes
- Parse: `[e1, e2, ...]` array literals; string literal primary expressions
- Type system: `SuruType.Array` and `SuruType.String`; both resolve via `"Array"` / `"String"` type annotations
- Semantic: `_arrayElementTypes` dictionary tracks element type per array variable; homogeneous element type check on array literals; `Int64`, `Float64`, `Bool` recognized as type-static receivers (e.g. `Int64.from(str)`)
- Codegen runtime types: `%suru.Seq = { i64, ptr }` — shared header layout for both Array and String (len + data pointer); heap-allocated via `malloc`
- New libc externals declared: `realloc`, `memcpy`, `strcmp`, `strtol`, `strtod`, `sprintf`, `strlen`, `memcmp`
- Array built-ins: `arr.len()` → `Int64`; `arr.at(i)` → element; `arr.set(val, i)` mutates; `arr.add(v)` appends via `realloc`; `arr.equals(other)` → `Bool` (length + `memcmp`); `arr.slice(from, to)` → new array copy
- String built-ins: `string.len()` → `Int64`; `string.at(i)` → `Int64` (byte value); `string.equals(other)` → `Bool` via `strcmp`; `string.append(other)` → new String; `string.slice(from, to)` → new String
- Static methods: `Int64.from(str)` via `strtol`; `Float64.from(str)` via `strtod`
- Instance methods: `anyValue.toString()` on `Int64`, `Float64`, `Bool` via `sprintf`; `String.toString()` is identity
- `printLn` extended to print `String` values via `printf("%s\n", dataPtr)`
- `clone`/`drop` extended to handle Array (memcpy data buffer / free data + header)
- Test fixtures `tests/fixtures/arrays/main.suru` and `tests/fixtures/strings/main.suru`; `ArrayTests` and `StringTests` integration tests
- **Milestone:** `let s: "1,hello,3"` with `string.len()`, `string.slice()`, `string.equals()`, `Int64.from()`, `toString()`, and array construction all work; foundation for writing a tokenizer in Suru

### Added Structs
- AST: `StructLiteralExpression`, `FieldAccessExpression`, `FieldAssignmentStatement` nodes
- Parse: `{ field: expr, ... }` struct literals; `expr.field` field access (no parens); `recv.field: expr` field assignment; postfix chain now branches on `(` to distinguish method calls from field access
- Semantic: `_structSymbols` table tracking field names and types per variable; field existence checks on access and assignment; `clone`/`drop` call validation; scope save/restore for struct metadata inside function bodies
- Type system: `SuruType.Struct` added; `"Struct"` type name resolves for function parameters and return types
- Codegen: structs are heap-allocated linked lists of `%suru.Field = { ptr name, i32 tag, i64 val, ptr next }` nodes; all field values stored as `i64` (bool zero-extended, float64 bit-cast); `malloc`/`free` declared as LLVM externals; `FieldNodeSize()` uses GEP-from-null trick; struct metadata propagated to `let` variables for compile-time field-index resolution
- Built-in `clone(x)` — deep-copies the field-node list; built-in `drop(x)` — frees each node
- Test fixture `tests/fixtures/structs/main.suru`; `StructTests` integration test
- **Milestone:** `let data: { tall: true, height: 2283 }` with field read/write, `clone`, `drop`, and `fn identity(d Struct) Struct` pass-through all work correctly

### Added Functions
- Lex: `fn`, `return`, `void` keywords
- AST: `FunctionDeclaration` (with `FunctionParameter`) and `ReturnStatement` nodes
- Parse: `fn name(param Type, ...) ReturnType { body }` top-level function declarations; `return expr` and bare `return` statements
- Semantic: two-pass analysis — first pass registers all function signatures to support recursion and forward references; second pass type-checks bodies; function-local variable scope; call-site arity and argument type validation; non-void functions require at least one `return`
- Codegen: three-pass `Generate()` — declare all LLVM function signatures (pass 1), emit function bodies (pass 2), emit `main` (pass 3); parameters materialized via `alloca`/`store`; user-defined functions dispatched from `EmitValue` via `BuildCall2`
- Test fixture `tests/fixtures/fibonacci/main.suru`; `FunctionTests` integration test
- **Milestone:** recursive `fibonacci(n Int64) Int64` compiles and runs correctly

### Added Control Flow
- Lex: `match` keyword; `{`, `}` tokens; `_` wildcard token
- AST: `MatchExpression` and `MatchArm` nodes; wildcard arm represented as `Pattern = null`
- Parse: `match cond { pattern: body, ... }` expression; patterns are `true`, `false`, integer/float literals, or `_`; arms separated by `,` or newlines
- Semantic: validate match condition is `Bool`; `equals` and `lessThan` methods infer `Bool` return type
- Codegen: LLVM test-chain with `icmp`/`fcmp`; conditional branches to per-arm basic blocks; `phi` node for expression-form match; statement-form match emits side effects without a phi
- Standard library: `equals(n)` and `lessThan(n)` on `Int64`, `Float64`, and `Bool`
- Test fixture `tests/fixtures/control-flow/main.suru`; `ControlFlowTests` integration test

### Added Arithmetic & Variables
- Lex: `let`, `not`, `and`, `or` keywords; `.` (Dot) and `:` (Colon) tokens
- AST: `LetStatement`, `AssignmentStatement`, `VariableReferenceExpression`, `MethodCallExpression`, `UnaryExpression` (not), `BinaryExpression` (and/or)
- Parse: `let x: expr` and `let x Type: expr` declarations; `x: expr` reassignment; postfix method-call chain `expr.method(args)`; `not`/`and`/`or` operators with correct precedence
- Semantic: symbol table tracking declared variable types; undefined variable error
- Codegen: LLVM `alloca`/`store`/`load` for variables; arithmetic methods `add`, `take`, `multiply`, `split`, `invert` for `Int64` and `Float64`; `BuildSelect` for runtime bool printing; `not`/`and`/`or` boolean codegen
- `SuruType` enum (`Bool`, `Int64`, `Float64`) shared between semantic and codegen layers
- Test fixture `tests/fixtures/arithmetic/main.suru`; `ArithmeticTests` integration test
- `examples/variables.suru` demonstrating variables, arithmetic, and boolean operators

### Added
- Integration test infrastructure: `CompiledFixtures` shared fixture compiles `.suru` files from `tests/fixtures/` into temp binaries; `PrintTests` runs the compiled binary and asserts stdout
- Test fixture `tests/fixtures/print/main.suru` covering `bool`, `i64`, and `f64` output via `printLn`
- Replaced placeholder `UnitTest1` with real integration tests

### Added
- Lexer: identifiers, `true`/`false` keywords, integer literals (i64), float literals (f64), `(`, `)`, `,`
- AST nodes: `BoolLitExpr`, `IntLitExpr`, `FloatLitExpr`, `CallExpr`, `ExprStmt`; `Module` now holds a statement list
- Parser: recursive descent; parses top-level call expressions without a statement terminator
- Built-in `printLn` compiles to a `printf` call; supports `bool`, `i64`, and `f64` arguments
- `examples/hello.suru` — first working Suru program

## [0.1.0] - 2026-04-12

### Added
- Initial solution structure with four projects: `Suru.Compiler`, `Suru.CLI`, `Suru.LSP`, `Suru.Tests`
- `LLVMSharp 20.1.2` dependency in `Suru.Compiler`
- All projects target `net10.0`
- Full compiler pipeline: lexer → parser → semantic analysis → LLVM codegen → native executable
- `suru build <file.suru>` CLI command; output placed in `build/` next to the source file
- Empty `.suru` file compiles to a native executable with `main()` returning 0
