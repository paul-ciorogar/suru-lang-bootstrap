# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Stage 15h — Bootstrap Round 2 & Self-Hosting Validation

Fixed a `currentReturnTypeName` leak in `ir-codegen.suru` that caused Binary A to emit `@suru_clone_String` / `@suru_drop_String` vtable pointers in struct literals whenever an `Array<String>.add()` call preceded a struct literal return in the same function. These symbols have no definition (String is a built-in, not a user-declared struct), producing linker errors when compiling Binary B.

**Root cause:** the `.add()` and `.set()` array method arms in `emitMethodCall` set `ctx.currentReturnTypeName = elemST` to propagate the element type into struct literal arguments, but never restored the original value. The leaked element type was then used by `emitStructLiteralAlloc` to compute the malloc size and vtable pointers for the *function's* return struct literal.

**Fix:** save and restore `ctx.currentReturnTypeName` around the `emitValue` call in both the `.add()` and `.set()` arms — the same save/restore pattern used by `narrowCondVar`/`restoreCondVar`.

- `tests/fixtures/suru-codegen/ir-codegen.suru`: save/restore `currentReturnTypeName` in `.add()` and `.set()` arms
- `IRSuruStage15hTests.BinaryBCompilesFromBinaryA`: now passes (Binary B links)
- `IRSuruStage15hTests.BinaryAAndBinaryBProduceIdenticalOutput`: now passes (self-hosting confirmed)

### Codegen Rewrite — Documentation (Task 12)

Completes the Suru-in-Suru codegen rewrite with a documentation pass. All twelve rewrite tasks are now done; `rewrite.md` is fully marked ✅ COMPLETE.

Old heap-codegen files (`heap-codegen-context.suru`, `heap-codegen-primitives.suru`, `heap-type-helpers.suru`, `heap-value-codegen.suru`, `heap-stmt-codegen.suru`) are intentionally preserved — they back the Stage 14a–15g historical tests and are not used by `suru-build`.

### Codegen Rewrite — Suru-in-Suru Codegen Restructure (Tasks 8–11)

Completes the Suru-in-Suru codegen rewrite by implementing `ir-codegen.suru` (the top-level code generator mirroring `IRCodeGenerator.cs + IRFunctionCodeGenerator.cs + IRMatchCodeGenerator.cs`) and wiring `suru-build/main.suru` to use it.

- **`tests/fixtures/suru-codegen/ir-codegen.suru`** (new, ~1709 lines): Top of the new rewrite DAG. Merges all three C# partial-class files because `emitValue ↔ emitMatchExpr ↔ emitStmt ↔ emitFunction` form a mutually-recursive cluster. Section banners mark logical partial-class boundaries. Key public entry point: `generate(stmts, sourceName, ownFnNames, ownTypeNames) String` — initialises `IrCodegenContext`, runs pre-passes, emits all type clone/drop helpers, emits all function bodies, returns the assembled `.ll` text (`header | globalConsts | strGlobals | decls | suruDecls | extCloneDropDecls | helpers | fns`). Pre-emission pattern: `emitMethodCall` pre-emits receiver and all args via `emitValue`, then passes SSA names to string/array/struct/fileio helper files — helpers never call `emitValue`. LLVM dominance invariant: match-expression result alloca is emitted BEFORE the comparison test chain. `emitMatchStmt` guards each merge branch with `ctx.blockOpen` to prevent double-terminator IR. `narrowCondVar`/`restoreCondVar` temporarily update `ctx.vars` to the concrete variant type within each match arm so field accesses resolve correctly.
- **`tests/fixtures/suru-build/main.suru`** (modified): Replaced `include "heap-stmt-codegen.suru" as codegen` + manual context assembly with `include "ir-codegen.suru" as irCodegen` + `irCodegen.generate(stmts, baseName(sourcePath), ownFnNames, ownTypeNames)`. Both `main()` and `compileOneFile()` simplified from ~6 lines of context manipulation to 2 lines.

### Stage 15g — Bootstrap Round 1: Binary A

