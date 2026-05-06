# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Stage 13a — Semantic Analyzer in Suru: Data Structures

Foundation types and scope-chain helpers for writing the Suru semantic analyzer in Suru itself. No C# changes — entirely new Suru source code and tests.

**New fixture:** `tests/fixtures/suru-semantic/`

- **`suru-semantic.suru`** — five named type declarations and nine helper functions:
  - `SymbolEntry { name String, typeName String }` — one binding in a scope frame
  - `Scope { symbols Array<SymbolEntry>, parent Int64 }` — flat-array scope node; `parent = -1` for module scope
  - `FunctionSig { name String, paramTypes Array<String>, returnType String }` — registered function signature
  - `AnalysisError { message String }` — accumulated semantic error
  - `AnalyzerState { scopes Array<Scope>, functions Array<FunctionSig>, typeNames Array<String>, errors Array<AnalysisError>, currentReturnType String, insideFunction Int64, constants Array<String> }` — full analyzer state
  - `makeAnalyzerState() AnalyzerState` — creates initial state with module scope pushed
  - `pushScope / popScope` — append / slice-remove the innermost scope
  - `declareSymbol` — adds a name→typeName binding to the current scope
  - `lookupSymbol` — walks the parent chain from innermost scope outward; returns `""` when not found
  - `existsInCurrentScope` — checks only the innermost scope (for duplicate-let detection)
  - `addError` — appends to `state.errors`
  - Three field-extractor helpers (`entryName`, `entryTypeName`, `scopeParent`) that force correct SuruType at call sites, working around the codegen's inability to infer String/Int64 types through untyped struct field chains

- **`main.suru`** — five unit tests: `push_pop_roundtrip`, `lookup_finds_nearest`, `lookup_walks_parent`, `lookup_returns_empty`, `add_error`; prints `PASS: <name>` for each

- **`IRSuruSemanticTests.cs`** — one integration test (`Semantic_DataStructures_AllPass`) that compiles and runs the fixture and asserts all five `PASS:` lines appear with no `FAIL:` lines

- All 77 tests pass

---

### Stage 12.5g — Re-enable & Fix Semantic Analysis

`SemanticAnalyzer.Analyze()` is now called on every compile path. Invalid programs are rejected before codegen with meaningful error messages.

**Changes:**
- **`Compiler.ParseAndResolve()`** — calls `SemanticAnalyzer.Analyze(module)` after include resolution; propagates errors through `CompilationResult.Fail` so both `GenerateIr()` and `CompileIR()` surface semantic errors.
- **Block-level scope stack** — `_symbols` and `_structSymbols` (flat dicts) replaced by `_scopes: Stack<Dictionary<string, SuruType>>` and `_structScopes: Stack<Dictionary<string, List<…>>>`. Scope helpers: `PushScope`/`PopScope`, `LookupSymbol` (walks top→bottom), `ExistsInCurrentScope` (current frame only), `DeclareSymbol`, `LookupStructMeta`, `DeclareStructMeta`.
- **`AnalyzeWhileStatement`** — pushes a fresh scope before the body and pops after; `let` names reused across sequential while loops no longer trigger false "already declared" errors.
- **`AnalyzeFunctionDeclaration`** — replaced save/clear/restore pattern with push/pop; module-scope constants remain visible inside functions via natural stack traversal — no explicit re-injection needed. Removed `_insideFunction` field; module scope detected by `_scopes.Count == 1`.
- **`CheckHasReturn`** — new helper that recurses into `while` bodies; a `return` or `exit` anywhere in the function (including inside nested loops) satisfies the non-void return requirement.
- **`SemanticAnalyzerTests.cs`** — 15 new unit tests: undefined variable, constant reassignment, unknown type, duplicate type, duplicate function, missing return, sequential while reuse (block scoping), return inside while, exit inside while, module constants visible in functions.
- All 76 tests pass.

---

### Stage 12.5f — Remove Scalar Boxing

Scalars (Bool, Int32, Int64, Float64) are now stored as raw LLVM types (`i1`, `i32`, `i64`, `double`) in local variables, function parameters, and return values. No heap allocation for scalar literals or arithmetic results.

**Changes:**
- **`LlvmType()`** now returns raw LLVM types for scalars (`i1`/`i32`/`i64`/`double`) instead of always `ptr`.
- **`IsScalar()`** new helper predicate distinguishing the four scalar types from heap types.
- **`EmitValue` for literals** returns raw constants (e.g., `("42", Int64)` not a box ptr).
- **`EmitLoad`** returns raw values for scalar local vars and global constants.
- **Alloca pattern** uses `alloca i64` / `alloca i1` / etc. for scalar local variables.
- **Function params/returns** use raw LLVM types for scalars; void functions remain `ptr`.
- **`FnReturnSuruType`** maps void → `SuruType.Struct` (previously `SuruType.Int64`) to naturally resolve to `"ptr"` via `LlvmType`.
- **Arithmetic/comparison/logical ops** (`EmitBinOp`, `EmitCmp`, `EmitCompare`, `EmitInvert`, `EmitBoolNot`, `EmitBinaryExpr`) operate directly on raw values — no unbox before, no rebox after.
- **Print boundary boxing:** `printLn`/`printError` box scalar args before calling the runtime (`suru_println`/`suru_printerror` still take `ptr`).
- **Array boundary boxing:** `EmitArrayAdd`/`EmitArraySet` box scalar elements; `EmitArrayAt` returns `ptr` (unboxed via annotation-guided coercion at let/assignment/return sites).
- **Struct boundary boxing:** `EmitStructLiteral`/`EmitFieldAssignment` box scalars via `EmitToI64`; `EmitFieldAccess` unboxes scalar results when `fa.ResolvedType` is scalar.
- **Match expressions:** `EmitMatchAsExpression` result alloca uses `alloca {LlvmType(resultType)}`; `EmitMatchTestChain` uses raw values directly — no `UnboxScalar` for known scalar conditions/patterns.
- **String/Array methods returning scalars** (`len`, `ord`, `equals`, `Int64.from`) return raw values; index args (`at`, `slice`) accept raw i64.
- **Annotation-guided coercion** added to `LetStatement`, `AssignmentStatement`, and `ReturnStatement` to handle dynamic `(ptr, SuruType.Struct)` results when the declared type is scalar.
- **`clone(scalar)`** is a no-op (returns raw value); **`drop(scalar)`** is a no-op.
- **`DefaultReturnValue()`** new helper for implicit function returns.
- All 61 tests pass; no heap allocations for scalar literals or arithmetic.

