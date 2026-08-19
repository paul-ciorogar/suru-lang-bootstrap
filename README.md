# Suru Lang

> A minimalist, library-driven, data oriented, general-purpose programming language with static typing and no garbage collection.

## Overview

Suru Lang prioritizes interactive development, transforming editors into REPL-like environments through LSP integration. The language emphasizes minimal syntax with maximum expressiveness, enabling developers to write clear, readable code without unnecessary ceremony.

This repository holds the **bootstrap compiler**, written in C# and emitting native executables via LLVM. 

## Requirements

- .NET SDK 10
- A C toolchain — linking shells out to `cc`

LLVM comes from the `LLVMSharp` NuGet package, so no separate LLVM install is needed.

## Building a program

```bash
dotnet run --project src/Suru.CLI -- build path/to/file.suru
```

The object file and executable are written to a `build/` directory next to the source. The examples below shorten the invocation to `suru`.

```suru
printLn(true)
printLn(1)
printLn(1.2)
```

That is close to the whole language today. [doc/](doc/README.md) is the language reference — what a program is, which types and literals exist, and what `printLn` accepts, all limited to what the compiler implements.

## Debugging the compiler

Following normal compiler practice, the compiler does not log events — it dumps the whole intermediate form after each stage, so a bug is found by diffing what the program looked like before and after a stage. The dumps are always compiled in and off by default, and go to stderr so stdout stays usable for the build result.

```bash
suru build --dump file.suru              # every stage
suru build --dump=ast,llvm file.suru     # a comma separated list
suru build --dump-tokens file.suru       # shorthand for a single stage
SURU_DUMP=llvm suru build file.suru      # same list, applied to every run
```

| Stage | Shows |
| --- | --- |
| `tokens` | the token stream in source order, with positions |
| `ast` | the AST as the parser produced it |
| `typed-ast` | the same AST after semantic analysis, annotated with resolved types |
| `llvm` | the generated LLVM IR, in LLVM's textual form |

Because semantic analysis annotates the AST in place, `ast` and `typed-ast` diff line for line — the types are the only thing that changed:

```
===== ast after parse =====
Module main.suru
  ExpressionStatement (1,1)
    CallExpression (1,1) printLn
      IntLiteral (1,9) 1

===== ast after semantic analysis =====
Module main.suru
  ExpressionStatement (1,1)
    CallExpression (1,1) printLn : void
      IntLiteral (1,9) 1 : i64
```

Separately from the dumps, the generated LLVM module is **verified on every build**. Malformed IR is reported as an internal compiler error at the stage that produced it, rather than as a mysterious crash in the emitted executable.

## Development

```bash
dotnet build Suru.slnx          # build all projects
dotnet test                     # run all tests
```

Four projects: `Suru.Compiler` (the pipeline — lex, parse, semantic analysis, codegen), `Suru.CLI` (argument parsing), `Suru.LSP` (scaffold), and `Suru.Tests`. See [CLAUDE.md](CLAUDE.md) for the architecture in detail and [CHANGELOG.md](CHANGELOG.md) for what has landed.
