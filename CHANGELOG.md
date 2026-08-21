# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — reactive test directives and `suru test`
- `#` test directives, the first step towards Suru's interactive ambitions: a test lives in the program it tests rather than in a file somewhere else. `#mock <name>: <value>` stubs a binding, `#view <expression>:` prints a value **into your own source line**, and `#assert(<actual>, <expected>)` compares two values and records the outcome in its line too. `#view` and `#assert` share one rule — everything after the colon on a directive line belongs to the compiler, and rewriting is idempotent, so an unchanged file is rewritten byte for byte
- `suru test <file.suru>`, a second CLI command beside `build`. It is a mode rather than a flag because `#view` and `#assert` observe values that exist only while the program is running: it compiles with the directives live, links, **runs the executable**, collects what the directives reported, annotates the source, and exits non-zero if any assertion failed. A failure is also printed as an ordinary positioned diagnostic — the annotation is for reading the code, the diagnostic and exit code are for the editor and for CI
- `BuildMode`, and the decision that a production build discards a `#` line **in the lexer**, on the same path as `//`. That is the strong form of "ignored in production": a directive written in syntax a given compiler build does not understand still cannot break a release build. The cost, taken deliberately, is that a mistyped directive is only ever reported by a test build
- `#mock x: v` is the assignment `x: v` restricted to a test build — it takes effect where it is written rather than replacing the binding's initialiser. That is what makes it free: no new scoping, no new codegen, and `AnalyzeStore` is shared with `AssignmentStatement` so the two cannot drift apart in what they accept or in what they say when they refuse. `#mock` is also the only directive that changes what the program computes, so a test build can pass on values production never produces
- `mock`, `view` and `assert` are recognised by the parser after a `#` rather than being keywords, so nothing is taken out of the namespace; a directive must be written on one line, since the expression-continuation rule has no business applying to something a production build treats as a comment
- A test build's executable reports each directive on stdout as a `\x1e`-prefixed record beside the program's own output. No new extern is needed, and there is no interleaving hazard while Suru has no strings: a `printLn` can only emit a number or `true`/`false` and cannot forge a record. That stops holding the day string literals land, which is when this should move to `fprintf(stderr, …)`
- `doc/testing.md`, `todo.md` (the design, including the deferred `#spec`/`#save`), and the fixture `tests/fixtures/directives` — compiled both ways, so the same file proves the directives are absent from a production build and live in a test one

### Changed — reactive test directives and `suru test`
- `Tokens` gained `LastConsumed` and `SkipRestOfLine`. The latter discards source text without lexing it, which is what lets an already-annotated directive be read back: `fail, got 2` is not an expression, and a large `f64` prints as `1e+20`, which the literal scanner rejects. The parser stops at the colon with nothing buffered and throws the rest of the line away
- `Compiler.Compile` is a thin wrapper over a private `Build(buildDir, mode)` that returns the `Module` alongside the result, since a test run needs it to map a record back to the directive that asked for it
- Codegen's per-type printf specifier moved out of `EmitPrintLn` into `Render`, so `printLn` and the test records share one source of truth for what a value looks like
- `CompiledFixtures.GetTestRun` copies a fixture into the temp build root before running it — a test run rewrites the source it is given, and the file under version control must not be mutated. It runs twice, so the second run has to re-parse what the first one wrote

### Added — blocks and block scope
- `{}` is a fourth kind of statement: it runs the statements inside it in order and, more to the point, is a **scope**. A binding made in a block is visible until the closing `}` and no further, while everything already in scope stays readable and assignable from inside it. Blocks nest to any depth
- Shadowing: a `let` inside a block may reuse a name bound further out, and since it is a new binding rather than an assignment it declares its own type — an `i64` `x` can be shadowed by an `f64` one, with the outer binding untouched and back in scope at the `}`. Shadowing crosses nesting levels only; two `let`s of one name in the same block are still `'x' is already declared`, which is now a per-scope rather than a per-program rule
- `ScopeStack<T>`, one shared innermost-last stack of scopes, replaces the flat symbol table in *both* semantic analysis (keyed to types) and codegen (keyed to stack slots). Codegen needed the same treatment rather than just the analyzer: its table was keyed on the bare name, so a shadowing binding would otherwise have overwritten the outer slot and never given it back. `EnterNew`/`Exit` rather than push/pop — a caller enters a scope, it has nothing to hand in or take back out
- A block is purely lexical: no branch and no new LLVM basic block, so statements keep emitting straight-line into `main`'s single `entry` block and a shadowing binding is simply a second `alloca`. Hoisting allocas to the entry block still only becomes necessary when real control flow arrives
- A block is a statement, not an expression — it yields no value and cannot appear where one is expected. That, and the absence of anything that takes a block as a *body*, is the whole of what it does today; it lands now because control flow and user-defined functions both need this scope stack first
- `doc/blocks.md`, and the fixture `tests/fixtures/blocks` — eight nested scopes each shadowing the same name, printing on the way in and again on the way out, so every level is proved to keep its own slot