Extends the `suru-build` smoke test to cover a broader corpus, confirming binary A (the `suru-build` executable compiled by the C# bootstrap compiler) handles recursive functions, while loops, and all string methods correctly.

- **`tests/Suru.Tests/IRSuruStage15gTests.cs`** (new): Three `[Fact]` tests — `FibonacciCompilesToCorrectOutput` (recursive match-expression, output `"0\n1\n5\n55\n"`), `WhileLoopCompilesToCorrectOutput` (loops + string append, output `"1\n2\n3\n4\n5\n55\nxxx\n"`), `StringsCompilesToCorrectOutput` (len/equals/append/slice/at/Int64.from/toString, output `"5\ntrue\nfalse\nhello world\nel\n42\n42\nh\n"`). No codegen gaps found; no Suru source changes required. 178/178 tests green.

### Stage 15f — Complete `suru build` Driver CLI

Creates the canonical Suru compiler source fixture (`tests/fixtures/suru-build/main.suru`) — the entry point for bootstrapping in Stages 15g and 15h — and cross-validates it against `arithmetic.suru`.

- **`tests/fixtures/suru-build/main.suru`** (new): Clean, standalone Suru compiler implementing the full pipeline: lex → parse → resolveIncludes (transitive + diamond) → semantic pre-passes → statement analysis → codegen → writeFile. Reads source from `args.at(1)`, writes `.ll` to `args.at(2)`. Exits code 1 on semantic errors. Shares the same include paths as `suru-codegen-driver-full` (both fixtures sit at the same depth under `tests/fixtures/`).
- **`tests/Suru.Tests/IRSuruStage15fTests.cs`** (new): `ArithmeticCompilesToCorrectOutput` — compiles `suru-build` with the C# bootstrap compiler, runs it on `arithmetic/main.suru`, links the emitted `.ll` with all five Suru runtime modules, and asserts output `"5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n"`. 175/175 tests green.

### Stage 15e — Semantic Analysis Integration in Driver

Inserts semantic analysis into the `suru-codegen-driver-full` pipeline between include resolution and codegen, so the driver rejects invalid programs with exit code 1 before emitting any IR.

- **`tests/fixtures/suru-codegen-driver-full/main.suru`** (modified): Added includes for `suru-semantic.suru as semantic`, `suru-semantic-passes.suru as passes`, `suru-semantic-stmts.suru as semstmts` (alias avoids collision with the `stmts` Array variable), `suru-semantic-fns.suru as fns`. Added `collectNonInclude` helper (filters IncludeNode entries from the raw parsed module so statement analysis runs only on the main file's own code). Copied `analyzeModuleStatement`, `analyzeModuleStatements`, and `printErrorsFrom` from `suru-check/main.suru`. Extended `main()`: after `resolveIncludes`, runs `passes.runPrePasses` on all resolved declarations, then `analyzeModuleStatements` on main-file-only statements via `collectNonInclude(mod.stmts)`; prints errors prefixed with source path and calls `exit(1)` if any errors were found.
- **`tests/fixtures/suru-semantic-reject/main.suru`** (new): Minimal invalid fixture (`printLn(undeclared)`) used by the rejection integration test.
- **`tests/Suru.Tests/IRSuruStage15eTests.cs`** (new): `ValidProgramStillCompiles` — driver on `while-loop/main.suru` asserts output `"1\n2\n3\n4\n5\n55\nxxx\n"`; `InvalidProgramExitsWithCode1` — driver on `suru-semantic-reject/main.suru` asserts exit code 1 and no `.ll` file written. 174/174 tests green.

### Stage 15c + 15d — Variant Creation, Field Access & Match Dispatch (Suru-in-Suru)

Implements variant creation, field access, and match dispatch in the Suru-in-Suru heap codegen, cross-validated against `tests/fixtures/sum-types/main.suru`. Also fixes a latent `ret void`/`unreachable` bug in `emitHFnDecl` for non-void-returning Suru functions.

- **`tests/fixtures/suru-codegen/heap-codegen-context.suru`** (modified): Added `findParentForVariant(ctx, variantName) SumTypeDecl` (walks `ctx.sumTypes` to find the sum type containing a given variant; returns empty decl if not found). Added `"void" → "void"` mapping to `heapLlvmTypeOf` so void Suru functions are correctly declared as LLVM `void` functions.
- **`tests/fixtures/suru-codegen/heap-stmt-codegen.suru`** (modified): `emitHLetStmt` now checks `isVariant` and — when the annotation names a variant — overwrites the flat struct header via GEP to set `type_tag=7` and `variant_idx` in-place (mirrors C# `IRFunctionCodeGenerator.EmitStmt`). Extracted `emitHMatchArmBodies` (shared tail: labeled arm bodies → wildcard → merge label) from `emitHMatchStmt` to enable reuse. Added `emitHMatchStmtVariant` — calls `@suru_variant_tag` once, then emits `icmp eq i64 tag, variantIdx` per arm using `findParentForVariant` + `variantIdx`. `emitHMatchStmt` now dispatches to `emitHMatchStmtVariant` when `isVariant(ctx, condR.suruType)` is true. Fixed `emitHFnDecl` end-of-function fallback: void-returning functions still get `ret void`; non-void functions with an open block (dead code from exhaustive match) now get `unreachable` instead of the incorrect `ret void`.
- **`tests/fixtures/suru-codegen/heap-value-codegen.suru`** (modified): `emitFieldAccess` now calls `@suru_variant_inner` (identity) on variant receivers before the field GEP, for parity with C# `IRStructCodeGenerator.EmitFieldAccess`.
- **`tests/Suru.Tests/IRSuruStage15cTests.cs`** (new): `VariantCreationAndFieldAccessAndMatchDispatch` — compiles `sum-types/main.suru` via the heap driver, links with five Suru runtime modules, asserts output `"2283\ncircle\nsquare\n"`. 172/172 tests green.

### Stage 15b — Sum Type Context & Registration

Adds `SumTypeDecl` to `HeapCodegenContext` and wires up sum type registration in the heap codegen pipeline, making variant indices available for Stages 15c/15d.

- **`tests/fixtures/suru-codegen/heap-codegen-context.suru`** (modified): Added `type SumTypeDecl: { name String, variants Array<String> }`. Added `sumTypes Array<SumTypeDecl>` field to `HeapCodegenContext`; updated `makeHeapContext` and all 8 `withH*` updater functions to thread the new field. Added sum type registry section: `addHSumType`, `lookupSumType` (linear scan; empty decl on miss), `variantIdx` (returns 0-based index or -1), `isVariant` (true when name appears as variant in any registered sum type).
- **`tests/fixtures/suru-codegen/heap-stmt-codegen.suru`** (modified): Added `registerHSumTypes` (scans stmts for `SumTypeDeclNode`, builds `SumTypeDecl`, calls `addHSumType`); called after `registerHTypes` in `emitHModule`; updated `emitHFnDecl` inner-context literal to propagate `sumTypes`.
- **`tests/fixtures/sum-type-context-test/main.suru`** (new): Standalone unit test driver — manually registers `Shape: Circle, Square`, then asserts all helpers with 10 PASS/FAIL cases.
- **`tests/Suru.Tests/IRSuruStage15bTests.cs`** (new): `SumTypeContextHelpersWork` — compiles and runs the fixture, asserts all 10 PASS lines and no FAIL lines. 171/171 tests green.

### Stage 15a — Multi-Level Include Resolution

Extends `suru-codegen-driver-full/main.suru` to handle transitive and diamond includes, replacing the single-level `resolveIncludes`/`collectDecls` pair with a recursive depth-first resolver backed by a path-deduplication list.

- **`tests/fixtures/suru-codegen-driver-full/main.suru`** (modified): Removed `collectDecls`. Added `ResolveState { decls Array<AstNode>, resolved Array<String> }` named type, `pathResolved` (linear scan helper), and `resolveIncludesRec` (depth-first recursive resolver — for each `IncludeNode` skips already-resolved paths, otherwise marks as resolved, recurses into the included file's own stmts, then adds `FnDeclNode`/`TypeDeclNode`/`SumTypeDeclNode` to `state.decls`). Rewrote `resolveIncludes` to drive the recursive pass then append the main file's own non-include nodes. Signature unchanged — `main()` unmodified.
- **`tests/fixtures/include-transitive/`** (new): Three-file fixture exercising transitivity (`main → lib-a → lib-b`) and diamond deduplication (`main → lib-b` direct + `main → lib-a → lib-b`). `triple(3)=9`; `quadruple(7)=28`; expected output `"28\n9\n"`.
- **`tests/Suru.Tests/IRSuruStage15aTests.cs`** (new): `IRSuruStage15aTests.TransitiveAndDiamondIncludeCrossValidation` — compiles driver, runs on `include-transitive/main.suru`, links with five Suru runtime modules, asserts `"28\n9\n"`. 170/170 tests green.

### Stage 14e — Full Pipeline & Cross-Validation

Extends the Suru-in-Suru code generator to cross-validate against six additional corpus programs and adds include-directive resolution to a new full-pipeline driver.

- **`tests/fixtures/suru-codegen/heap-type-helpers.suru`** (new): Per-type clone/drop helpers extracted from `heap-stmt-codegen.suru` to keep all files ≤ 500 lines. Contains `cloneHeapFieldText`, `dropHeapFieldText`, `emitTypeCloneDrop`, `emitAllTypeCloneDrop`.
- **`tests/fixtures/suru-codegen/heap-stmt-codegen.suru`** (modified, 438 lines): Includes `heap-type-helpers.suru as htypes`; delegates `emitAllTypeCloneDrop` to the new file. Adds `exit()` handling in `emitHExprStmt` (emits `call void @exit(i64 n)` + `unreachable`, sets `blockOpen=false`).
- **`tests/fixtures/suru-codegen/heap-codegen-primitives.suru`** (modified): Adds `declare void @exit(i64)` to `emitAllHeapDecls`.
- **`tests/fixtures/suru-codegen/heap-value-codegen.suru`** (modified, 500 lines): Adds namespace alias call support in `emitHMethod` — when receiver VarRef is not in scope and not a builtin type, emit a direct `call @methodName(args...)` using the registered function signature. Enables `lib.double(21)` after include-resolution merges `lib.suru` declarations into the module.
- **`tests/fixtures/suru-codegen-driver-full/main.suru`** (new): Full pipeline driver with single-level include resolution. `dirName(path)` extracts directory prefix; `collectDecls` picks FnDeclNode/TypeDeclNode/SumTypeDeclNode from a file's stmts; `resolveIncludes` reads and parses each IncludeNode's file and prepends its declarations before the main file's stmts.
- **`tests/Suru.Tests/IRSuruStage14eTests.cs`** (new): Six cross-validation tests in two classes — `IRSuruStage14eSimpleTests` (heap driver: `while-loop`, `comparisons`, `negative-literals`, `print`, `exit_test`) and `IRSuruStage14eFullTests` (full driver: `include-test`). 169/169 tests green.

### Stage 14d — Heap Types in Suru-in-Suru Codegen

Extends the Suru-in-Suru code generator with String, Array, and Struct codegen. Cross-validates against the C# codegen on `strings/main.suru`, `arrays/main.suru`, and `structs/main.suru`.

- **`tests/fixtures/suru-codegen/heap-codegen-context.suru`** (new, 282 lines): Extended context for heap-type codegen. Defines `TypeFieldEntry`, `TypeDecl`, `StrIndexEntry`, `HeapVarEntry`, `HeapCodegenContext` (adds `fns`, `strGlobals`, `helpers`, `types`, `strIndex`, `strCount` to the scalar context), and `HeapCodegenResult`. Context helpers, type registry, string index, field lookup, and type classification helpers (`isScalarSuruType`, `isArraySuruType`, `elemSuruType`, `makeSuruType`, `heapLlvmTypeOf`, `suruTypeTag`).
- **`tests/fixtures/suru-codegen/heap-codegen-primitives.suru`** (new): Pure IR building helpers — no `emitHValue` calls. String literal global emitter (with null-terminated content), string method dispatch (`len` via GEP+load of `%suru.String` field 1; others via `suru_string_*` calls), array method dispatch (`len` via GEP+load of `%suru.Array` field 2; `set` with correct arg-order swap `(arr, idx, boxedVal)`; others via `suru_array_*`), struct header initialization, struct field encode/decode (`emitStructFieldToI64`/`emitFromI64`), `emitAllHeapDecls`.
- **`tests/fixtures/suru-codegen/heap-value-codegen.suru`** (new, 481 lines): Group A — `emitHValue` and all its mutual recursion partners. String literal emit (malloc+memcpy+`suru_string_create`), array literal emit (header init + per-element `emitToI64`), struct literal emit (malloc + header + per-field store), field access (GEP + load + `emitStructFieldFromI64`), string/array/struct method dispatch, `emitHCallExpr` (handles `clone`/`drop` as builtins → `suru_clone_dyn`/`suru_drop_dyn`), `emitHMatchExpr`, `collectHStmtPatIdxs`.
- **`tests/fixtures/suru-codegen/heap-stmt-codegen.suru`** (new, 497 lines): Group B — `emitHStmt*` + module assembly. `emitHPrintLn` (boxes scalars by `llvmType` check); `emitHLetStmt` (sets `currentReturnName` via `makeSuruType` for array/struct literal dispatch); `emitHFnDecl` (fresh inner context with `makeSuruType(node.returnType)` as `currentReturnName`); `emitTypeCloneDrop` (memcpy-based clone + per-heap-field re-clone/drop); `emitHModule` (scan string literals → register globals → register types → register fns → emit decls → emit clone/drop helpers → emit fn bodies → emit `@main` wrapper; output in preamble|strGlobals|helpers|fns order).
- **`tests/fixtures/suru-codegen-driver-heap/main.suru`** (new): Stage 14d driver fixture — lexes, parses, calls `emitHModule`, writes assembled `.ll` output.
- **`tests/Suru.Tests/IRSuruStage14dTests.cs`** (new): Three cross-validation tests: `StringsCrossValidation`, `ArraysCrossValidation`, `StructsCrossValidation` — each runs the heap driver on the corresponding fixture, links with Suru runtime modules, and asserts output matches the C# compiler's binary.
- **Key bugs fixed during implementation:** `invert()` on Bool is identity (`sub i1 0, x = x`) — use explicit `true`/`_` arm ordering instead; `suru_string_len` and `suru_array_len` don't exist in the runtime — use GEP+load; `suru_array_set(arr, idx, val)` argument order is swapped from Suru call order `arr.set(val, idx)`.
- 163/163 tests green.

### Stage 14c — Control Flow & Match Codegen (Suru-in-Suru)

Extends the Suru-in-Suru codegen with while loops, match-as-expression, match-as-statement, function parameter alloca, assignment statements, recursive calls, and the `compare` method. Cross-validates against the C# codegen on `fibonacci.suru` (match expression + recursion, pure integers).

- **`tests/fixtures/suru-codegen/control-flow-codegen.suru`** (new, 499 lines): Standalone codegen file — includes only `ir-builder.suru`, `codegen-types.suru`, and `suru-parser-ast.suru`. Contains the full mutually-recursive codegen dispatch layer. Key additions: `emitCallExpr` (user-defined function calls, enables recursion); `emitAssignStmt` (variable reassignment); `emitWhile` (three-block pattern: `wh_N_c / wh_N_b / wh_N_a`); `peekType` (LLVM type inference for match result alloca); `collectPatIdxs` / `collectStmtPatIdxs` (pre-collect non-wildcard arm indices); `emitMatchExpr` (result alloca emitted BEFORE the comparison chain — critical dominance invariant); `emitMatchStmt` (block arm bodies with `blockOpen` tracking to prevent double-terminators); `compare` method (sgt + slt + zext×2 + sub → i64 -1/0/1). `emitFnDecl` allocas each parameter from its LLVM SSA name (`%argv` for `suru_main`, `%name` for all others).
- **`tests/fixtures/suru-codegen/codegen-types.suru`**: Added `blockOpen Bool` to `CodegenContext`; added `withBlockOpen` helper; updated `makeContext`, `withBuilder`, `withReturnType` to thread the field through.
- **`tests/fixtures/suru-codegen/scalar-codegen.suru`**: Preserved as Stage 14b artifact; no longer used by the driver.
- **`tests/fixtures/suru-codegen-driver/main.suru`**: Changed include from `scalar-codegen.suru` to `control-flow-codegen.suru`.
- **`tests/Suru.Tests/IRSuruCodegenTests.cs`**: Added `IRSuruStage14cTests.FibonacciCrossValidation` — runs the driver on `fibonacci.suru`, links with Suru runtime modules, asserts output `"0\n1\n5\n55\n"`.
- 160/160 tests green.

### Stage 14b — Scalar Values & Arithmetic (Suru-in-Suru Codegen)

First real codegen layer: processes AST nodes from the Suru-in-Suru parser and emits LLVM IR for scalar programs. Cross-validates against the C# codegen on `arithmetic.suru`.

- **`tests/fixtures/suru-codegen/codegen-types.suru`** (new): `CodegenContext` (wraps `IrBuilder` with `vars Array<VarEntry>`, `fns Array<FnSig>`, `currentReturnType String`), `VarEntry`, `FnSig`, `CodegenResult` (adds `llvmType String` so callers know the scalar type for boxing). Context helpers: `makeContext`, `withBuilder`, `withReturnType`, `lookupVar`, `addVar`, `addFn`, `lookupFn`, `llvmTypeOf` (Suru type annotation → raw LLVM type).
- **`tests/fixtures/suru-codegen/scalar-codegen.suru`** (new): `emitScalarDecls` (declare stubs for malloc/free/suru_println/suru_box_*), `emitValue` (dispatch on AstNode variant: IntLitNode/BoolLitNode/VarRefNode/MethodCallNode), `emitMethod` (arithmetic: add→`add`/take→`sub`/multiply→`mul`/split→`sdiv`/invert→`sub 0,x`; comparison: lt/gt/lte/gte/equals→icmp), `emitLetStmt` (alloca + emitValue + store + addVar), `emitPrintLn` (box scalar at printLn boundary: `suru_box_int64/bool/int32` + `suru_println`), `emitExprStmt`, `emitReturnStmt`, `emitStmt`/`emitStmts`, `buildParamsStr`, `emitFnDecl` (fn main → `@suru_main(ptr %argv) i64`; implicit `ret i64 0`), `emitMainWrapper` (builds `%suru.String` argv envelope, calls `@suru_main`, truncates to i32), `registerFunctions`, `emitModule` (module header + decls + register fns + emit fn bodies + @main wrapper).
- **`tests/fixtures/suru-codegen-driver/main.suru`** (new): Compiler driver fixture — reads a `.suru` source file, runs Suru lexer + parser, calls `codegen.emitModule`, writes the `.ll` output. Usage: `suru-codegen-driver <source.suru> <output.ll>`.
- **`tests/Suru.Tests/IRSuruCodegenTests.cs`**: Added `IRSuruStage14bTests.ArithmeticCrossValidation` — compiles the driver, runs it on `arithmetic.suru`, writes Suru runtime `.ll` modules via `SuruRuntime`, links all with clang, runs the binary, asserts output matches `"5\n6\n6\n3\n-5\ntrue\nfalse\n7\n3\n"`.
- **`TODO.md`**: Stage 14b marked complete; Stage 14c updated with deferred items (fibonacci, comparisons.suru, match expression codegen, while loops).
- 159/159 tests green.

### Stage 14a — IR Builder Primitives

Foundation for the Suru-in-Suru code generator. A pure string-building IR emitter
library with no dependencies on other Suru fixtures.

- **`tests/fixtures/suru-codegen/ir-builder.suru`** (new): `IrBuilder` struct (`output String, tmpCount Int64, labelCount Int64`) and `IrResult` struct (`builder IrBuilder, name String`). Core helpers: `makeBuilder`, `emit`, `withOutput`, `nextTmp`, `nextLabel`, `joinArgs`. Module-level emitters: `emitModuleHeader`, `emitGlobalStrConst`, `emitDeclare`, `emitFnStart`, `emitFnEnd`, `emitLabel`. Instruction emitters (all LLVM IR instructions used by the C# codegen): `emitAlloca`, `emitLoad`, `emitStore`, `emitGEP`, `emitBinop`, `emitICmp`, `emitFCmp`, `emitCall`, `emitVoidCall`, `emitRet`, `emitRetVoid`, `emitRetI32Zero`, `emitBr`, `emitCondBr`, `emitZext`, `emitSext`, `emitTrunc`, `emitPtrToInt`, `emitIntToPtr`, `emitBitcast`, `emitUnreachable`, `emitXor`.
- **`tests/fixtures/suru-codegen/main.suru`** (new): Test driver that uses the IR builder to emit a hardcoded "Hello World!" `.ll` program and writes it to `args.at(1)`. Demonstrates the full builder API: module header, global string constant, extern declaration, function definition with GEP and variadic call, and `@main` wrapper.
- **`tests/Suru.Tests/IRSuruCodegenTests.cs`** (new): Two integration tests. `HelloWorldEmitsValidLl` compiles the fixture, runs it to produce `.ll`, compiles with clang, runs and asserts output `"Hello World!\n"`. `EmittedIrContainsExpectedStructure` checks structural landmarks in the emitted text without requiring clang.
- **`TODO.md`**: Stage 14 split into sub-stages 14a–14e; Stage 14a marked complete.
- 158/158 tests green.

---

### Stage 13k — Flat Struct Layout, Vtable Dispatch & Type Narrowing

Replaces the linked-list struct representation with a flat fixed-layout allocation, adds per-type vtable-based clone/drop, unifies the variant representation with the struct layout, and adds type narrowing inside variant match arms.

**Struct representation — Phase 2 (flat layout)**

- **`src/Suru.Compiler/Codegen/IRStructCodeGenerator.cs`**: Rewrites `EmitStructLiteral` for a flat fixed-layout allocation. New header layout (32 bytes): `{ i64 type_tag, i64 variant_idx, ptr clone_fn, ptr drop_fn }`; field slots follow at byte offset `32 + i*8` in `TypeDeclaration` order, stored as raw `i64` (scalars widened, heap values `ptrtoint`). The old linked-list of `%suru.Field` nodes and the runtime name-lookup via `@suru_find_field` / `@strcmp` are gone. `StructSize` / `FieldOffset` / `FieldIndex` helpers compute byte geometry. `StructFieldToI64` / `StructFieldFromI64` encode/decode scalar and heap field slots without boxing. `EmitStructLiteral` now requires a declared type name (anonymous struct literals were already disallowed by the grammar). Missing fields in a literal are zero-initialized so `suru_drop_dyn` never dereferences a garbage slot. Fields absent from a literal (partial struct, empty `{}`) are stored as `i64 0`; `suru_clone_dyn`/`suru_drop_dyn` null-guard these before dispatch. `_fieldNames` dict and `@.field_N` globals removed.

- **`src/Suru.Compiler/Codegen/IRTypeCloneDropCodeGenerator.cs`** (new): Emits per-type `@suru_clone_{T}(ptr) ptr` and `@suru_drop_{T}(ptr) void` functions into `_helpers` (before user function bodies) for every locally-declared `TypeDeclaration`. Clone: copies the 32-byte header word-by-word; scalar field slots copied as `i64`; heap field slots loaded, `inttoptr`'d, deep-cloned via `@suru_clone_dyn`, `ptrtoint`'d back, and stored. Drop: heap field slots recursively dropped via `@suru_drop_dyn`; then `@free`s the allocation. External types (merged via include resolution) are skipped here — their clone/drop live in the origin module's `.o`; `declare` stubs are emitted instead.

- **`src/Suru.Compiler/Codegen/IRCodeGenerator.cs`**: Pre-pass calls `EmitTypeCloneDrop()` after registering user functions. `_externalTypeCloneDrop` `HashSet<string>` tracks types needing `declare` stubs for cross-module clone/drop. `%suru.Field` struct type definition and field-name globals removed from the emitted preamble. `StructLiteralExpression` case removed from `EmitValue` (must go through `EmitStructLiteral` with a type name). `EmitArraySet` and `EmitArrayAdd` now receive the element `SuruType` so they can apply the correct `StructFieldToI64` encoding. `_externals.AddFree()` called in the pre-pass so `EmitTypeCloneDrop` can emit `@free` calls unconditionally.

- **`src/Suru.Compiler/Codegen/IRFunctionCodeGenerator.cs`**: Variant `let` tagging inlined — instead of calling `@suru_variant_create`, the codegen now GEPs directly into the flat struct and stores `type_tag=7` and `variant_idx` in-place (no separate wrapper allocation). Assignment `StructLiteralExpression` now routes through `EmitStructLiteral` using the declared variable type, mirroring the `let` case.

- **`src/Suru.Compiler/Codegen/SuruStructRuntime.cs`**: Rewritten. `suru_struct.ll` now provides `@suru_clone_dyn` and `@suru_drop_dyn` — two universal dispatch functions. `suru_clone_dyn` reads `type_tag` at offset 0; for tag 0–3 (Box) calls `@suru_box_clone`; for tag 5 (Array) calls `@suru_array_clone_dyn`; for tag 6 (String) calls `@suru_string_clone`; for tag 4 (Struct) or 7 (Variant) loads `clone_fn` from vtable offset 16 and calls it. Null guard: if `val == null` return `null`. `suru_drop_dyn` mirrors this for drop, using vtable offset 24 for tag 4/7. `%suru.Field` struct type, `@suru_find_field`, `@suru_struct_clone`, `@suru_struct_drop` removed. `@strcmp` no longer needed.

- **`src/Suru.Compiler/Codegen/SuruVariantRuntime.cs`**: Simplified. A variant is now the flat struct itself with `type_tag=7` — no separate 24-byte `%suru.Variant` wrapper. `@suru_variant_create(i64 idx, ptr struct_ptr)` writes `type_tag=7` and `variant_idx` in-place and returns the same ptr (no `@malloc`). `@suru_variant_inner(ptr v)` is the identity (returns `v` unchanged). `@suru_variant_drop(ptr v)` reads `drop_fn` from vtable offset 24 and calls it. `%suru.Variant` struct type removed.

- **`src/Suru.Compiler/Codegen/SuruArrayRuntime.cs`**: `suru_array_clone_dyn` and `suru_array_drop_dyn` now delegate to `@suru_clone_dyn` / `@suru_drop_dyn` (single call per element) instead of an inline `switch` on `type_tag`. Cross-module `declare` stubs updated: `suru_box_clone`, `suru_string_clone/drop`, `suru_struct_clone/drop` removed; `suru_clone_dyn`, `suru_drop_dyn` added.

- **`src/Suru.Compiler/Codegen/SuruRuntime.cs`**: File-header comments updated to describe Phase 2 struct layout, vtable dispatch, and revised cross-module dependency graph. `@suru_dyn_len` gains a null guard (`icmp eq ptr %v, null → label %unknown`) so it doesn't crash on a null value.

- **`src/Suru.Compiler/Codegen/SuruRuntimeDeclarations.cs`**: `AddFindField` method and related `@suru_find_field` declare-stub removed. `AddCloneDyn` / `AddDropDyn` methods added.

- **`tests/Suru.Tests/IRStructFieldNameGlobalsTests.cs`**: Updated to assert the flat-layout IR instead of linked-list field-node IR.

**Language improvements**

- **`src/Suru.Compiler/Lex/TokenKind.cs`** / **`Lexer.cs`**: `as` promoted from identifier to `TokenKind.As`. `ParseIncludeDirective` now uses `Consume(TokenKind.As)` instead of checking that the identifier text equals `"as"`.

- **`src/Suru.Compiler/Semantic/SemanticAnalyzer.cs`**: Three improvements:
  1. **Type narrowing in match arms** — within a variant arm of a sum-type match, the condition variable is shadowed in the arm's scope with the narrowed concrete `NamedType`. This means `x.field` resolves correctly inside the arm without an explicit re-binding.
  2. **Variant return covariance** — returning a value whose type is a variant of the function's declared sum-type return type is now valid (no spurious type-mismatch error).
  3. **`InferType` extension** — field access on any expression returning a named type (e.g. a function call result) now resolves the field type via `_typeDeclarations`, not just variable references.

- **`src/Suru.Compiler/Codegen/IRMatchCodeGenerator.cs`**: **Codegen type narrowing** — `NarrowCondVar` / `RestoreCondVar` helpers temporarily update `_vars[condVar]` to the narrowed `NamedType` for the duration of each variant arm's emit, so `EmitFieldAccess` resolves fields against the concrete struct type. Handles expression-form match (`EmitMatchAsExpression`), statement-form match (`EmitMatchStatement`), and `EmitMatchAsStatement`. Empty struct literal `{ }` in a match arm body (`_: {}`) now treated as a no-op instead of raising a codegen error.

**Fixture & test updates**

- **`tests/fixtures/suru-lexer/suru-lexer.suru`** / **`main.suru`**: Updated for flat struct emit; `main.suru` split — driver logic extracted, file reduced significantly.
- **`tests/fixtures/suru-parser/suru-parser.suru`**: Major expansion; `suru-parser-ast.suru` (new) extracted to keep both files under 500 lines.
- **`tests/fixtures/suru-semantic/`**: All semantic fixture files updated to exercise type narrowing and variant return covariance.
- **`tests/Suru.Tests/IRSuruLexerTests.cs`** / **`SuruLexerTests.cs`**: Assertions updated for `as` keyword token and flat struct IR changes.

All 156 tests pass.

---

### Stage 13j — Sum Type Match Patterns & Exhaustiveness

Completes the sum type feature: `match` dispatch on variant-typed values and compile-time exhaustiveness checking.

- **`src/Suru.Compiler/Semantic/SemanticAnalyzer.cs`**: `AnalyzeMatchStatement` now recognises sum-type / variant conditions (`SuruType.SumType` or `NamedType(V)` where `IsVariant(V)`). For these matches it validates each non-wildcard pattern identifier against the parent sum type's variant list and reports `"non-exhaustive match: missing variant '<name>'"` for any uncovered variant when no wildcard arm is present. Pattern expressions are not passed to `AnalyzeExpression` (variant names are not variables). Same logic applied to expression-context `MatchExpression`. Added `IsSumTypeVariant` and `FindParentForVariant` helpers. `CheckHasReturn` extended: a `MatchStatement` where every arm body contains a return/exit satisfies the non-void return requirement regardless of wildcard — semantics analyzer already ensures exhaustiveness for sum-type matches.

- **`src/Suru.Compiler/Codegen/IRMatchCodeGenerator.cs`**: `EmitPatternComparisons` now detects variant/sum-type conditions at the top of the method. When detected, calls `@suru_variant_tag(ptr cond)` once to extract the `i64` variant index, then for each arm compares that index against the arm's static variant index via `icmp eq i64`. Pattern variant names are resolved to indices via the existing `GetVariantIndex` helper — `EmitValue` is never called on variant-name patterns (they are not variables). Applies to both statement-context (`EmitMatchStatementTestChain`) and expression-context (`EmitMatchTestChain`) match.

- **`tests/fixtures/sum-types/main.suru`**: Added `getCircleInfo(c Circle) String` and `getSquareInfo(s Square) String` functions, each with an exhaustive `match` on both `Circle` and `Square` arms. `main` calls both and prints the results. Expected output: `2283\ncircle\nsquare`.

- **`tests/Suru.Tests/IRSumTypeTests.cs`**: Four new semantic tests — exhaustive match accepted, non-exhaustive match reports error for missing variant, wildcard covers all variants, unknown variant in pattern reports error. Integration test updated to assert `"2283\ncircle\nsquare\n"`.

All 154 tests pass.

---

### Stage 13i — Variant Creation & Field Access Codegen

Wires codegen so user code can create variant values and transparently access their fields. Given `type Circle: { radius Int64 }` and `type Shape: Circle, Square`, writing `let c Circle: { radius: 2283 }` now wraps the struct in `@suru_variant_create`, and `c.radius` unwraps via `@suru_variant_inner` before `@suru_find_field`.

- **`src/Suru.Compiler/Codegen/IRCodeGenerator.cs`**: Added three helpers — `IsVariant(string)` (checks if a type name is a variant in any declared sum type), `FindParentSumType(string)` (returns the owning `SumTypeDeclaration`), `GetVariantIndex(string, SumTypeDeclaration)` (0-based index within the variant list).

- **`src/Suru.Compiler/Codegen/IRFunctionCodeGenerator.cs`**: `EmitStmt` struct-literal `let` case now checks `IsVariant(slAnn.Name)`; when true, calls `@suru_variant_create(i64 idx, ptr structPtr)` and stores the wrapper ptr. The variable's `SuruType` stays `NamedType("Circle")` so downstream `EmitFieldAccess` can detect the variant via `IsVariant`.

- **`src/Suru.Compiler/Codegen/IRStructCodeGenerator.cs`**: `EmitFieldAccess` now checks if the receiver type is a variant; when true, calls `@suru_variant_inner(ptr)` to extract the inner struct pointer before calling `@suru_find_field`. No change to the rest of the field-load path — scalar unboxing and ptr returns work unchanged.

- **`tests/fixtures/sum-types/main.suru`** (new): Integration fixture — declares `Circle`, `Square`, and `Shape`; creates a `Circle` value; accesses `.radius`; prints the result.

- **`tests/Suru.Tests/IRSumTypeTests.cs`**: Added `IRSumTypeIntegrationTests` class (Stage 13i) with one integration test asserting `c.radius.toString()` prints `2283`.

No semantic changes needed: `Circle` is already a `TypeDeclaration`, so `InferType` for `var.field` resolves field types correctly without modification. All 150 tests pass.

---

### Stage 13h — Sum Type Runtime Module

Adds the `suru_variant.ll` LLVM IR runtime module, compiled to `suru_variant.o` and linked unconditionally into every Suru binary. No user-visible language changes — this is the infrastructure that Stages 13i and 13j will build on for variant creation, field access, and match dispatch.

- **`src/Suru.Compiler/Codegen/SuruVariantRuntime.cs`** (new): `SuruRuntime.GenerateVariantRuntime()` returns the full LLVM IR text for `suru_variant.ll`. Defines `%suru.Variant = { i64 type_tag=7, i64 variant_idx, ptr inner }` (24 bytes) and four functions: `@suru_variant_create(i64, ptr) ptr`, `@suru_variant_tag(ptr) i64`, `@suru_variant_inner(ptr) ptr`, `@suru_variant_drop(ptr) void`. `suru_variant_drop` frees only the 24-byte wrapper; inner struct drop is the caller's responsibility.

- **`src/Suru.Compiler/Codegen/SuruRuntimeDeclarations.cs`**: Added four idempotent `AddVariant*` methods (`AddVariantCreate`, `AddVariantTag`, `AddVariantInner`, `AddVariantDrop`) following the existing bool-flag declare-stub pattern.

- **`src/Suru.Compiler/Compiler.cs`**: `CompileIR()` now generates and compiles five runtime modules (was four); `suru_variant` added to the tuple array. Updated comments to reflect five modules.

- **`src/Suru.Compiler/Codegen/SuruRuntime.cs`**: Updated file-header comment to list tag=7 in the type_tag enum, add `suru_variant.ll` module description, and reference `SuruVariantRuntime.cs` in the partial class split list.

All 149 existing tests pass unchanged.

---

### Stage 13g — Sum Type Declarations

Adds the `type Shape: Circle, Square` sum type declaration syntax end-to-end through the AST, parser, type system, semantic analyzer, and include resolver. No codegen yet — this stage makes sum type declarations a first-class compile-time construct for Stage 13h–13j to build on.

- **`src/Suru.Compiler/Parse/Ast/SumTypeDeclaration.cs`** (new): `SumTypeDeclaration(string Name, IReadOnlyList<string> Variants)` AST node, a `Statement` subclass. Each variant name must refer to a declared struct type.

- **`src/Suru.Compiler/Types/SuruType.cs`**: Added `SumType` sealed class (`Tag = 7`, carries `Name` and `Variants`). Fixed `VoidType.GetHashCode()` from `7` → `8` to avoid collision. Updated file-header comment.

- **`src/Suru.Compiler/Types/SuruTypeSystem.cs`**: `TryResolve` accepts an optional `sumTypeDecls` parameter; returns `SuruType.SumType` for declared sum type names.

- **`src/Suru.Compiler/Parse/Parser.cs`**: `ParseTypeDeclaration()` now dispatches on the token after `:` — `{` → struct (existing path), identifier → sum type (new `ParseSumTypeBody` helper). `_Parse()` builds `SumTypeDeclarations` alongside `TypeDeclarations`.

- **`src/Suru.Compiler/Parse/Ast/Module.cs`**: Added `SumTypeDeclarations: IReadOnlyDictionary<string, SumTypeDeclaration>`.

- **`src/Suru.Compiler/Semantic/SemanticAnalyzer.cs`**: Sum type registration pass between struct and function passes. `RegisterSumTypeDeclaration` validates: no duplicates, non-empty variant list, all variant names are declared struct types. `ResolveTypeAnnotation` passes `_sumTypeDeclarations` to `TryResolve`. `AnalyzeStatement` has a no-op case for `SumTypeDeclaration`.

- **`src/Suru.Compiler/IncludeResolver.cs`**: `MergeModule` handles `SumTypeDeclaration` nodes (deduplication + registry registration). `BuildSumTypeDict` helper. Merged module includes `SumTypeDeclarations`. Sum types prepended before struct types in merged statement list.

- **`src/Suru.Compiler/Parse/Ast/ExternalDeclarationRegistry.cs`**: `Register()` now accepts `SumTypeDeclaration`.

- **`src/Suru.Compiler/Parse/AstPrinter.cs`**: Prints `SumType [Name] Variants: [A, B]`.

- **`tests/Suru.Tests/IRSumTypeTests.cs`** (new, 13 tests): parser tests (2-variant, single-variant, module index, struct unaffected, both indexed), AstPrinter test, semantic tests (valid, duplicate name, unknown variant, all unknown, sum type as param type, type system resolution, TypeTag value).

- 149/149 tests green.

---

### Stage 13f — Semantic Cross-Validation CLI

Completes the Suru-implemented semantic analyzer by wiring it into a CLI tool (`suru-check`) and cross-validating its output against the C# `SemanticAnalyzer` on a shared corpus of valid and invalid programs.

- **`tests/fixtures/suru-check/main.suru`** (new): reads a `.suru` source file, runs the full pipeline (Suru lexer → Suru parser → pre-passes → statement analysis including function bodies), prints each error on its own line prefixed with the source path (`<path>: <message>` format matching C# output), and exits with code 1 on any error. `analyzeModuleStatement` extends `stmts.analyzeStatement` with a `NODE_FN_DECL` arm that calls `fns.analyzeFunctionDeclaration`.

- **`tests/fixtures/suru-check/corpus/`** (new): five include-free test programs used for cross-validation:
  - `valid_hello.suru`, `valid_functions.suru` — valid programs; expect 0 errors
  - `invalid_undef_var.suru`, `invalid_dup_type.suru`, `invalid_arity.suru` — each triggers one known semantic error

- **`tests/Suru.Tests/IRSuruSemanticCrossValidationTests.cs`** (new, 8 tests): cross-validates the Suru and C# analyzers. Valid corpus tests assert exit code 0 and empty output. Invalid corpus tests assert exit code 1, a keyword match on the output, and that the error message body (path prefix stripped) matches C# output exactly. Milestone tests assert no false positives on `arithmetic`, `fibonacci`, and `control-flow` fixtures.

- **Bug fix** (`suru-semantic-fns.suru`): `isExitCall` accessed `innerExpr.name` unconditionally before checking the node kind. For `NODE_MATCH` nodes (no `name` field), `suru_find_field` would walk off the end of the linked list and segfault. Fixed by moving the `name` access inside the `NODE_CALL` arm of a match statement so it is only evaluated when the field is guaranteed to exist.

- **Enhancement** (`suru-semantic-passes.suru`): `isBuiltinType` extended with `isArrayGeneric` helper that recognises generic array type names like `"Array<Int64>"` (stored as flat strings by the Suru parser) as valid builtin types.

- 136/136 tests green.

---

### Stage 13e — Semantic Analyzer in Suru: Expression Analysis

Completes the expression-analysis layer of the Suru-written semantic analyzer. All
statement analyzers now recurse into expressions; inferType + analyzeExpr are fully
implemented in the new `suru-semantic-exprs.suru` file.

- **`suru-semantic-exprs.suru`** (new, ~300 lines): `inferType` returns a String type
  name for literals (Bool/Int64/Float64/String), variable refs (scope lookup), method
  calls (equals/lt/gt/etc. → Bool; len/compare/ord → Int64; toString → String; from →
  receiver type), call expressions (readFile → String; user functions via state.functions),
  and match expressions (first arm type); returns `""` for unknown/context-dependent types,
  mirroring the C# `null` convention to suppress false-positive type errors.
  `analyzeExpr` recurses into all composite expression kinds: undefined variable check
  (builtin type names exempt); arity check for 1-arg builtins (printLn/printError/exit/
  readFile/clone/drop), writeFile (2 args), and user functions via state.functions;
  namespace alias heuristic (METHOD_CALL receiver not in scope + not static builtin →
  silently skip, avoids false "undefined variable" errors for calls like
  `semantic.makeAnalyzerState()`); match condition type check; NODE_FIELD_ACCESS,
  NODE_ARRAY_LIT, NODE_STRUCT_LIT, NODE_UNARY, NODE_BINARY sub-expression recursion.

- **`suru-semantic-stmts.suru`** (updated): wired `exprs.analyzeExpr` into every statement
  analyzer; `analyzeReturnStatement` now performs return-type mismatch checking when both
  the inferred return type and the declared type are known; `analyzeWhileStatement` now
  checks the condition is Bool; `analyzeStatement` gained a `NODE_EXPR_STMT` case.

- **`suru-semantic-fns.suru`** (updated): `checkHasReturnAt` extended with a
  `NODE_EXPR_STMT` case that calls `isExitCall` to detect `exit()` terminal statements;
  `isExitCall` extracted as a separate helper to keep match expression arm bodies
  single-expression.

- **`suru-semantic-tests.suru`** (new, ~450 lines): all Stage 13a–13d test functions
  extracted from `main.suru` to keep `main.suru` under 500 lines; test AstNodes updated
  to include `value: { kind: NODE_INT_LIT }` and `condition: { kind: NODE_BOOL_LIT }`
  fields required after expression analysis was wired into statement analyzers.

- **`main.suru`** (rewritten): thin driver including all semantic files; 14 Stage 13e unit
  tests for `inferType` (each literal kind, var ref, method call equals/len, unknown →
  empty) and `analyzeExpr` (undefined var, defined var no error, builtin type receiver no
  error, arity mismatch, correct arity, builtin arity error).

- **Codegen fix** (`IRFunctionCodeGenerator.cs`): duplicate `let` variable names in sibling
  match arm scopes generated duplicate LLVM alloca names (`%name.addr`). Fixed by using
  `_tmp++` counter for all let-binding alloca names — each alloca now has a unique name
  independent of the variable's Suru name.

- **Codegen fix** (`suru-semantic-fns.suru`): chaining `.equals()` directly on an
  undeclared field access (e.g. `innerExpr.name.equals("exit")`) caused the codegen to
  emit `icmp eq i64` instead of `suru_string_equals` because the field's type was
  `NamedType("")`. Fixed by using a typed intermediate `let innerName String: innerExpr.name`
  to force correct String dispatch.

- 128/128 tests green.

---

### Match statement block bodies

`match` at statement level now supports `{ stmt* }` block arm bodies, enabling early returns, `let` bindings, and multi-statement logic inside arms.

- New AST node `MatchStatement` / `MatchStatementArm` (`Parse/Ast/MatchStatement.cs`) — arm body is `IReadOnlyList<Statement>` (empty list for `{}` or bare colon).
- Parser: `match` at statement level dispatches to `ParseMatchStatement()`; `match` in expression position (let RHS, return, etc.) continues to parse as `MatchExpression` (unchanged).
- Arm body dispatch: `{` → block, `}` immediately after `:` → empty, otherwise → single expression (backward-compat).
- Semantic analyzer: `AnalyzeMatchStatement` — same condition-type guard as expression form; each arm body analyzed in a fresh scope. `CheckHasReturn` extended: `MatchStatement` satisfies return requirement when wildcard arm is present and every arm body contains a return/exit.
- Codegen: `EmitPatternComparisons` extracted as a shared helper; `EmitMatchStatementTestChain` + `EmitMatchStatement` added to `IRMatchCodeGenerator.cs`. Branch terminators guarded by `_blockOpen` to avoid double-terminator IR when an arm body ends with `return`/`exit`.
- AstPrinter: prints `MatchStatement` with per-arm block contents.
- New fixture `tests/fixtures/match-statement/` + `IRMatchStatementTests` (128/128 green).

### Stage 13d — Semantic Analyzer in Suru: Function Declaration Analysis

New file `tests/fixtures/suru-semantic/suru-semantic-fns.suru` implements function body
analysis for the Suru semantic analyzer written in Suru.

- `checkHasReturnInWhile` / `checkHasReturnAt` / `checkHasReturn` — mutually recursive
  scan that finds `NODE_RETURN` in a statement list, recursing into `NODE_WHILE` bodies.
  Mirrors `CheckHasReturn` in SemanticAnalyzer.cs:228–237.
- `analyzeFunctionDeclaration` — pushes a fresh scope, injects parameters as symbols,
  sets `state.currentReturnType` and `state.insideFunction`, analyzes the body via
  `analysis.analyzeStatements`, checks non-void functions have at least one return path,
  then pops the scope and restores context. Mirrors `AnalyzeFunctionDeclaration` in
  SemanticAnalyzer.cs:201–226. Module-scope constants remain visible through parent-chain
  traversal without re-injection.
- 7 unit tests added to `main.suru` and `IRSuruSemanticTests.cs`: void no-return,
  non-void with return, missing return error, bare-return-in-non-void error, params
  visible in scope, scope not leaked after analysis, return-in-while counts.
- 127/127 tests green.

### Module system — Chunk 6: remove `Namespaces`/`ExternalFunctions`; eliminate `ns.fn` rename

`Module.Namespaces` and `Module.ExternalFunctions` are removed; all callers now use
`Module.Aliases` (`AliasMap`) and `Module.ExternalDeclarationRegistry` exclusively.
The `ns.fn` qualified-name rename in `MergeModule` is also removed — merged functions
carry their original unqualified names with `FunctionDeclaration.SourcePath` set to
their origin file.

- `Module`: removed `Namespaces` (`IReadOnlySet<string>`) and `ExternalFunctions`
  (`IReadOnlyDictionary<string,string>`).
- `IncludeResolver.MergeModule`: removed `namespaces`/`externalFns` parameters;
  replaced `seenFns` deduplication keyed by `(path, name)`; direct functions merged
  with original name + `SourcePath`; transitive functions forwarded as-is.
- `IRMatchCodeGenerator.PeekMethodType`: replaced `_module.Namespaces.Contains` +
  `_userFunctions` lookup with `_module.Aliases.Resolve` +
  `_module.ExternalDeclarationRegistry.LookupFunction` + `FnReturnSuruType`.
- `IncludeResolverTests`: updated assertions to use `Aliases.All`, `Aliases.Contains`,
  and `ExternalDeclarationRegistry.LookupFunction`; renamed
  `TransitiveFunction_PropagatedWithOriginalQualifiedName` →
  `TransitiveFunction_PropagatedInRegistry`.
- All 127 tests pass; zero references to `Namespaces`/`ExternalFunctions` remain in source.

### Module system — Chunk 5: diamond-include registry propagation

Diamond includes now propagate their cached module's `Aliases` and
`ExternalDeclarationRegistry` into the current context, making the registry
complete regardless of include ordering.

- `IncludeGraph`: added `CacheModule` / `GetCachedModule` to store each resolved
  `Module` by absolute path after `LoadModule` returns.
- `IncludeResolver.Resolve`: calls `graph.CacheModule(fullPath, included)` after
  each non-diamond resolution.
- Diamond branch: after registering the new alias, calls `aliases.MergeFrom` and
  `registry.MergeFrom` on the cached module — both use TryAdd semantics so the
  operation is idempotent.

### Module system — Chunk 4: codegen uses `Aliases` + `ExternalDeclarationRegistry`

The IR code generator now dispatches cross-module calls via canonical
`(absoluteSourcePath, unqualifiedName)` lookups instead of `ExternalFunctions` dictionary.

- `EmitFunction`: detects external functions via `fn.SourcePath != null`; strips the `"ns."` prefix to recover the unqualified LLVM symbol name.
- Pre-pass: registers only same-file functions (`SourcePath == null`) into `_userFunctions`; externals are resolved through `ExternalDeclarationRegistry` at call sites.
- `EmitMethodCall`: uses `_module.Aliases.Contains(ns)` instead of `_module.Namespaces.Contains(ns)`.
- `EmitUserFunctionCall`: detects cross-module calls by a dot in the name, resolves via `Aliases.Resolve(ns)` → `ExternalDeclarationRegistry.LookupFunction(path, fn)`, and uses the unqualified name as the LLVM symbol.

### Module system — Chunk 3: semantic analyzer uses `Aliases` + `ExternalDeclarationRegistry`

Renamed `DeclarationRegistry` → `ExternalDeclarationRegistry` throughout for clarity.
The semantic analyzer now dispatches cross-module calls via canonical
`(absoluteSourcePath, unqualifiedName)` lookups instead of alias-string scope lookups.

- `DeclarationRegistry` renamed to `ExternalDeclarationRegistry` (class, file, `Module`
  property, all `IncludeResolver` references).
- `RegisterFunction` skips functions with `SourcePath != null` — external functions live in
  `ExternalDeclarationRegistry` and are not injected into the scope stack.
- `AnalyzeFunctionDeclaration` returns early for external functions — their bodies are
  analyzed in their own module's context, not re-analyzed in the importing module.
- `AnalyzeExpression` VarRef check: `Namespaces.Contains` → `Aliases.Contains`.
- `AnalyzeExpression` namespace MethodCall: `Namespaces` + `_scopes.LookupFunction(qualified)`
  → `Aliases.Resolve` + `ExternalDeclarationRegistry.LookupFunction(path, name)`.
- `InferType` namespace MethodCall: same substitution; return type derived from
  `ResolveTypeAnnotation(fnDecl.ReturnType)` instead of `FunctionSig.ReturnType`.

`Namespaces` and `ExternalFunctions` remain on `Module` for codegen (Chunk 4 removes them).

### Module system — Chunk 2: wire `AliasMap`/`DeclarationRegistry` into the pipeline

`AliasMap` and `DeclarationRegistry` are now populated by `IncludeResolver` and carried on
`Module` alongside the existing `Namespaces`/`ExternalFunctions` dictionaries (which are
unchanged — old code paths remain active until Chunk 6 cleanup).

- `FunctionDeclaration.SourcePath` — new `init` property; set to the absolute path of the
  source file that declared the function when a function is merged from an include; `null`
  for functions declared in the current file.
- `Module.Aliases` (`internal AliasMap`) — populated by `IncludeResolver.Resolve`:
  `aliases.Register(ns, fullPath)` for each direct include (including diamond re-entries),
  plus `aliases.MergeFrom(included.Aliases)` for transitive propagation.
- `Module.DeclarationRegistry` (`internal DeclarationRegistry`) — populated by
  `IncludeResolver.MergeModule`: `registry.Register(fullPath, decl)` for each direct
  `FunctionDeclaration`, scalar `LetStatement`, and `TypeDeclaration` merged from an
  included file; transitive entries arrive via `registry.MergeFrom(included.DeclarationRegistry)`
  called in `Resolve` before `MergeModule`.

### Module system — Chunk 1: `AliasMap` + `DeclarationRegistry` (foundation only)

Two new internal classes added to `Parse/Ast/` as the foundation for canonical name
resolution (replacing alias-string function lookup with `(absoluteSourcePath, name)` identity).
Neither class is wired into the pipeline yet — this chunk just gets the types in place.

- `AliasMap` — maps namespace aliases to absolute source paths; `Register`, `Contains`,
  `Resolve`, `MergeFrom` (TryAdd semantics for transitive propagation).
- `DeclarationRegistry` — canonical `(sourcePath, unqualifiedName) → Statement` table covering
  `FunctionDeclaration`, `TypeDeclaration`, and scalar `LetStatement` constants; `Register`,
  `Lookup`, `LookupFunction`, `Contains`, `MergeFrom`.

### Compiler audit #13 — Struct field name globals use `@.field_N` prefix

Field name strings used as struct field identifiers are now interned in a separate
`_fieldNames` dictionary (previously shared with `_stringLiterals`) and emitted as
`@.field_N` globals instead of `@.str_N`. This eliminates the theoretical collision
between user string literals and field names, and makes the generated `.ll` easier to
read. The two pools are emitted in distinct sections of the IR file. Three unit tests
in `IRStructFieldNameGlobalsTests.cs` verify the separation.

### Compiler audit #12 + #14 — Array element homogeneity validation and empty-array annotation enforcement

Two related array-literal gaps have been closed.

**#12 — Mixed-type array literals are now a compile error.** `SemanticAnalyzer.AnalyzeExpression`
for `ArrayLiteralExpression` checks that all element `ResolvedType`s match the first non-null
one after analyzing elements; the first mismatch emits
`"array literal has mixed element types (element 0 is X, element N is Y)"`.

**#14 — Empty array literals no longer silently default to `Int64`.** `IRArrayCodeGenerator`
now reads `lit.ResolvedType` (an `ArrayType` with the element type set) for count == 0, and
throws `InvalidOperationException` if it is not set. Three call sites propagate the annotation
type into the empty array's `ResolvedType` before codegen runs:
- `SemanticAnalyzer.AnalyzeLetStatement` — for `let xs Array<T>: []`
- `IRStructCodeGenerator.EmitStructLiteral` — for struct fields like `{ scopes: [] }` where
  the declared field type is `Array<T>`
- `IRMatchCodeGenerator.EmitMatchArmValue` (new helper) — for struct literal match-arm bodies
  like `true: { parser: parser, args: [] }` inside a function returning a named struct type;
  threads `_currentFnReturnTypeName` into the struct so nested empty array fields resolve

Three unit tests added to `SemanticAnalyzerTests`: mixed-type array (error), homogeneous
array (no error), and empty array with annotation (no error).

### Compiler audit #11 — Collapse redundant return-type state in `SemanticAnalyzer`

`SemanticAnalyzer` previously tracked the current function's return type with two fields:
`_currentFunctionReturnType` (nullable `SuruType`) and `_currentFunctionIsVoid` (bool).
Both encoded the same concept and had to be kept in sync on every entry and exit of a
function body, with a doubled reset at the end.

They have been merged into a single `SuruType? _currentReturnType` where `null` means
"void or unresolvable return type" and a non-null value means "known non-void type". The
three usage sites in `AnalyzeFunctionDeclaration` and `AnalyzeReturnStatement` now perform
a single null-check each. As a deliberate side-effect, the "non-void function has no return
statement" and "bare return in non-void function" diagnostics are suppressed when the return
type failed to resolve — the registration-pass error is sufficient, and suppressing these
reduces redundant noise on already-invalid programs.

### Compiler audit #10 — Extract `IncludeResolver` and `IncludeGraph`

`Compiler.ResolveIncludes` was a single 136-line method responsible for six distinct
concerns: path validation, circular detection, diamond deduplication, source-path collection,
namespace merging, and declaration merging. It has been replaced by two dedicated types.

`IncludeGraph` (`src/Suru.Compiler/IncludeGraph.cs`) is a type-safe class that makes the
two-HashSet guard protocol explicit. `IsActive`/`IsResolved` name the two different states
(on the call stack vs. fully processed); `Enter`/`Exit` must be called symmetrically around
each recursive call, making the circular/diamond distinction impossible to confuse.

`IncludeResolver` (`src/Suru.Compiler/IncludeResolver.cs`) is a static class with four
focused helpers: `ValidatePath` (existence check, returns absolute path), `LoadModule`
(read + parse + recurse), `CollectPaths` (appends a file and its transitives to the path
list), `MergeModule` (folds one resolved module's declarations into the accumulator).
`Compiler.ParseAndResolve` now delegates to `IncludeResolver.Resolve`.

15 new unit tests in `IncludeResolverTests.cs` cover every previously untested edge case:
no-op, missing file, circular include, single include (namespace, function qualification,
path recording), diamond (path deduplication, function deduplication), type merging, diamond
type deduplication, scalar constant merging, string constant exclusion, diamond constant
deduplication, and transitive namespace/function/path propagation. All 121 tests pass.

### Compiler audit #9 — Extract `ParseNegativeNumber()` helper

`ParsePrimary()` and `ParseMatchPattern()` both contained identical 15-line blocks for
consuming a leading `-` and returning a negated `IntLiteral` or `FloatLiteral`. The shared
logic is now in a private `ParseNegativeNumber()` method; both call sites are a single
delegation. No behaviour change — all 106 tests pass.

### Compiler audit #8 — `BuiltinNames` constants replace magic strings

Added `src/Suru.Compiler/Types/BuiltinNames.cs` with `public const string` entries for all
seven built-in Suru type names (`Void`, `Bool`, `Int32`, `Int64`, `Float64`, `String`, `Array`)
and eight built-in function names (`Main`, `PrintLn`, `PrintError`, `Exit`, `Clone`, `Drop`,
`ReadFile`, `WriteFile`). All inline Suru-level magic string literals in `SuruType.cs`,
`SuruTypeSystem.cs`, `SemanticAnalyzer.cs`, `IRCodeGenerator.cs`, `IRFunctionCodeGenerator.cs`,
`IRBoxCodeGenerator.cs`, and `IRMatchCodeGenerator.cs` now reference these constants. A future
rename or addition only requires a single edit in `BuiltinNames.cs`.

### Compiler audit #7 — Consistent error handling across the pipeline

`ParseException` is now `internal` (private flow-control detail within the parser).
`Parser.Parse()` changed from `→ Module` (throwing) to `→ CompilationResult<Module>`
(returning), wrapping the first `ParseException` as a `Fail` result. 
`Compiler.ParseAndResolve()` checks `parseResult.Success` directly, removing both
`catch (ParseException)` blocks. Parse errors in included files surface as
`"Include resolution failed: <message>"`, consistent with other include errors.
Five test call sites updated to `.Require()`.

### Compiler audit #6 — `CompilationResult.Require()` helper

Added `Require()` to both `CompilationResult` (returns `OutputPath`) and
`CompilationResult<T>` (returns `Value`). Each throws `InvalidOperationException`
with the joined error list if called on a failed result. All 6 `!` force-unwrap
sites across `Program.cs`, `Compiler.cs`, `IRSuruParserTests.cs`, and
`CompiledFixtures.cs` now call `Require()` instead.

### Compiler audit #5 — Guard `Path.GetDirectoryName()` null returns

Both `Path.GetDirectoryName()` call sites in `Compiler.cs` are now explicitly guarded.
`ParseAndResolve()` (line 208) checks for null and returns `(null, [error])` instead of
throwing `NullReferenceException`. `ResolveIncludes()` (line 300) uses `?? throw new
InvalidOperationException(...)`, which propagates to the caller's existing exception
handler and surfaces as `CompilationResult.Fail` with a descriptive message.

### Compiler audit #4 — Type tag synchronization

Added `public const int Tag = N;` to each TypeTag-bearing subclass in `SuruType.cs`
(`BoolType.Tag=0` through `StringType.Tag=6`), with `TypeTag` delegating to `Tag`.
Three codegen sites now reference these constants instead of inline literals:
- `IRArrayCodeGenerator` — `SuruType.ArrayType.Tag` (was `5`)
- `IRStructCodeGenerator` — `SuruType.NamedType.Tag` (was `4`)
- `IRFunctionCodeGenerator` — `SuruType.StringType.Tag` (was `6`)

The LLVM IR text in the four `SuruRuntime` files remains as the documented secondary
sync point; a cross-reference comment in `SuruRuntime.cs` points to `SuruType.cs`
as the C# authority. Any future type addition now requires updating `SuruType.cs`
in one place — the codegen derives the value automatically.

### Compiler audit #3 — Guard unchecked dictionary accesses in codegen

Replaced four bare `[]` indexer accesses in the IR codegen partial files with `TryGetValue` guards
that throw `InvalidOperationException` with a descriptive message naming the missing symbol:
- `EmitLoad` (`IRCodeGenerator.cs`) — `_globalVars[name]`
- `PeekType` switch (`IRMatchCodeGenerator.cs`) — `_globalVars[v.Name].Type`
- `PeekMethodType` (`IRMatchCodeGenerator.cs`) — `_userFunctions[$"{ns}.{fn}"]`
- `AssignmentStatement` case (`IRFunctionCodeGenerator.cs`) — `_vars[assignName]`

Previously these would throw opaque `KeyNotFoundException`s if the semantic analyzer ever failed
to catch an undefined reference.

### Compiler audit #2 — `printLn`/`printError` arity check

Added explicit arity validation for `printLn` and `printError` in `SemanticAnalyzer.AnalyzeExpression`,
matching the pattern already used by `clone`, `drop`, `exit`, `readFile`, and `writeFile`. Calling
either builtin with any argument count other than 1 now produces a `CompilationResult.Fail` with a
readable error message instead of crashing at codegen. The general call-site guard no longer needs
to exclude these names.

### Compiler audit #1 — Extract shared type resolution

Eliminated the parallel `ResolveTypeAnnotation` / `SuruTypeFromAnnotation` implementations that
were the highest divergence risk in the codebase. Added `SuruTypeSystem.TryResolve()` in
`src/Suru.Compiler/Types/SuruTypeSystem.cs` as the single source of truth for
`TypeAnnotation → SuruType` mapping. Both the semantic analyzer and IR codegen delegate to it;
adding a new primitive type now requires one edit instead of two.

### Stage 13c — Semantic Analyzer in Suru: Statement Analysis

Implements statement-level analysis in Suru itself (`tests/fixtures/suru-semantic/suru-semantic-stmts.suru`), building on the Stage 13a scope-chain data structures and the Stage 13b declaration pre-passes.

**New Suru file `suru-semantic-stmts.suru`** (211 lines):
- `analyzeLetStatement` — duplicate-in-scope check, type resolution via `resolveTypeName`, module-scope constant marking
- `analyzeAssignmentStatement` — constant-reassignment check (takes priority), undefined-variable check
- `analyzeFieldAssignmentStatement` — undefined receiver check for `NODE_VAR_REF` receivers
- `analyzeReturnStatement` — bare-return-in-non-void check (defers return-type mismatch to Stage 13e)
- `analyzeWhileStatement` — pushes a fresh scope around the body so sequential while loops can reuse variable names
- `analyzeStatement` — dispatch over `NODE_LET`, `NODE_ASSIGN`, `NODE_FIELD_ASSIGN`, `NODE_RETURN`, `NODE_WHILE`; `NODE_TYPE_DECL` and `NODE_FN_DECL` handled by pre-passes; `NODE_EXPR_STMT` deferred to Stage 13e
- `analyzeStatements` — iterates an `Array<AstNode>` and calls `analyzeStatement` on each; mutually recursive with `analyzeWhileStatement`
- Helpers: `isConstant`, `addConstant`, `markConstantIfModuleScope`, `checkBareReturn`

**`main.suru` extended** with 11 new unit tests for Stage 13c: `let_declares_symbol`, `let_duplicate_in_scope_reports_error`, `let_unknown_type_reports_error`, `let_at_module_scope_is_constant`, `assign_undefined_reports_error`, `assign_constant_reports_error`, `assign_valid_no_error`, `return_bare_in_nonvoid_reports_error`, `return_bare_in_void_no_error`, `while_scopes_do_not_leak`, `while_sequential_allow_same_name`.

**`IRSuruSemanticTests.cs` extended** — 11 new `Assert.Contains` checks (one per Stage 13c test case).

**Key codegen observations documented in file header:** `stmt.hasValue` and `stmt.body` are undeclared fields on `AstNode`; `hasValue` returns a `NamedType("")` ptr that `EmitMatchTestChain` correctly unboxes via `UnboxInt64` before `icmp eq i64 1`; `stmt.body` requires an explicit `let body Array<AstNode>: stmt.body` binding so the array annotation propagates to `body.len()` and `body.at(i)`.

---

### Transitive namespace propagation in include resolution

`ResolveIncludes` now propagates transitive namespaces and their function declarations into any importing module, lifting a constraint that prevented included files from calling functions in their own included namespaces.

**Problem:** when file A included file B (which itself included C as `sem`), B's function bodies could not call `sem.*` functions — those calls would fail semantic analysis in A's context because `sem` was not in A's `Namespaces` set, and C's functions were double-prefixed as `A.sem.foo` instead of `sem.foo`.

**Fix in `Compiler.cs` (`ResolveIncludes`):**
- After adding `ns` to `Namespaces`, all of `includedModule.Namespaces` are also added (transitive namespace propagation).
- When iterating `includedModule.Statements`, functions already in `includedModule.ExternalFunctions` are transitive — they are propagated as-is without a second namespace prefix. `externalFns.TryAdd` deduplicates when multiple siblings share the same transitive dependency.
- Functions declared directly in the included file continue to get the `ns.` prefix as before.

**Comment updated** in `tests/fixtures/suru-semantic/suru-semantic-passes.suru` to reflect that `sem.*` / `parser.*` calls are now safe from any including file.

All 106 tests pass.

---

### FunctionSignatures refactor — scoped function signatures and constants (internal)

Moved function-signature tracking and module-level constant tracking out of flat dictionaries in `SemanticAnalyzer` and into the scope stack so nested function declarations will be supported without additional plumbing.

**New file `FunctionSig.cs`:** `internal sealed record FunctionSig(IReadOnlyList<SuruType> ParamTypes, SuruType? ReturnType)` — replaces the anonymous tuple `(IReadOnlyList<SuruType>, SuruType?)` that was stored in `_functions`.

**`Scope.cs` extended:** each scope frame now holds three separate dictionaries: variable symbols (existing), function signatures (`Dictionary<string, FunctionSig>`), and a constant marker set (`HashSet<string>`). New methods: `DeclareFunction`, `TryGetFunction`, `ContainsFunction`, `MarkConstant`, `IsConstant`.

**`Scopes.cs` extended:** stack-walking methods added for function signatures (`RegisterFunction`, `FunctionExistsInCurrent`, `LookupFunction`) and constants (`MarkConstant`, `IsConstant`). All four follow the same top-to-bottom stack-walk pattern as the existing `Lookup` for variables.

**`SemanticAnalyzer.cs` simplified:** `_functions` and `_constants` fields removed. All call sites updated to use `_scopes.RegisterFunction`, `_scopes.LookupFunction`, `_scopes.FunctionExistsInCurrent`, `_scopes.MarkConstant`, and `_scopes.IsConstant`.

**`InternalsVisibleTo.cs` added:** `[assembly: InternalsVisibleTo("Suru.Tests")]` so the test project can directly unit-test `internal` infrastructure classes.

**New test file `FunctionSignaturesTests.cs`:** 23 tests covering `FunctionSig` record behaviour, `Scope`/`Scopes` unit tests for function and constant storage, and semantic integration tests verifying all existing checks (arg-count validation, duplicate function detection, constant reassignment) continue to work through the new scoped API.

All 106 tests pass.

---

### SuruType class hierarchy refactor (internal)

Replaced the flat `SuruType` enum with a class hierarchy so every type value carries its full static information.

**`SuruType` is now an abstract class** with eight sealed subclasses: `BoolType` (tag 0), `Int32Type` (tag 1), `Int64Type` (tag 2), `Float64Type` (tag 3), `NamedType(string Name)` (tag 4), `ArrayType(SuruType Element)` (tag 5), `StringType` (tag 6), `VoidType` (no tag — only used for void function returns in codegen). The six primitive singletons (`SuruType.Bool`, `.Int32`, `.Int64`, `.Float64`, `.String`, `.Void`) are `static readonly` fields.

**Three side-dictionaries eliminated:**
- `_arrayElementTypeNames: Dictionary<string, string>` in `SemanticAnalyzer` — element type now lives in `SuruType.ArrayType.Element`
- `_varStructTypeNames: Dictionary<string, string>` in `SemanticAnalyzer` — struct type name now lives in `SuruType.NamedType.Name`
- `_arrayElementTypes: Dictionary<string, SuruType>` in `IRCodeGenerator` — element type carried by the `SuruType.ArrayType` stored in `_vars`

**`SemanticAnalyzer` changes:** `ResolveTypeAnnotation` returns `new SuruType.ArrayType(inner)` and `new SuruType.NamedType(name)`; `InferType` resolves field access types via `.Element` and `.Name` rather than flat dictionary lookups. Scope infrastructure refactored into `Scope.cs` / `Scopes.cs`.

**`IRCodeGenerator` changes:** `FnReturnSuruType` returns `SuruType.Void` for void functions (was `SuruType.Struct`). `IsScalar` / `LlvmType` / `UnboxScalar` use C# type patterns. `EmitArrayAt` reads the element type from `SuruType.ArrayType.Element` in `_vars`.

All 83 tests pass.

---

### Stage 13b — Semantic Analyzer in Suru: Declaration Pre-passes

Implements the two declaration pre-passes of the Suru semantic analyzer in Suru itself (`tests/fixtures/suru-semantic/suru-semantic-passes.suru`), building on the Stage 13a scope-chain data structures.

**New Suru functions:**
- `isBuiltinType(name String) Bool` — true for the six primitive type names
- `resolveTypeName(state AnalyzerState, name String) Bool` — true for built-ins and any name in `state.typeNames`
- `collectTypeDeclarations(state AnalyzerState, stmts Array<AstNode>) AnalyzerState` — pass 1: walks module statements, registers `NODE_TYPE_DECL` names into `typeNames`, errors on duplicates
- `collectFunctionDeclarations(state AnalyzerState, stmts Array<AstNode>) AnalyzerState` — pass 2: walks module statements, registers `NODE_FN_DECL` signatures into `functions`, validates param/return types via `resolveTypeName`, errors on unknown types and duplicate names
- `runPrePasses(state AnalyzerState, stmts Array<AstNode>) AnalyzerState` — runs pass 1 then pass 2 in order

**New unit tests** (8, bringing the fixture total to 13): `resolveTypeName_builtin`, `resolveTypeName_user_declared`, `collectTypeDecls_registers`, `collectTypeDecls_duplicate`, `collectFnDecls_registers`, `collectFnDecls_duplicate`, `collectFnDecls_unknown_param`, `runPrePasses_cross_pass` (verifies a type declared in pass 1 is accepted as a param type in pass 2).

**Codegen fix — array element type propagation:** `IRCodeGenerator` now maintains `_arrayElementTypes: Dictionary<string, SuruType>` (reset per function), populated from `let` and parameter type annotations when the declared type is `Array<T>`. `EmitArrayAt` accepts an optional element type and uses it to return the correct `SuruType` — unboxing scalars and returning `SuruType.String` for `Array<String>` elements. This fixes chained method calls like `arr.at(i).equals(x)` when the array is `Array<String>`, which previously dispatched through integer compare instead of `suru_string_equals`. The fix uses the declared type from syntax, not inference.

**Codegen fix — nested match in statement context:** `EmitMatchArmBodyAsStatement` now routes `MatchExpression` arm bodies through `EmitMatchAsStatement` instead of `EmitValue`. Previously, a nested match used as a statement arm created a result alloca and attempted `store ptr 0` when arm return types were incompatible (e.g. `AnalyzerState` vs void-returning `array.add()`), producing invalid LLVM IR.

All 83 tests pass.

---

### Refactor: Remove Legacy PropagateStructMeta / _structScopes

Replaced the legacy struct-metadata tracking system with a simple flat dictionary, matching the pattern already used for `_arrayElementTypeNames`. No behaviour change — all 83 tests pass.

**Motivation:** `SuruType` is a flat enum — `LookupSymbol("p")` returns `SuruType.Struct` with no way to recover the declared type name. The old system patched this by caching the full resolved field layout (`List<(string, SuruType)>`) in a scope-parallel stack. Since Suru `let` statements have mandatory type annotations, the annotation name is always available directly — no need to derive it from the value expression.

**Removed from `SemanticAnalyzer`:**
- `_structScopes: Stack<Dictionary<string, List<(string Name, SuruType Type)>>>` — scope-parallel field layout cache
- `_functionReturnStructSymbols: Dictionary<string, List<…>>` — function return field cache
- `PropagateStructMeta(varName, value, typeName)` — 45-line dispatcher with 6 value-expression cases
- `LookupStructMeta` / `DeclareStructMeta` helper methods
- `_functionReturnStructSymbols` population block in `AnalyzeReturnStatement`
- `_structScopes` push/pop from `PushScope`/`PopScope`

**Added:**
- `_varStructTypeNames: Dictionary<string, string>` — maps var/param name → declared type name (e.g. `"p" → "Point"`); set in `AnalyzeLetStatement` and `AnalyzeFunctionDeclaration`.
- `InferType` `var.field` case updated: `_varStructTypeNames` + `_typeDeclarations` lookup, consistent with the existing `arr.at(i).field` case.

---

### Universal AST Type Annotation + Array Element Type Tracking

Every `Expression` AST node now carries a `ResolvedType` property set by the semantic analyzer.
This eliminates codegen guessing and enables direct field access on `arr.at(i).field` chains.

**Changes:**

- **`Expression` base class** (`Parse/Ast/Expression.cs`) — new `public SuruType? ResolvedType { get; set; }` property; `FieldAccessExpression` no longer declares its own (inherited).
- **`SemanticAnalyzer`** (`Semantic/SemanticAnalyzer.cs`):
  - `_arrayElementTypeNames: Dictionary<string, string>` — records element type name for every `Array<TypeName>` variable or parameter declaration.
  - `RecordArrayElementType` helper — called from `AnalyzeLetStatement` and `AnalyzeFunctionDeclaration`.
  - `InferType` extended — new case resolves `arr.at(i).field` via `_arrayElementTypeNames` + `_typeDeclarations`, returning the correct field type instead of `null`.
  - `PropagateStructMeta` extended — new case propagates field layout metadata when the value is `arr.at(i)` for a tracked element type, enabling subsequent `let x T: arr.at(i)` + `x.field` access.
  - `AnalyzeExpression` — annotation call `expr.ResolvedType = InferType(expr)` moved to the bottom of the method so it fires for **every** expression node, not just `FieldAccessExpression`.
- **`suru-semantic.suru`** (`tests/fixtures/suru-semantic/`) — field-extractor workaround removed:
  - Deleted `entryName`, `entryTypeName`, `scopeParent` helper functions.
  - `lookupInScopeAt` uses `symbols.at(i).name` and `symbols.at(i).typeName` directly.
  - `lookupSymbolAt` uses `scopes.at(idx).parent` directly.
- **`SemanticAnalyzerArrayElementTypeTests.cs`** — 6 new unit tests covering String/Int64 field access via function parameters and `let` variables, chained method calls, and expression annotation coverage.
- All 83 tests pass.

---

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
