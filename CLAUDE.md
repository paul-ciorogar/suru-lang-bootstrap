# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Bootstrap compiler for **Suru Lang** — a minimalist, library-driven, general-purpose language with structural typing and no garbage collection, designed around LSP-driven interactive development. Written in C# (`net10.0`), emitting native executables via LLVM.

The language is at a very early stage: a program is a sequence of bindings (`let x i64: 1`), assignments (`x: 2`), calls and `{}` blocks, with operators but **no operator precedence** — every binary operator folds left to right (see [tests/fixtures/expressions/main.suru](tests/fixtures/expressions/main.suru)). A block is a scope and allows shadowing (see [tests/fixtures/blocks/main.suru](tests/fixtures/blocks/main.suru)), but it is a statement, not an expression. `if <expr> <block> [else <block>]` is the only control flow (see [tests/fixtures/if/main.suru](tests/fixtures/if/main.suru)); `else if` is not a form of its own but an `else` whose body is another `if`, so it costs no case past the parser. There are no user-defined functions and no loops.

Tests live in the program they test, as `#` directives a production build never lexes and `suru test` executes — `#mock`, `#view` and `#assert` (see [doc/testing.md](doc/testing.md) and [todo.md](todo.md), which also holds the deferred `#spec`/`#save` design).

## Commands

```bash
dotnet build Suru.slnx                          # build all projects
dotnet test                                     # run all tests
dotnet test --filter PrintTests                 # run one test class
dotnet test --filter FullyQualifiedName~PrintsExpectedOutput   # run one test

dotnet run --project src/Suru.CLI -- build path/to/file.suru
dotnet run --project src/Suru.CLI -- test  path/to/file.suru          # build with '#' directives, run, annotate
dotnet run --project src/Suru.CLI -- test --timeout=5000 file.suru    # also --connect-timeout=<ms>
dotnet run --project src/Suru.CLI -- build --dump path/to/file.suru   # all stage dumps to stderr
dotnet run --project src/Suru.CLI -- build --dump=ast,llvm file.suru  # or --dump-ast, SURU_DUMP=ast
```

`suru build <file.suru>` writes the object file and executable to a `build/` directory **next to the source file**.

`suru test <file.suru>` **rewrites the source file** — never point it at anything you are not prepared to have annotated.

Linking shells out to `cc`, so a C toolchain must be on `PATH`. LLVM comes from the `LLVMSharp` NuGet package.

## Architecture

Four projects (`Suru.slnx`): `Suru.Compiler` (all the logic), `Suru.CLI` (thin arg-parsing entry point), `Suru.LSP` (empty scaffold — no sources yet), `Suru.Tests`.

The pipeline lives entirely in `Compiler.Build` ([Compiler.cs](src/Suru.Compiler/Compiler.cs)) and runs in fixed order. `Compile` is `Build` in [BuildMode](src/Suru.Compiler/BuildMode.cs)`.Production`; `Test` is `Build` in `Test` mode plus a sixth step — it *runs* the executable, since `#view` and `#assert` observe values that only exist at runtime:

1. **Lex** — [Lexer](src/Suru.Compiler/Lex/Lexer.cs) is a pull-based scanner producing one `Token` at a time; it is never materialized into a list.
2. **[Tokens](src/Suru.Compiler/Lex/Tokens.cs)** is an internal cursor over the lexer (`Current`/`Next`/`Peek`/`PeekN`). Lookahead is a queue of pending tokens that `Next` drains before pulling from the lexer again — the parser gets arbitrary lookahead over a streaming lexer.
3. **Parse** — [Parser](src/Suru.Compiler/Parse/Parser.cs), recursive descent, static `Parse(Lexer)` entry with a private instance. The parser constructs its own `Tokens` cursor and pulls tokens on demand; nothing between the lexer and the AST is materialized. Produces a [Module](src/Suru.Compiler/Parse/Ast/Module.cs) of `Statement`s. Errors throw `ParseException`; the driver catches it and converts to a `CompilationResult` failure. There is no statement terminator.
4. **Semantic** — [SemanticAnalyzer](src/Suru.Compiler/Semantic/SemanticAnalyzer.cs) annotates every expression with its `SuruType` and tracks bindings in a [ScopeStack](src/Suru.Compiler/ScopeStack.cs) — the shared innermost-last scope structure a `BlockStatement` enters and exits, which codegen uses too for the matching stack slots; same static-entry/private-instance shape. It returns a list of error strings rather than throwing, so one run reports every problem.
5. **Codegen** — [CodeGenerator](src/Suru.Compiler/Codegen/CodeGenerator.cs) emits an LLVM module directly from the AST (no IR of its own). Everything is emitted into a single `main`, split into an `entry` block that holds nothing but `alloca`s (built through a second builder parked there for the whole run) and a `body` block that holds the code — so a binding's slot belongs to the frame while its `store` stays where the statement sits. `if` is the only thing that emits further blocks (`if.then` / optional `if.else` / `if.end`), taking the enclosing function from `_builder.InsertBlock.Parent`; every arm falls through to the end, since nothing can leave one early. `printLn` is special-cased in `EmitPrintLn` into a `printf` call with a per-type format string, which is now `Printf`'s only caller. Instructions are chosen from an expression's resolved type, not its node class. There is no function-declaration support yet. In `BuildMode.Test` — the one thing codegen is told its mode for — it also declares the test runtime's `suru_frame_begin`/`suru_field`/`suru_frame_end` and emits every `#view`/`#assert` through `WriteFrame` as a frame rather than a `printf`, plus a `run-started` at the top of `body` and a `run-finished` before the `ret`.
5b. **Run + collect** (test mode only) — [Testing/](src/Suru.Compiler/Testing/) holds the `#` directive machinery downstream of codegen. The emitted binary reports over a **unix socket**, not stdout: [TestChannel](src/Suru.Compiler/Testing/TestChannel.cs) binds one under the temp directory, starts accepting, then starts the child with `SURU_TEST_CHANNEL` in its environment, and drains the socket and the child's stdout **concurrently** — sequentially would deadlock the moment either pipe fills. The C shim [runtime/suru_rt.c](runtime/suru_rt.c) is the program's end of it, linked in by the same `BuildMode` decision and located by [RuntimeShim](src/Suru.Compiler/Testing/RuntimeShim.cs); the wire is [doc/test-protocol.md](doc/test-protocol.md), spelled by [FrameProtocol](src/Suru.Compiler/Testing/FrameProtocol.cs) and read by [FrameReader](src/Suru.Compiler/Testing/FrameReader.cs) into [Frame](src/Suru.Compiler/Testing/Frame.cs)s. [TestRun](src/Suru.Compiler/Testing/TestRun.cs) takes it from there — matching each frame to its directive by id and rewriting the source line — and knows nothing about how a frame travelled. Two deadlines, `--connect-timeout` (200 ms) and `--timeout` (1000 ms), because a binary that never connected and a program that never ended are different failures; every channel failure is its own sentence in a `TestChannelException`, and a run that throws one leaves the source file **unwritten**. `run-finished` is what separates `undefined` (the run ended without reaching the directive) from a crashed run (nothing is known, and the line is blanked). A line carries one directive, **enforced** — a directive ends at its terminator (the `:` of a `#view`, the `)` of a `#assert`) and the rest of the line is discarded without being lexed, and `Parser.RequireLineToItself` rejects a second `#` on the line rather than letting it work. `#mock` has no annotation and so no discard: its expression ends it, and anything after that on the line is an error. `BuildMode` is decided in the **lexer**: `Production` skips a `#` line exactly like `//`, so no later stage can be broken by a directive it does not understand.
6. **Emit + link** — the module is verified (`TryVerify`, always, not gated on a flag) so malformed IR is reported as an internal compiler error rather than crashing the emitted binary; then target machine from `LLVMTargetRef.DefaultTriple`, object file, and `cc` to link — handed `runtime/suru_rt.c` as a second source in test mode, so a test build carries the channel and a production build cannot.

**Debugging** follows the compiler convention of dumping whole intermediate forms per stage rather than logging events — see [src/Suru.Compiler/Debug/](src/Suru.Compiler/Debug/). `Dump` is a `[Flags]` enum of stages (`tokens`, `ast`, `typed-ast`, `llvm`); `DumpOptions` pairs the enabled set with a `TextWriter` the CLI supplies, and its `Section(stage, title, body)` takes the body as a callback so a disabled stage never runs its printer. `DumpOptions.Off` is the default. `AstPrinter` serves both AST dumps — semantic analysis annotates in place, so `withTypes: true` is the only difference and the two dumps diff line for line. `TokenPrinter` re-lexes the source instead of teeing `Tokens`, keeping the parser's pull path untouched.

