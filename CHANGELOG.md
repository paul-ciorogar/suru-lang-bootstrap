# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — line comments
- `//` starts a comment that runs to the end of the line. It is the only comment syntax: there is no block comment, and a lone `/` is still an unexpected character
- The lexer treats a comment as whitespace and never produces a token for it, so the parser is unchanged. The terminating newline is left for the whitespace path, keeping line and column numbers correct after a comment
- `doc/program-structure.md` documents the syntax and drops the "no comment syntax yet" note

### Added — language documentation
- `doc/` — a language reference written for people writing `.suru` programs, kept strictly to what the compiler implements: `doc/README.md` (entry page, first program, table of contents), `doc/program-structure.md` (statements, no terminator, insignificant whitespace, error format), `doc/literals-and-types.md` (`bool`, `i64`, `f64`, `void` and literal syntax) and `doc/printing.md` (`printLn` and each of its diagnostics). Every example and every quoted diagnostic was produced by compiling and running the snippet
- Compiler internals stay in `README.md` and `CLAUDE.md`; `doc/` links out to them rather than repeating them

### Added — stage dumps and IR verification
- Stage dumps, the compiler equivalent of application logging: each stage can print its whole intermediate form, so a bug is found by diffing what the program looked like before and after a stage. Stages are `tokens`, `ast` (after parse), `typed-ast` (after semantic analysis) and `llvm`
- `suru build --dump=<stages>` (also `--dump-<stage>` and a bare `--dump` for all), plus the `SURU_DUMP` environment variable — always compiled in, off by default, so a release build can be asked for a dump without a rebuild. Dumps go to stderr, leaving stdout for the build result
- `DumpOptions` carries the enabled stages and the destination writer; the compiler never chooses a destination itself, so only the CLI prints. `DumpOptions.Off` is the default and makes every stage check a flag test
- `AstPrinter` renders a `Module` as an indented tree, optionally annotating each expression with its resolved type. Semantic analysis annotates in place, so the after-parse and after-analysis dumps diff line for line. The typed dump is written before the error check, so a failed analysis still shows how far typing got
- `TokenPrinter` lexes the source a second time rather than teeing the parser's cursor: lexing is context-free here, so the result is identical while the pull-based path stays untouched. A lex error is reported inline and ends the dump
- LLVM module verification after codegen, run unconditionally — malformed IR is now reported as an internal compiler error at the stage that produced it instead of crashing the emitted executable. The LLVM dump is written before verification, since a module that fails is the one worth reading
- `DumpTests` asserts the full text of every dump, and `DumpIntegrationTests` covers the LLVM dump and stage ordering over a real compile

### Changed — stage dumps and IR verification
- `Compiler` takes an optional `DumpOptions`; the existing single-argument constructor still means "no dumps"
- `CompiledFixtures.FindFixturePath` is now the public `CompiledFixtures.FixturePath`, for tests that drive the compiler with their own options

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
