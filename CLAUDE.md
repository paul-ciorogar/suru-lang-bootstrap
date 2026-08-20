# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Bootstrap compiler for **Suru Lang** — a minimalist, library-driven, general-purpose language with structural typing and no garbage collection, designed around LSP-driven interactive development. Written in C# (`net10.0`), emitting native executables via LLVM.

The language is at a very early stage: a program is a sequence of top-level bindings (`let x i64: 1`), assignments (`x: 2`) and calls, with operators but **no operator precedence** — every binary operator folds left to right (see [tests/fixtures/expressions/main.suru](tests/fixtures/expressions/main.suru)). There are no user-defined functions and no control flow.

## Commands

```bash
dotnet build Suru.slnx                          # build all projects
dotnet test                                     # run all tests
dotnet test --filter PrintTests                 # run one test class
dotnet test --filter FullyQualifiedName~PrintsExpectedOutput   # run one test

dotnet run --project src/Suru.CLI -- build path/to/file.suru
dotnet run --project src/Suru.CLI -- build --dump path/to/file.suru   # all stage dumps to stderr
dotnet run --project src/Suru.CLI -- build --dump=ast,llvm file.suru  # or --dump-ast, SURU_DUMP=ast
```

`suru build <file.suru>` writes the object file and executable to a `build/` directory **next to the source file**.

Linking shells out to `cc`, so a C toolchain must be on `PATH`. LLVM comes from the `LLVMSharp` NuGet package.

## Architecture

Four projects (`Suru.slnx`): `Suru.Compiler` (all the logic), `Suru.CLI` (thin arg-parsing entry point), `Suru.LSP` (empty scaffold — no sources yet), `Suru.Tests`.

The pipeline lives entirely in [Compiler.Compile](src/Suru.Compiler/Compiler.cs) and runs in fixed order:

1. **Lex** — [Lexer](src/Suru.Compiler/Lex/Lexer.cs) is a pull-based scanner producing one `Token` at a time; it is never materialized into a list.
2. **[Tokens](src/Suru.Compiler/Lex/Tokens.cs)** is an internal cursor over the lexer (`Current`/`Next`/`Peek`/`PeekN`). Lookahead is a queue of pending tokens that `Next` drains before pulling from the lexer again — the parser gets arbitrary lookahead over a streaming lexer.
3. **Parse** — [Parser](src/Suru.Compiler/Parse/Parser.cs), recursive descent, static `Parse(Lexer)` entry with a private instance. The parser constructs its own `Tokens` cursor and pulls tokens on demand; nothing between the lexer and the AST is materialized. Produces a [Module](src/Suru.Compiler/Parse/Ast/Module.cs) of `Statement`s. Errors throw `ParseException`; the driver catches it and converts to a `CompilationResult` failure. There is no statement terminator.
4. **Semantic** — [SemanticAnalyzer](src/Suru.Compiler/Semantic/SemanticAnalyzer.cs) annotates every expression with its `SuruType` and holds one flat scope of bindings (there are no blocks or functions to nest one inside yet); same static-entry/private-instance shape. It returns a list of error strings rather than throwing, so one run reports every problem.
5. **Codegen** — [CodeGenerator](src/Suru.Compiler/Codegen/CodeGenerator.cs) emits an LLVM module directly from the AST (no IR of its own). Everything is emitted into a single `main`; a binding is an `alloca` + `store` where the statement sits, and `printLn` is special-cased in `EmitPrintLn` into a `printf` call with a per-type format string. Instructions are chosen from an expression's resolved type, not its node class. There is no function-declaration support yet.
6. **Emit + link** — the module is verified (`TryVerify`, always, not gated on a flag) so malformed IR is reported as an internal compiler error rather than crashing the emitted binary; then target machine from `LLVMTargetRef.DefaultTriple`, object file, and `cc` to link.

**Debugging** follows the compiler convention of dumping whole intermediate forms per stage rather than logging events — see [src/Suru.Compiler/Debug/](src/Suru.Compiler/Debug/). `Dump` is a `[Flags]` enum of stages (`tokens`, `ast`, `typed-ast`, `llvm`); `DumpOptions` pairs the enabled set with a `TextWriter` the CLI supplies, and its `Section(stage, title, body)` takes the body as a callback so a disabled stage never runs its printer. `DumpOptions.Off` is the default. `AstPrinter` serves both AST dumps — semantic analysis annotates in place, so `withTypes: true` is the only difference and the two dumps diff line for line. `TokenPrinter` re-lexes the source instead of teeing `Tokens`, keeping the parser's pull path untouched.

Failure convention: every stage funnels into [CompilationResult](src/Suru.Compiler/CompilationResult.cs) (`Ok`/`Fail`); only the CLI prints anything.

## Tests

Two layers. **Prefer the unit layer** — reach for a fixture only when the case genuinely needs a file on disk, codegen, or a real executable.

**Unit tests over source text** ([LexerTests](tests/Suru.Tests/LexerTests.cs), [ParserTests](tests/Suru.Tests/ParserTests.cs), [SemanticTests](tests/Suru.Tests/SemanticTests.cs)) run the front end in-process with no LLVM and no `cc`. They go through [Source](tests/Suru.Tests/Source.cs):

- `Source.Parse(text)` → `Module`, `Source.Analyze(text)` → error list, `Source.Analyzed(text)` → `Module` asserted error-free (for checking `Expression.Type`), `Source.SingleExpression(module)` unwraps the lone statement.
- The source path is always the constant `Source.Path` (`"test.suru"`), so diagnostics can be asserted with `Assert.Equal` on the whole string.
- The lexer is exercised through the parser — `Lexer.NextToken()` and `Tokens` are `internal` and there is no `InternalsVisibleTo`.

**Integration tests** compile real `.suru` fixtures and assert on the executable's stdout ([PrintTests](tests/Suru.Tests/PrintTests.cs)):

- [CompiledFixtures](tests/Suru.Tests/CompiledFixtures.cs) is an xUnit `ICollectionFixture` shared via the `"Integration"` collection. It compiles each fixture once into a temp dir keyed by GUID and deletes it on dispose. `GetExecutable(name)` expects success; `GetErrors(name)` expects failure.
- Fixtures live at `tests/fixtures/<name>/main.suru`, located by walking up from `AppContext.BaseDirectory`, so a new fixture needs no csproj change.
- To add a case: create the fixture directory, then a test class marked `[Collection("Integration")]` taking `CompiledFixtures` in its constructor.

## Conventions

- Record changes in [CHANGELOG.md](CHANGELOG.md) (Keep a Changelog format, under `## [Unreleased]`).
- Compiler stages use `static Xxx(...)` factory entry points over private constructors + a `_Xxx()` instance method.
- Nullable and implicit usings are enabled everywhere; `Suru.Compiler` also enables `AllowUnsafeBlocks` for LLVM interop.