### Stage 12.5e — Update suru-parser Fixture

The `suru-parser.suru` fixture is fully updated to use named types — no `Struct` keyword remains in any `.suru` source file in the repository. The Suru parser now also handles `type` declarations, enabling cross-validation against the Suru lexer source (which contains named type declarations added in Stage 12.5d).

**Changes:**
- **suru-parser fixture:** Added 12 named type declarations at the top of `suru-parser.suru` (`Parser`, `AstNode`, `Param`, `MatchArm`, `AstModule`, `ParseResult`, `TypeAnnResult`, `RetTypeResult`, `ArgsResult`, `ArmResult`, `PatResult`, `StmtResult`). Updated all ~50 function signatures and ~20 `Array<Struct>` references. Stripped per-field type annotations from all struct literals, completing the Stage 12.5c syntax transition for this fixture.
- **`parsePrimaryStruct` fixed:** Removed type-annotation parsing from struct literal fields — the parser now correctly handles the Stage 12.5c `{ field: value }` syntax (no `field Type: value` per-field annotations).
- **`printExprStructLit` fixed:** Changed output from `Field [name typeName]` to `Field [name]` to match the C# `AstPrinter` format exactly.
- **`parseTypeDeclaration` added:** New function (`TOK_TYPE → NODE_TYPE_DECL`) so the Suru parser recognises and prints `type` declarations in the same format as the C# `AstPrinter` (`TypeDeclaration [Name]` / `Field [field] Type [type]`).
- **`main.suru` updated:** `Array<Struct>` → `Array<Token>`, `Struct` → `AstModule`.
- **Tests:** All 61 tests pass; `IRSuruParserTests` (3 tests) all green for the first time.

---

### Stage 12.5d — Remove `Struct` Keyword + Update suru-lexer Fixture

The `Struct` keyword is now a compile-time error. All struct values must use a named type declared with `type`. The suru-lexer fixture has been fully updated with named types, new struct literal syntax, and `TOK_TYPE` support.

**Breaking change:** `let x Struct: { ... }` is no longer valid. Use a named type: `type Foo: { ... }` then `let x Foo: { ... }`.

**Changes:**
- **Semantic:** Removed `"Struct" => SuruType.Struct` from `ResolveTypeAnnotation`. Any use of `Struct` as a type annotation now reports "unknown type 'Struct'".
- **Codegen:** Removed `"Struct" => SuruType.Struct` from `SuruTypeFromAnnotation` in `IRBoxCodeGenerator`. Any use of `Struct` in codegen now throws `NotSupportedException`.
- **suru-lexer fixture:** Rewrote `suru-lexer.suru` and `main.suru` with four named type declarations (`Token`, `TextPos`, `TokenWithState`, `LexState`), updated all 37+ function signatures and local variable annotations, and removed per-field type annotations from all struct literals (completing the Stage 12.5c syntax transition for this fixture). Added `TOK_TYPE = 31` constant and `"type": TOK_TYPE` arm to `keywordKind` so the Suru lexer correctly identifies the `type` keyword.
- **structs fixture:** Updated `tests/fixtures/structs/main.suru` — replaced `Struct` field types and `Array<Struct>` with specific named types (`Lvl1`, `Lvl2`, `Lvl3`, `Array<Lvl1>`).
- **Tests:** 57 tests pass; `IRSuruParserTests` (3 tests) remain broken pending Stage 12.5e.

---

### Stage 12.5c — Typed Struct Instantiation (No Field Annotations)

Per-field type annotations in struct literals have been removed. Field types now come from the surrounding `let`/return type annotation, resolved against `type` declarations. The `Struct` keyword remains valid as a type annotation name for this stage.

**Before (old syntax — now a parse error):**
```suru
let p Struct: { x Int64: 2283, y Int64: 2281 }
```

**After (new syntax):**
```suru
type Point: { x Int64, y Int64 }
let p Point: { x: 2283, y: 2281 }
```

