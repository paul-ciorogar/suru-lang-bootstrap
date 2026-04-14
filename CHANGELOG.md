# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
