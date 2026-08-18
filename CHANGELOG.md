# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — unit-level front-end tests
- `Source` test helper: `Parse`, `Analyze`, `Analyzed` and `SingleExpression` run lex → parse → analyze over in-memory source text with no file on disk, no LLVM and no C toolchain. The source path is the constant `test.suru`, so diagnostics are asserted in full
- `LexerTests`, `ParserTests` and `SemanticTests` — token shapes, AST shapes and positions, type annotations, and every diagnostic, all in-process
- `LexException`, thrown by the lexer with the standard `file(line,col): message` prefix

### Fixed — unit-level front-end tests
- An unexpected character crashed the CLI with a stack trace: the lexer threw a bare `Exception` that the driver did not catch. It now throws `LexException` and the driver reports it as a compile error

### Changed — unit-level front-end tests
- Semantic diagnostics moved from compiled fixtures to `SemanticTests` over source text; fixtures are reserved for cases that genuinely need files on disk. Removed `SemanticErrorTests` and the `unknown-function`, `wrong-arity`, `unprintable-argument` and `non-call-statement` fixtures. `tests/fixtures/print` remains as the end-to-end codegen → link → run check

### Added
- `SuruType` — a named type representation (`bool`, `i64`, `f64`, `void`) shared by the semantic analyzer and codegen
- `SourcePosition` on every AST node, so errors after parsing can point at a line and column
- Semantic analysis: resolves `printLn` as a builtin and reports unknown functions, wrong arity, unprintable argument types, and non-call statements — all errors in one run, formatted as `file(line,col): message`
- Negative-compilation test path: `CompiledFixtures.GetErrors(name)` compiles a fixture expected to fail; `SemanticErrorTests` covers each diagnostic
- Fixtures `unknown-function`, `wrong-arity`, `unprintable-argument`, `non-call-statement`

### Changed
- `Parser.Parse` takes a `Lexer` instead of a `Tokens` cursor and owns the cursor itself, so tokens are pulled on demand while parsing; `Tokens` is now internal and `Lexer` carries the source path
- `Tokens` lookahead rewritten as a queue of pending tokens; previously `Peek` re-buffered the current token, which made the following `Next` a no-op

- Codegen emits from an expression's resolved type rather than its AST node class, and now interns global string constants
- Codegen throws `CodegenException` instead of silently skipping constructs it does not recognize; the driver reports it as an internal compiler error
- `CodeGenerator` follows the static-entry/private-instance convention used by the other stages

### Added (earlier)
- Integration test infrastructure: `CompiledFixtures` shared fixture compiles `.suru` files from `tests/fixtures/` into temp binaries; `PrintTests` runs the compiled binary and asserts stdout
- Test fixture `tests/fixtures/print/main.suru` covering `bool`, `i64`, and `f64` output via `printLn`
- Replaced placeholder `UnitTest1` with real integration tests

### Added (previous)
- Lexer: identifiers, `true`/`false` keywords, integer literals (i64), float literals (f64), `(`, `)`, `,`
- AST nodes: `BoolLitExpr`, `IntLitExpr`, `FloatLitExpr`, `CallExpr`, `ExprStmt`; `Module` now holds a statement list
- Parser: recursive descent; parses top-level call expressions without a statement terminator
- Built-in `printLn` compiles to a `printf` call; supports `bool`, `i64`, and `f64` arguments

## [0.1.0] - 2026-04-12

### Added
- Initial solution structure with four projects: `Suru.Compiler`, `Suru.CLI`, `Suru.LSP`, `Suru.Tests`
- `LLVMSharp 20.1.2` dependency in `Suru.Compiler`
- All projects target `net10.0`
- Full compiler pipeline: lexer → parser → semantic analysis → LLVM codegen → native executable
- `suru build <file.suru>` CLI command; output placed in `build/` next to the source file
- Empty `.suru` file compiles to a native executable with `main()` returning 0