**Changes across all compiler layers:**
- **AST:** `StructLiteralExpression.Fields` is now `IReadOnlyList<(string Name, Expression Value)>` — the `TypeAnnotation` per-field element is gone.
- **Parser:** `ParseStructLiteral` no longer calls `ParseTypeAnnotation()` for each field. Grammar is now `{ name: expr [, name: expr]* }`.
- **AstPrinter:** Struct literal fields now render as `Field [name]` (no type suffix).
- **Semantic analyzer:**
  - `PropagateStructMeta` takes a `typeName` string and derives field types from `_module.TypeDeclarations` instead of the literal. Added `_currentFunctionReturnTypeName` field to thread the return type name into `AnalyzeReturnStatement`.
  - New `ValidateStructLiteralFields` helper: when the variable's type annotation is a named type, validates field names and count against the `TypeDeclaration`; reports errors for unknown field names and wrong field counts.
- **Codegen:**
  - `EmitStructLiteral(lit, typeName)` — new `typeName` parameter; looks up field types by name from `_module.TypeDeclarations` when a named type is present; falls back to inferred types from `EmitValue` for `Struct`-typed or anonymous literals.
  - `IRFunctionCodeGenerator` adds special `LetStatement` and `ReturnStatement` cases that detect struct literals and call `EmitStructLiteral` with the annotation's type name, threading the declared type name through codegen.
  - Added `_currentFnReturnTypeName` field to `IRCodeGenerator`, set/cleared per function in `EmitFunction`.
- **Fixtures:** `tests/fixtures/structs/main.suru` and `tests/fixtures/named-types/main.suru` updated — named types declared, all struct literals rewritten.
- **Tests:** 5 new tests in `IRNamedTypeTests.cs` for Stage 12.5c (parse round-trip, semantic field validation). 17 named-type tests + structs fixture all pass. 49 total tests pass; `IRSuruLexerTests` and `IRSuruParserTests` are temporarily broken (repaired in 12.5d and 12.5e respectively).

---

### Stage 12.5b — Named Type Declarations

Introduced `type` declarations as a first-class Suru language construct. Named types are a prerequisite for typed struct instantiation (Stage 12.5c) and eventual removal of the `Struct` keyword.

**Syntax:**
```suru
// inline
type Point: { x Int64, y Int64 }

// multiline
type Person: {
    name String
    age Int64
}
```

**Changes across all compiler layers:**
- **Lexer:** `type` is now a reserved keyword producing `TokenKind.Type` (not `Identifier`).
- **AST:** New `TypeDeclaration` node (`Name`, `Fields: (Field, TypeAnnotation)[]`). `Module.TypeDeclarations` dictionary populated by the parser and preserved through include resolution.
- **Parser:** `ParseTypeDeclaration()` handles inline and multiline field lists (`,` or newline separated). `Module.TypeDeclarations` index built with last-wins semantics (duplicate errors caught by semantic analysis).
- **Semantic analyzer:** Three-pass analysis — type declarations registered first, then functions, then statements. `ResolveTypeAnnotation` is now an instance method that falls through to `_typeDeclarations` for user-defined names, resolving to `SuruType.Struct`. Duplicate type names reported as errors.
- **AstPrinter:** `TypeDeclaration` nodes render as `TypeDeclaration [Name]` with `Field [name] Type [type]` children.
- **Codegen:** `SuruTypeFromAnnotation` and `FnReturnSuruType` are now instance methods; named types map to `SuruType.Struct` via `_module.TypeDeclarations` lookup.
- **Tests:** 11 new tests in `IRNamedTypeTests.cs` covering lexer, parser, semantic analyzer (unit), and the end-to-end `named-types` fixture. 55 total tests, all passing.

---

### Stage 12.5a — Code Refactoring (File Size < 500 Lines)

Split the two largest source files (`IRCodeGenerator.cs` at 1076 lines, `SuruRuntime.cs` at 989 lines) into focused partial-class files. No behavior changes — all 44 tests pass unchanged.

**`IRCodeGenerator` splits** (was 1076 lines → now 452):
- `IRFunctionCodeGenerator.cs` — `EmitFunction`, `EmitStmt`, `EmitMainWrapper`
- `IRMatchCodeGenerator.cs` — `EmitMatchAsStatement/Expression`, `PeekType/MatchType/MethodType`, `EmitMatchTestChain`
- `IRBoxCodeGenerator.cs` — `BoxBool/Int32/Int64/Float64`, `UnboxBool/Int32/Int64/Float64`, `UnboxScalar`, `BoxValue`, type utilities (`SuruTypeFromAnnotation`, `LlvmType`, `RawLlvmType`, `FnReturnSuruType`), `EscapeStringForIR`, `NextTmp`
- `IRFileIoCodeGenerator.cs` — `EmitArgAt`, `EmitReadFile`, `EmitWriteFile`

**`SuruRuntime` splits** (was 989 lines → now 337): made `static partial class`; each runtime module extracted to its own file:
- `SuruStringRuntime.cs` — `GenerateStringRuntime()`
- `SuruArrayRuntime.cs` — `GenerateArrayRuntime()`
- `SuruStructRuntime.cs` — `GenerateStructRuntime()`

Every `.cs` file in `src/Suru.Compiler/` is now under 500 lines.

### Stage 12 — Suru Parser written in Suru

Complete recursive-descent parser for the Suru language, written in Suru itself (`tests/fixtures/suru-parser/`). Produces an AST that cross-validates exactly against the C# `AstPrinter` output.