Failure convention: every stage funnels into [CompilationResult](src/Suru.Compiler/CompilationResult.cs) (`Ok`/`Fail`); only the CLI prints anything.

## Tests

Two layers. **Prefer the unit layer** — reach for a fixture only when the case genuinely needs a file on disk, codegen, or a real executable.

The test project mirrors `src/`: [Compiler/](tests/Suru.Tests/Compiler/) holds the tests for `Suru.Compiler` in the same subfolders its sources live in (`Lex/`, `Parse/`, `Semantic/`, `Debug/`, `Testing/`, with the ones for loose files like `ScopeStack` at its root), [Lib/](tests/Suru.Tests/Lib/) the tests for `Suru.Lib`, and [Integration/](tests/Suru.Tests/Integration/) the ones that compile, link and run a real binary, since those belong to no single source file. Namespaces follow the folders (`Suru.Tests.Compiler.Lex`, `Suru.Tests.Integration`, …). The shared helpers — [Source](tests/Suru.Tests/Source.cs), [CompiledFixtures](tests/Suru.Tests/CompiledFixtures.cs), [Executable](tests/Suru.Tests/Executable.cs) — stay at the root in `Suru.Tests`, which every nested namespace encloses, so they need no `using`.

**Unit tests over source text** ([LexerTests](tests/Suru.Tests/Compiler/Lex/LexerTests.cs), [ParserTests](tests/Suru.Tests/Compiler/Parse/ParserTests.cs), [SemanticTests](tests/Suru.Tests/Compiler/Semantic/SemanticTests.cs)) run the front end in-process with no LLVM and no `cc`. They go through [Source](tests/Suru.Tests/Source.cs):

- `Source.Parse(text)` → `Module`, `Source.Analyze(text)` → error list, `Source.Analyzed(text)` → `Module` asserted error-free (for checking `Expression.Type`), `Source.SingleExpression(module)` unwraps the lone statement.
- The source path is always the constant `Source.Path` (`"test.suru"`), so diagnostics can be asserted with `Assert.Equal` on the whole string.
- The lexer is exercised through the parser — `Lexer.NextToken()` and `Tokens` are `internal` and there is no `InternalsVisibleTo`.
- **Assert on a whole dump, not on node types.** A test about AST shape compares `AstPrinter.Print(module)` (or `withTypes: true` for resolved types) against a raw string literal of the expected tree; a test about tokens uses `TokenPrinter.Print(text, Source.Path)`. One `Assert.Equal` then says everything about position, nesting, order and type at once, reads like the tree it checks, and fails with a diff instead of "expected IntLiteral". Prefer that to a chain of `Assert.IsType`/`Assert.Collection`/`Assert.Single` unwrapping, and reach for a hand-written assertion only for what the printers do not render (e.g. `MockDirective.NamePosition`).

**Integration tests** compile real `.suru` fixtures and assert on the executable's stdout ([PrintTests](tests/Suru.Tests/Integration/PrintTests.cs)):

- [CompiledFixtures](tests/Suru.Tests/CompiledFixtures.cs) is an xUnit `ICollectionFixture` shared via the `"Integration"` collection. It compiles each fixture once into a temp dir keyed by GUID and deletes it on dispose. `GetExecutable(name)` expects success; `GetErrors(name)` expects failure.
- Fixtures live at `tests/fixtures/<name>/main.suru`, located by walking up from `AppContext.BaseDirectory`, so a new fixture needs no csproj change.
- `GetTestRun(name)` runs a fixture through `suru test`. It **copies the fixture into the temp build root first**, because a test run rewrites the source it is given; it runs twice, so the second run has to re-parse what the first one wrote.
- To add a case: create the fixture directory, then a test class in `Integration/` marked `[Collection("Integration")]` taking `CompiledFixtures` in its constructor. (The xUnit collection name is global, so [TestModeTests](tests/Suru.Tests/Compiler/Testing/TestModeTests.cs) joins the same collection from `Compiler/Testing/`, where the feature it tests lives.)

## Conventions

- Record changes in [CHANGELOG.md](CHANGELOG.md) (Keep a Changelog format, under `## [Unreleased]`).
- Compiler stages use `static Xxx(...)` factory entry points over private constructors + a `_Xxx()` instance method.
- Nullable and implicit usings are enabled everywhere; `Suru.Compiler` also enables `AllowUnsafeBlocks` for LLVM interop.