### Added — numeric literal separators and base prefixes
- Digit separators: `1_000_000`, and `1_000.000_1` on either side of a float's `.`. A `_` must sit between two digits, so `1_`, `1__0` and `0x_ff` are errors — the separator groups digits, it never stands in for one
- Base prefixes for integers: `0x` hexadecimal, `0b` binary and `0o` octal, each accepting separators too. The prefix must be lowercase — `0X`, `0B` and `0O` are errors (`base prefix 'X' must be lowercase`), so a literal has one spelling and `0o` never has to be told apart from `00`; the hexadecimal digits themselves may still be written in either case. A float is always decimal. Every base shares the one `i64` range, so `0xFFFFFFFFFFFFFFFF` is out of range rather than `-1` — there are no unsigned types to reinterpret it as
- Signed literals: a `-` directly in front of a number is part of the literal rather than the negation operator applied to it, so `-1` is the literal −1 and the `i64` minimum, −9223372036854775808, can be written at last. The fold happens only where an operand is expected, so `1 - 2` still subtracts and `-count` is still the operator
- A literal is now decoded by the lexer rather than the parser: only the lexer knows the base, and it has the line and column to report a bad digit against. `Token` carries the decoded `IntMagnitude`/`FloatValue` alongside the raw lexeme, which stays in `Text` so token dumps and diagnostics still show what was written. The magnitude is unsigned and the sign is applied by the parser, which is the only stage that can tell the negated minimum from an out-of-range positive

### Changed — numeric literal separators and base prefixes
- An integer literal out of range for `i64` is reported as `integer literal is out of range for 'i64'`, closing the documented gap where it crashed the compiler with an unhandled overflow. It comes from the lexer for a magnitude past 9223372036854775808 and from the parser for that magnitude written without its sign
- A letter, digit or `_` butted up against the end of a literal is a lex error (`invalid digit 'i' in decimal literal`) instead of lexing as a separate identifier. `1i64` used to reach semantic analysis as `1` and an unknown variable `i64`; the same rule is what keeps `0xffz` from splitting into `0xff` and `z`

### Added — binary expressions, bindings and assignment
- Operators: `+ - * / %`, the comparisons `= <> < <= > >=`, and the word operators `and`, `or`, `not`. Equality is a single `=`, not-equal is `<>`, and prefix `-` negates. Integer `/` is truncating division yielding an `i64`; `%` is its remainder
- **No operator precedence.** Every binary operator folds left to right, so `1 + 2 * 3` is `(1 + 2) * 3` — a Suru expression is a recipe, a list of steps in order, not a formula to untangle. Parentheses now group an expression and are the only way to say otherwise
- Line continuation falls out of the same rule: an expression continues while the next token is a binary operator, wherever it sits, so a line beginning with an operator joins the line above. No newline tracking is needed in the lexer, since no statement can begin with a binary operator
- Bindings: `let <name> <type>: <expression>`, with the type written out — never inferred. Assignment is `<name>: <expression>`. The parser tells an assignment from a call by the `:` that follows the name, using the existing one-token lookahead
- Semantic analysis gains a symbol table (one flat scope — there are no blocks yet) and the diagnostics for it: `unknown type`, `already declared`, `cannot bind a value of type`, `cannot assign a value of type`, `unknown variable`, and `operator '<op>' cannot be applied to` for both binary and unary operands. No type converts implicitly, so both operands of an operator must already agree
- Codegen gains stack slots: a binding is an `alloca` plus a `store`, a use is a `load`. `and`/`or` evaluate both sides — no expression can have a side effect yet, so short-circuiting waits for user-defined functions
- `doc/expressions.md` and `doc/bindings.md`, and the fixture `tests/fixtures/expressions` covering the lot end to end

### Changed — binary expressions, bindings and assignment
- A bare identifier is now an expression (a variable use), so `printLn 1` parses as two statements instead of failing with "expected LeftParen"; the missing parentheses are reported by semantic analysis instead. A call is still an identifier followed by `(`
- A lone `/` is division rather than a lex error; `//` is still a comment
- `PrintTests`' process runner moved to a shared `Executable.Run`, now that a second integration test needs it

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