- **`suru-parser.suru`** (881 lines) — full recursive-descent parser + AST pretty-printer: token stream management (`currentToken`, `advance`, `consume`, `consumeIf`), expression hierarchy (`parseOrExpr` → `parseAndExpr` → `parseNotExpr` → `parsePostfixChain` → `parsePrimary`), all statement forms (`include`, `fn`, `while`, `return`, `let`, assignment, field-assignment, expression-statement), type annotation parsing (simple and generic `Array<T>`), match arm patterns (literals, negative numbers, wildcard, variable references), and a `printModule` pretty-printer that mirrors `AstPrinter.cs` output exactly.
- **`main.suru`** — entry point: lexes a `.suru` source file via the Stage-9 suru-lexer, runs the Suru parser, and prints the AST.
- **`IRSuruParserTests.cs`** — three integration tests: (1) cross-validates on the `print` fixture; (2) cross-validates on `suru-lexer.suru` (the Stage-12 milestone: a substantial real-world Suru file); (3) validates the parser can parse its own source (>500 lines of output, well-formed).
- All 44 tests pass.

### Universal Tagged-Pointer Value System

Complete overhaul of the runtime value representation. Every Suru value at the LLVM level is now a `ptr` to a heap-allocated object whose **first i64 field is always the type_tag**. This eliminates compile-time metadata dictionaries that were silently producing wrong results when type information was lost across function boundaries.

