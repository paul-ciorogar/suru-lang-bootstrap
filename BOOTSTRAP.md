# Bootstrap Compiler v0.1.0

This repository is the **frozen C# bootstrap compiler** for the Suru language.
It was archived after Suru achieved self-hosting — the compiler can compile its own source.

## What is here

- `src/` — C# compiler source (lexer, parser, semantic analyzer, IR codegen via LLVM text IR)
- `tests/` — Full test suite (203 tests)
- `bin/suru-build` — Pre-built bootstrap binary (Linux x86-64)
- `tests/fixtures/suru-build/main.suru` — The Suru compiler written in Suru itself

## Using the bootstrap binary

`bin/suru-build` is the Suru compiler written in Suru and compiled by the C# bootstrap.
It takes a source file and emits LLVM IR — you then link it with the runtime modules:

```sh
chmod +x bin/suru-build

# 1. Emit IR
./bin/suru-build <source.suru> <output.ll>

# 2. Link with the Suru runtime (runtime .ll files are in the build dir after any C# build)
clang <output.ll> suru_box.ll suru_string.ll suru_array.ll suru_struct.ll suru_variant.ll -o <binary>
```

Requires `clang` (tested with clang-15) on your PATH.

## Rebuilding from C# source

```sh
dotnet build
dotnet run --project src/Suru.CLI -- build tests/fixtures/suru-build/main.suru
# output: tests/fixtures/suru-build/build/main
```

Requires .NET 8+ SDK and clang-15.