**Unified type_tag enum** (matches C# `SuruType` ordinals): 0=Bool 1=Int32 2=Int64 3=Float64 4=Struct 5=Array 6=String

- **New `%suru.Box`** (`suru_box.ll`, new) — 16-byte scalar heap wrapper: `{ i64 type_tag, i64 payload }`. Every `Bool`, `Int32`, `Int64`, `Float64` value is heap-allocated in a Box at the point it is produced (literal, arithmetic result, comparison result, etc.) and unboxed only at the point it is consumed (arithmetic operand, condition branch, syscall arg). Functions: `suru_box_bool/int32/int64/float64`, `suru_unbox_bool/int32/int64/float64`, `suru_box_clone`, `suru_println` (dispatches to stdout by type_tag), `suru_printerror` (same, to stderr).

- **`%suru.String`** (`suru_string.ll`) — type_tag field added at offset 0 (TYPE_STRING changed 1→6). Layout: `{ i64 type_tag=6, i64 len, ptr data }` (24 bytes).

- **`%suru.Array`** (`suru_array.ll`) — type_tag field added at offset 0 (TYPE_ARRAY=5). Layout: `{ i64 type_tag=5, i64 elem_tag, i64 len, i64 cap, ptr data }` (40 bytes). `elem_tag` now stores the full SuruType ordinal (not the old 0=scalar/1=ptr binary). `suru_array_at` returns `ptr` (inttoptr of raw i64); `suru_array_add/set` take `ptr` (ptrtoint to store). New `suru_array_clone_dyn` / `suru_array_drop_dyn` read each element's type_tag at runtime and dispatch clone/drop — no compile-time element-type metadata needed. Removed: `suru_array_clone/drop_scalar/string/struct`.

- **`%suru.Field`** (`suru_struct.ll`) — TYPE_STRUCT changed 0→4; field_tag now uses the unified enum. Layout: `{ i64 type_tag=4, ptr name, i32 field_tag, i64 val, ptr next }` (40 bytes). `suru_struct_clone` propagates type_tag from source; `suru_struct_drop` unchanged.

- **`IRCodeGenerator.cs`** — all LLVM values are `ptr`; all allocas are `alloca ptr`. Box helpers (`BoxBool`, `BoxInt32`, `BoxInt64`, `BoxFloat64`, `BoxValue`, `UnboxBool`…`UnboxFloat64`, `UnboxScalar`) replace the old type-dispatch emit paths. Literals box on creation; arithmetic unboxes operands, computes, reboxes result. `printLn`/`printError` emit a single `call void @suru_println/printerror(ptr %val)` — runtime dispatches. Removed `_arrayElementTypes`, `_pendingArrayElemType`; added `_argvVars HashSet<string>` to identify the argv Seq. `EmitMainWrapper` stores `type_tag=6` (String) in the argv Seq header.

- **`IRArrayCodeGenerator.cs`** — GEP indices updated (len@2, cap@3, data@4). `EmitArrayLiteral` mallocs 40 bytes, stores type_tag=5 and full-ordinal elem_tag. `EmitArrayLen` boxes result. `EmitArrayAt` unboxes idx, returns ptr directly. `EmitArraySet`/`EmitArrayAdd` take ptr values. `EmitCloneArrayDispatch`/`EmitDropArrayDispatch` call `suru_array_clone_dyn`/`suru_array_drop_dyn`. `EmitToI64`/`EmitFromI64` simplified to `ptrtoint`/`inttoptr` (all values are ptr).

- **`IRStructCodeGenerator.cs`** — stores `type_tag=4` (not 0) at field 0. field_tag uses `(int)fieldType` (unified enum). Field values stored/loaded as ptrtoint/inttoptr.

- **`IRStringCodeGenerator.cs`** — `EmitStringLen`, `EmitStringOrd`, `EmitInt64FromString` box their raw i64 results. `EmitStringAt` / `EmitStringSlice` unbox index args. `EmitStringEquals` boxes the i1 result.

- **`SemanticAnalyzer.cs`** — removed 6 array metadata fields (`_arrayElementTypes`, `_functionArrayParamMeta`, `_functionReturnArrayMeta`, `_arrayStructElementTypes`, `_functionReturnArrayStructSymbols`, `_functionArrayStructParamMeta`) and `PropagateArrayMeta`. `_structSymbols` and `_functionReturnStructSymbols` retained (needed for `fa.ResolvedType` which drives unboxing type in `EmitFieldAccess`).

- **`SuruRuntimeDeclarations.cs`** — updated declares: `suru_array_at` returns `ptr`, `suru_array_add/set` take `ptr`. Added box/unbox/print declares. Removed static clone/drop variant declares.

- **`Compiler.cs`** — four runtime modules linked: `suru_box`, `suru_string`, `suru_array`, `suru_struct`.

### Match on Variables and Constants

Match arm patterns now accept any identifier in addition to literals. The named variable (local or module-level constant) is loaded at runtime and compared against the match condition using the same `icmp`/`fcmp`/`strcmp` logic as literal patterns — no codegen changes were needed.

- **Parser** (`Parse/Parser.cs`) — `ParseMatchPattern()` now handles `TokenKind.Identifier` by returning a `VariableReferenceExpression`; any defined local variable or module-level constant can appear as a match pattern.
- **SemanticAnalyzer** (`Semantic/SemanticAnalyzer.cs`) — `AnalyzeExpression` for `MatchExpression` now calls `AnalyzeExpression(arm.Pattern)` for each non-wildcard arm so undefined variable patterns are caught at compile time.
- **`tests/fixtures/control-flow/main.suru`** — two previously-commented TODO blocks are now active: one matching against module-level constants (`CONST_MONDAY`, `CONST_FRIDAY`), one matching against local variables (`monday`, `friday`). Both match `5` against `CONST_FRIDAY`/`friday` and print `Friday`.
- **`IRControlFlowTests`** / **`ControlFlowTests`** — expected output updated from `…-1\n` to `…-1\nFriday\nFriday\n`.

### Suru Runtime Modules

Extracted all non-trivial string, array, and struct operations from inline per-function IR codegen into three standalone LLVM runtime modules (`suru_string.ll`, `suru_array.ll`, `suru_struct.ll`). User `.ll` files now emit only `declare` stubs; the linker resolves the symbols at link time.

- **`SuruRuntime`** (`Codegen/SuruRuntime.cs`, new) — static class with three methods returning raw LLVM IR strings: `GenerateStringRuntime()`, `GenerateArrayRuntime()`, `GenerateStructRuntime()`. Each method returns a complete `.ll` module with all function definitions for that domain. `CompileIR` writes these into the build directory on every build.

- **`SuruRuntimeDeclarations`** (`Codegen/SuruRuntimeDeclarations.cs`, new) — tracks which Suru runtime `declare` stubs are needed in a user module. Same idempotent Add* pattern as `Externals`. Covers all string functions (`suru_string_create/clone/drop/append/at/equals/slice/ord`, `suru_int64_from_string`, `suru_int64_to_string`), all array functions (`suru_array_at/set/add/slice`, `suru_array_clone_scalar/string/struct`, `suru_array_drop_scalar/string/struct`), and all struct functions (`suru_find_field`, `suru_struct_clone/drop`). `IRCodeGenerator` holds a `private readonly SuruRuntimeDeclarations _runtimeDecls` field; `Emit()` appends `_runtimeDecls.ToString()` after the `_externals` block.

- **`suru_string.ll`** — defines 10 functions: `suru_string_create` (malloc Seq header, store data+len), `suru_string_clone` (malloc Seq+buffer, memcpy), `suru_string_drop` (free buffer, free Seq), `suru_string_append` (malloc new buffer, memcpy both halves), `suru_string_at` (malloc 2-byte buffer, copy 1 char), `suru_string_equals` (strcmp wrapper returning i1), `suru_string_slice` (malloc slice buffer, memcpy range), `suru_string_ord` (load first byte as i64), `suru_int64_from_string` (strtol on data ptr), `suru_int64_to_string` (two-call snprintf pattern).

- **`suru_array.ll`** — defines 10 functions: `suru_array_at/set` (GEP into data buffer), `suru_array_add` (grow branch with two `select` instructions for cap==0→4, cap<1024→cap×2, cap≥1024→cap+1024; realloc updates header in-place), `suru_array_slice` (malloc new header+buffer, memcpy range), `suru_array_clone_scalar` (malloc new header+buffer, memcpy all elements), `suru_array_clone_string/struct` (loop calling `suru_string_clone` / `suru_struct_clone` per element), `suru_array_drop_scalar` (free data+header), `suru_array_drop_string/struct` (loop calling `suru_string_drop` / `suru_struct_drop` per element, then free data+header). Cross-module calls to `suru_string_clone/drop` and `suru_struct_clone/drop` resolve at link time.

- **`suru_struct.ll`** — defines 3 functions: `suru_find_field` (phi-loop through linked list, strcmp to match name), `suru_struct_clone` (loop: malloc 32 bytes per node, copy name/tag/val slots, wire `next`; tracks head via alloca), `suru_struct_drop` (loop: load `next` before `free` to avoid use-after-free).

- **`IRStringCodeGenerator.cs` rewritten** — all complex string operations replaced with `_runtimeDecls.Add*()` + `call @suru_string_*`. Kept inline: `EmitExtractStringData`, `EmitExtractStringLen`, `EmitStringLen`, `EmitStringLiteralValue` (references module-local `[N x i8]` globals). Removed: all direct `_externals.Add*()` calls from string/conversion methods; `_boolStringGlobals.AddFmtIntRaw()` from `EmitInt64ToString`.

- **`IRArrayCodeGenerator.cs` rewritten** — `EmitArrayAt/Set/Add/Slice` emit `call @suru_array_*`; `EmitCloneArrayDispatch`/`EmitDropArrayDispatch` dispatch to `@suru_array_clone_scalar/string/struct` based on element type. Removed: `EmitCloneArray`, `EmitDropArray`, `EmitCloneArrayShallow`, `EmitDropArrayShallow`, `_arrayCounter`.

- **`IRStructCodeGenerator.cs` rewritten** — `EmitFindFieldCall` uses `_runtimeDecls.AddFindField()`; `EmitCloneStruct`/`EmitDropStruct` emit `call @suru_struct_clone` / `call void @suru_struct_drop`; dispatch wrappers `EmitCloneStructDispatch`/`EmitDropStructDispatch` added. Removed: `EnsureFindFieldHelper`, `EmitFindFieldHelper` (inline phi-loop), `_structCounter`. The `_helpers` StringBuilder is retained but no longer used for `suru_find_field`.

- **`IRCodeGenerator.cs`** — added `_runtimeDecls` field; removed `_findFieldEmitted`, `_structCounter`, `_arrayCounter` fields; `clone`/`drop` struct dispatch sites updated to call `EmitCloneStructDispatch`/`EmitDropStructDispatch`.

- **`Compiler.CompileIR`** — updated from 3-step to 4-step build: step 3 generates and compiles the three runtime modules into the build directory; step 4 links all objects. Runtime objects are always linked unconditionally.

### LLVM Module Model for Includes

Refactored the include system from a single merged IR module to the standard LLVM separate-compilation model. Each `.suru` source file now compiles to its own `.ll` and `.o`; the linker resolves cross-module symbol references.

- **`Module.ExternalFunctions`** (new) — `IReadOnlyDictionary<string, string>` populated by `ResolveIncludes`; maps every Suru-qualified call name (`"ns.fn"`) to the original LLVM symbol name (`"fn"`). Codegen uses this table in two places: `EmitFunction` emits `declare @fn(...)` instead of a `define` body for imported functions; `EmitUserFunctionCall` calls `@fn` (not `@ns.fn`) at call sites. The namespace is a Suru language concept only and does not appear in any LLVM IR.

- **`Module.IncludedSourcePaths`** (new) — `IReadOnlyList<string>` of absolute source file paths, collected transitively by `ResolveIncludes`. Paths are deduplicated: each file appears at most once regardless of how many include chains reach it. `CompileIR` iterates this list to compile every included file into its own `.o`.

- **`ResolveIncludes` rewritten** — still merges included `FunctionDeclaration`s (with `"ns."` prefix) into the main module's statement list for semantic analysis; additionally populates `ExternalFunctions` and `IncludedSourcePaths`. Transitive includes are collected by merging `includedModule.IncludedSourcePaths` into the outer set.

- **`CompileIR` three-step pipeline** — (1) compile main source to `.ll`/`.o`; (2) for each path in `module.IncludedSourcePaths`, call `new Compiler(path).GenerateIr()` and compile the resulting standalone IR to its own `.o`; (3) link all `.o` files together with `cc`. Each included file's standalone IR is produced by the same full front-end pipeline (lex → parse → include resolution → semantic → codegen), so transitive includes appear as `declare` stubs in that file's own IR.

- **`Link` signature updated** — now accepts `IReadOnlyList<string> objectPaths`; passes all objects to `cc` on a single command line.

- **`EmitFunction` external-function guard** — if the function name is a key in `_module.ExternalFunctions`, emits a `declare` with the original symbol name and returns immediately; no function body is emitted in the importing module.

- **`EmitFunction` external linkage** — user-defined functions now use `define <rettype> @name(...)` (external linkage); the previous `define internal` made functions invisible across compilation unit boundaries.

- **`@main` wrapper guard** — `EmitMainWrapper` is only called when the current module contains a `fn main` declaration; included modules compiled standalone no longer produce a broken C `main` referencing an undefined `@suru_main`.

### String IR: self-contained clone and drop

- **`clone(s)` for String** — `EmitCloneStringDispatch` / `EmitCloneString` (new, in
  `IRStringCodeGenerator.cs`) produce an independent copy of a `%suru.Seq`: a new
  16-byte Seq header and a new heap-allocated char buffer are allocated; `len + 1` bytes
  (including the null terminator) are `memcpy`'d from the source buffer.  The result is
  typed `SuruType.String` and dispatched from `EmitValue` ahead of the Array and Struct
  clone arms.

- **`drop(s)` for String** — `EmitDropStringDispatch` / `EmitDropString` (new, in
  `IRStringCodeGenerator.cs`) free a String's memory in two steps: `free(data)` releases
  the char buffer, then `free(seqPtr)` releases the 16-byte Seq header.  Because
  `EmitStringLiteralValue` always mallocs a fresh char buffer, every Seq's data pointer
  is heap-owned and this sequence is unconditionally safe.  Returns `("0", Bool)` so
  `drop(s)` can appear in both statement and expression position.

- **`EmitCloneStringValue` removed from `IRArrayCodeGenerator.cs`** — the logic is
  now canonical in `EmitCloneString` (string partial class).  `EmitCloneArray` and
  `EmitDropArray` delegate to `EmitCloneString` / `EmitDropString` instead of inlining
  the string memory operations.  The array file no longer contains any string-level GEP
  or malloc logic.

- **`EmitValue` dispatch order** — String clone/drop arms are added before the Array
  and Struct arms so that `PeekType` guards resolve correctly when the argument is a
  `String` variable (all three types map to `ptr` in LLVM, but Suru tracks them as
  distinct `SuruType` values).

### Array IR: dedicated `%suru.Array` struct, amortised growth, clone, and drop

- **New `%suru.Array` type** — arrays now use a dedicated 24-byte IR struct
  `%suru.Array = { i64 len, i64 cap, ptr data }` instead of sharing `%suru.Seq`
  with strings. The separate `cap` field enables amortised growth without
  realloc on every `.add()` call.

- **Amortised `.add(v)` growth** — `EmitArrayAdd` branches once on `len == cap`.
  Inside the grow path two `select` instructions choose the new capacity with no
  additional branches: `cap == 0 → 4`, `0 < cap < 1024 → cap * 2` (doubling),
  `cap ≥ 1024 → cap + 1024` (linear). The data pointer is reloaded from the
  header after the branch merge, since `realloc` may have moved it.

- **`clone(arr)` — deep copy** — `EmitCloneArray` produces an independent copy
  of the array. Scalar element types (`Int64`, `Float64`, `Bool`) are copied via
  a single `memcpy`. Pointer element types (`String`, `Struct`, `Array`) are
  copied element-by-element in a loop:
  - `String` → `EmitCloneStringValue`: new 16-byte Seq header + malloc+memcpy char buffer
  - `Struct` → `EmitCloneStruct` (existing linked-list traversal)
  - Nested `Array` → shallow bitwise copy (nested element type not tracked by SSA value)

  The clone's `cap` is set to `len` (exact fit).  `EmitCloneArrayDispatch`
  extracts the element type from `_arrayElementTypes[variableName]` so the
  correct clone path is selected at each call site.

- **`drop(arr)` — free array memory** — `EmitDropArray` frees the data buffer
  and header. For scalar elements two `free` calls suffice. For pointer elements
  a loop drops each element before freeing the buffer:
  - `String` → `free(data)`, `free(Seq header)` (data is heap-owned, always safe)
  - `Struct` → `EmitDropStruct`
  - Nested `Array` → shallow drop (`free(data)`, `free(header)`)

  `EmitDropArrayDispatch` mirrors `EmitCloneArrayDispatch` for element-type lookup.

- **`IRArrayCodeGenerator.cs` extracted and documented** — all array-specific
  codegen lives in `IRArrayCodeGenerator.cs` as a `sealed partial` slice of
  `IRCodeGenerator`. New helpers `EmitExtractArrayLen`, `EmitExtractArrayCap`,
  `EmitExtractArrayData` use `%suru.Array` GEP offsets 0/1/2 rather than the
  string helpers. `EmitCloneStringValue` and `EmitCloneArrayShallow` /
  `EmitDropArrayShallow` are private IR-emit helpers (not user-visible language
  features). `_arrayCounter` provides unique block-label suffixes for all
  grow/clone/drop sites.

- **String literals now heap-own their data** — `EmitStringLiteralValue` now
  malloc+memcpy's the string bytes from the interned `[N x i8]` global into a
  fresh heap buffer before building the Seq header. Every Seq's `data` field is
  therefore always heap-owned, making `drop(Array<String>)` safe without any
  distinction between literal and computed strings.

- **`PeekType(clone(x))` generalised** — the `clone` pattern in `PeekType` now
  returns `PeekType(arg)` rather than the hardcoded `SuruType.Struct`, so match
  expressions over a cloned array see the correct `Array` return type.

### CLI Inspection Commands

- **`suru lex <file>`** — tokenises a source file and prints every token to stdout (`line:col  Kind  text`), one per line. Runs only the lexer so it succeeds even on syntactically invalid input. Useful for debugging tokenisation without running the full parser.

- **`suru parse <file>`** — runs the lexer, parser, and include resolution, then prints the resulting AST as an indented tree to stdout. Each node kind is on its own line; child nodes are indented two spaces; leaf values (names, literals) appear in `[square brackets]`. Include directives are expanded so the output reflects the fully merged module that the semantic analyser sees.

- **`suru ir <file>`** — runs the full front-end (lex → parse → include resolution → semantic analysis → `IRCodeGenerator`) and prints the generated LLVM IR to stdout. No files are written; `clang` is not invoked. Useful for inspecting or diffing generated IR without triggering a build.

- **`suru` / unknown command** — now prints a usage summary listing all four commands instead of a single-line error.

- **`AstPrinter`** (`Parse/AstPrinter.cs`) — new static class; `Print(Module)` walks every AST node type (all statements and expressions) and returns an indented tree string.

- **`Compiler.LexFile()`** — new public method; returns `CompilationResult<IReadOnlyList<Token>>` (tokens including EOF sentinel, or an error on bad character / missing file).

- **`Compiler.ParseFile()`** — new public method; runs lex + parse + include resolution and returns `CompilationResult<Module>`.

- **`Compiler.GenerateIr()`** — new public method; runs the full front-end through `IRCodeGenerator.Generate()` and returns `CompilationResult<string>`.

- **`CompilationResult<T>`** — new generic variant of `CompilationResult` for stages that produce a typed value rather than an output file path.

- **`Compiler.ParseAndResolve()`** — private helper extracted from `CompileIR`; shared by `ParseFile`, `GenerateIr`, and `CompileIR` so all three stages run identical front-end logic.

- **`Lexer.Tokenize(string source)`** — new public static method; yields all tokens until EOF. `NextToken()` is now `public` (was `internal`).

### Stage 12 Prep — Mandatory Types + Array<T> Generics

- **Mandatory `let` type annotations** — every `let` declaration must now include an explicit type between the variable name and the `:`. `let x Int64: 42` is valid; `let x: 42` is a parse error. This eliminates silent type inference failures and makes every binding self-documenting.

- **`Array<T>` generic syntax** — arrays now carry a type parameter that names their element type: `let nums Array<Int64>: [10, 20, 30]`, `let tokens Array<Struct>: []`. The type parameter is used by the compiler to emit correct `FromI64`/`ToI64` conversions when loading array elements — no more fragile heuristic inference from the first literal element. Accepted element types: `Bool`, `Int32`, `Int64`, `Float64`, `String`, `Struct`.

- **Function signatures with `Array<T>`** — function parameters and return types accept generic array annotations: `fn tokenize(src String) Array<Struct>` and `fn process(items Array<Int64>) void`. The compiler propagates the element type directly from the annotation into `_arrayElementTypes`, replacing the previous `_functionReturnArrayMeta` / `_functionArrayParamMeta` propagation chains for explicitly-typed arrays.

- **`fn main(args Array<String>)`** — the entry point signature is updated to declare the element type of the argv array. The semantic analyzer no longer special-cases `main` to inject `_arrayElementTypes["args"] = String`; the annotation is authoritative.

- **Lexer: `<` and `>` tokens** — two new `TokenKind` values (`LessThan`, `GreaterThan`) for parsing generic type parameters. These tokens are only consumed in type annotation positions and do not conflict with comparison (which uses method calls).

- **`TypeAnnotation` AST node** — a new `sealed record TypeAnnotation(string Name, TypeAnnotation? TypeParam)` replaces the `string?` type annotation field on `LetStatement` and the `string TypeName` field on `FunctionParameter` and `FunctionDeclaration.ReturnType`. `ToString()` renders `Array<Struct>` for diagnostics.

- All 16 fixture files updated to use mandatory type annotations and `Array<T>` where applicable.

- **Line comments** — `//` starts a comment that extends to the end of the line. Comments are stripped in the lexer and may appear anywhere in a Suru source file.

- **Implicit void return type** — a function whose parameter list is followed directly by `{` (no explicit return-type token) now defaults to `void`; `fn main(args Array<String>)` is valid without writing the `void` keyword.

- **LLVMSharp removed; IR-only pipeline** — `CodeGenerator.cs` (LLVMSharp-based backend) deleted; `LLVMSharp 20.1.2` NuGet dependency removed. `Compiler.Compile()` renamed to `Compiler.CompileIR()` — the method now writes a `.ll` text file and invokes `clang -c` to produce the object file instead of calling the LLVM C API directly. `FindClang()` probes `clang`, then `clang-20` through `clang-15` for a usable compiler. `IRCodeGenerator` is now the sole codegen backend.

### Stage 11 Improvements

- **Implicit `return 0` for `main`** — `fn main(args Array) Int64` no longer requires an explicit `return 0` at the end. If the function body falls off without a terminator, the code generator emits `ret i64 0` automatically (both LLVMSharp and IR backends). The semantic analyzer no longer requires a return statement for `main`. All fixtures and README examples updated.

- **Include directive** — `include "relative/path.suru" as ns` imports all functions from another `.suru` file under a namespace alias. Call sites use `ns.fn(args)`. Resolved at compile time: included `FunctionDeclaration`s are merged into the module with a `"ns.fn"` key; LLVM symbols use `ns__fn`. Circular includes are detected and reported as a compile error. `IncludeDirective` AST node; `Module.Namespaces` set; `ResolveIncludes` pre-pass in `Compiler.Compile`; semantic and codegen namespace-call dispatch via `MethodCallExpression` receiver check. Integration test: `tests/fixtures/include-test/`.
- **Module-level constants accessible inside functions** — Module-level `let` bindings (constants) are now visible inside all function bodies. Two bugs fixed: `SemanticAnalyzer` was clearing constants from `_symbols` on function entry; `CodeGenerator` was not emitting module-level lets at all for programs with an explicit `main`. Constants are now emitted as LLVM global variables (pass 0 in `Generate`) and looked up via a new `_globalVars` fallback in `EmitValue`.
- **Suru-lexer fixture uses named constants** — All magic integer token-kind values in `tests/fixtures/suru-lexer/main.suru` replaced with 27 named module-level constants (`TOK_EOF` through `TOK_WHILE`).
- **Negative literal syntax** — `let x: -5` and `let y: -2.5` now parse as expressions. `ParsePrimary` handles `Minus + IntLiteral/FloatLiteral`, mirroring the existing `ParseMatchPattern` logic. Previously negative literals were only supported in `match` pattern positions.
- **Float64 toString precision** — `printLn` and `.toString()` on `Float64` now use `%.15g` (15 significant figures) instead of `%g` (6). Simple literals like `1.2`, `0.1`, and `3.14` still print concisely; values with more than 6 significant figures are now faithfully represented.
- **`printError` built-in** — `printError(val)` writes to stderr. Accepts Bool, Int64, Float64, and String, mirroring `printLn`. Implemented via `fprintf(stderr, ...)` with `@stderr` as an LLVM external global.
- **`exit` is now a terminal statement** — a non-void function whose last reachable statement is `exit(code)` no longer requires an explicit `return`. Semantic analysis counts `exit(...)` as satisfying the return requirement; codegen emits `unreachable` after the call so LLVM sees a properly terminated basic block.

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
