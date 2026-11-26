# `main` and file-level constants

## Context

Today a Suru file *is* `main`: its statements are emitted in order into the C entry point, and
there is no function a program starts from
([program-structure.md](../program-structure.md), [CodeGenerator.cs:93-121](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L93-L121)).
That has three costs that the next steps of the reactive-test work run into:

- **There is no unit to test.** "Tests run on functions" (`#spec` in [testing.md §Not yet supported](../testing.md#not-yet-supported), the TODO at
  the top of [directives-in-function](../../tests/fixtures/directives-in-function/main.suru)) needs
  every directive to sit in a function, and today half of them sit in no function at all.
- **Phase B wants the program body as a callable function** (`suru_run()` in
  [todo_test.md](todo_test.md#phase-b--one-binary-many-runs)). Today that body is the whole file,
  interleaved with the declarations around it.
- **A function cannot see anything at file level** ([functions.md §Scope](../functions.md#scope)
  carries a TODO for it), because a file-level `let` is a local of `main`, and the barrier hides a
  caller's locals.

**Outcome:** a program starts at `fn main() void`. A file holds only `let` and `fn`. A file-level
`let` is a **constant** whose value is a literal, and it is visible inside every function. A `#`
directive is only allowed inside a function. The pipeline, the test channel and the protocol are
unchanged.

```suru
let rate i64: 12
let verbose bool: false

fn scaled(n i64) i64 {
  return n * rate                  // a constant crosses the function barrier
}

fn main() void {
  let factor i64: 2
  #mock factor: 5
  #view scaled(factor): 60
  printLn(scaled(factor))
}
```

## Decisions

These are the plan's premises, not open questions.

| | |
|---|---|
| **Entry point** | `fn main() void` at file level. No parameters, no return value, exit code always 0. `fn main() i64` is a later, separate change |
| **`main` is optional** | A file with no `main` compiles to a program that does nothing and exits 0, so an empty file stays legal. A file of constants and functions is a library-shaped file that `suru test` can still build |
| **A wrong `main`** | `fn main(a i64) void` or `fn main() i64` at file level is an error, not "just another function". A near-miss on the entry point must not silently produce a program that does nothing |
| **`main` is reserved, at every level** | The file-level `fn main` is the only thing the name may ever mean. A nested `fn main`, a `let main`, a parameter named `main` and any call to `main` are all errors. That makes "`main` runs exactly once per run" a guarantee, which the wrapper's `run-started` → `main` → `run-finished` and Phase B's one-run-per-request both rest on, and it means a directive in `main` reports at most once per run. It also keeps a later `fn main() i64` free of the question of what a recursive call's exit code means. The precedents are C++, where `main` "shall not be used within a program", and Go's `init`, which cannot be referred to at all |
| **What a file may hold** | `let` and `fn` only. Everything else is an error at file level: a call (`printLn` included), an assignment, a block, `if`, `while`, `break`, `continue`, `return` |
| **Enforced by** | The **semantic analyzer**, not the parser. That follows the precedent [todo_functions.md](todo_functions.md) set for a misplaced `fn`, so every misplaced statement is reported in one run. It also means `ParserTests`, `LexerTests` and `DirectiveTests` keep parsing file-level statements unchanged |
| **A file-level `let` is a constant** | Same spelling, no new keyword. It cannot be assigned, and since `#mock` is an assignment, it cannot be mocked either (but see open question 1) |
| **A constant's value is a literal** | `true`, `false`, an integer or a float, including a leading `-`, which the parser already folds into the literal ([Parser.cs:358-364](../../src/Suru.Compiler/Parse/Parser.cs#L358-L364)). Nothing else: no operator, no other constant, no call. The compiler does no compile-time evaluation at all. `let area i64: 3 * 4` is `a constant's value must be a literal` |
| **Order does not matter** | A constant's value names nothing, so there is nothing for it to refer to forward or backward. Every function body sees **every** constant, wherever the `fn` and the `let` are written |
| **Codegen** | A use of a constant is the literal's LLVM immediate. There is no slot and no global |
| **Visibility** | A constant **survives the function barrier**, the way a function name does and a variable does not. That is a third binding shape in each stage, not a new lookup rule in `ScopeStack` |
| **Shadowing** | A parameter or a function's `let` may shadow a constant, as a block's `let` may shadow an outer one, because a body is a new scope. A constant and an `fn` of the same name at file level collide with the existing `'x' is already declared` |
| **Directives** | Only inside a function body, at any depth. At file level a directive is an error in a test build. A production build never lexes it, so a `#` line still cannot break `suru build` |
| **How a test run runs** | Unchanged: `suru test` runs the program, which runs `main`, and a directive in any function `main` reaches reports as today. Directives in a function `main` never calls read `undefined`. Function-as-test-unit is the next design, not this one |
| **The C entry point** | Emitted by codegen as a **wrapper**: `run-started` (test mode), a call to Suru's `main` if there is one, `run-finished` (test mode), `ret i32 0`. Suru's `main` is an ordinary internal function with the symbol `suru.main`. No Suru or C identifier can contain the dot, so it never collides with the C symbol, and it follows the same convention as `outer.helper` |

### What the emitted module looks like

```
 production                              test
 ──────────                              ────
 define i32 @main() {                    define i32 @main() {
   call void @suru.main()                  call @suru_frame_begin ... run-started
   ret i32 0                               call void @suru.main()
 }                                         call @suru_frame_begin ... run-finished
                                           ret i32 0
 define internal void @suru.main() {     }
   ... the body of 'fn main'
 }                                       define internal void @suru.main() { ... }

 define internal i64 @scaled(i64 %n) {   define internal i64 @scaled(i64 %n) {
   %r = mul i64 %n.load, 12   ◄── 'rate' is an immediate, never a slot or a global
 }                                       }
```

With no `fn main`, the wrapper is just `ret i32 0`, plus the two frames in test mode. The wrapper
is exactly the thing Phase B turns into `suru_run()`, which is why the frames go there rather than
inside `suru.main`.

---

## Tasks

The order keeps `dotnet test` green after every task. Task 1 adds `main` **without** taking
anything away, so every test and fixture can move into `main` while both forms still work. Task 3
is the only one that changes what an existing program means.

### 0. Bindings that survive the barrier *(~1.5h)*

- [x] **`ConstantBinding(SuruType Type)`** in the analyzer's
      [Binding.cs](../../src/Suru.Compiler/Semantic/Binding.cs) and
      **`ConstantSymbol(LLVMValueRef Value)`** in codegen's
      [Binding.cs](../../src/Suru.Compiler/Codegen/Binding.cs), both with
      `SurvivesFunctionBoundary => true`. `VariableType` reads a `ConstantBinding` as its type.
      `AnalyzeStore` rejects it with `'x' is a constant and cannot be assigned`, which `#mock`
      inherits for free. Codegen's identifier emission returns a `ConstantSymbol`'s value instead
      of a load. The symbol's value is built by the literal emission codegen already has, so
      there is no second copy of how a literal becomes LLVM.
      *Done when:* nothing declares either yet, so behaviour is unchanged, and the `ScopeStack`
      tests pass untouched. The barrier rule needing no edit is what this task proves.

### 1. `main`, alongside the file's statements *(~4h)*

A **transitional** state, removed in task 3: file-level statements still run, and then `main` is
called. It exists so tasks 2a and 2b can move code into `main` one test at a time.

- [x] **Analyzer: validate `main`.** In [DeclareSignatures](../../src/Suru.Compiler/Semantic/SemanticAnalyzer.cs#L167),
      a file-level `fn main` whose signature is not `() void` is
      `'main' must be declared as 'fn main() void'`, positioned at the `fn`.
- [x] **Analyzer: reserve `main`.** One `const string Main` beside `PrintLn`, and three checks,
      each reported *instead of* the error the same code would otherwise get, so a reserved name
      never also reads as `'main' is already declared`:
      - a `fn main` anywhere but the file level, in `DeclareSignatures`:
        `'main' can only be declared at file level`;
      - a `let main` at any level (a file-level one included) or a parameter named `main`:
        `'main' is reserved`, positioned at the name;
      - a call to `main` from anywhere, in `ResolveCall` before the lookup:
        `'main' cannot be called`. It is reported whether or not the file declares a `main`, so
        the rule does not depend on what else is in the file.

      Using `main` as a value or `#mock`ing it needs nothing new: the existing
      `'main' is a function and cannot be used as a value` already covers both.
- [x] **Codegen: the wrapper.** `_Generate` emits the C `main` as the wrapper described above.
      For the transitional state it emits the file's statements first and the `call @suru.main`
      after them. `EmitFunctions` gives the file-level `main` the symbol `suru.main`.
      `MainExitCode` stays the one constant both `ret` and `run-finished` read.
      *Done when:* a fixture with only `fn main() void { printLn(1) }` prints `1`, and
      `--dump-llvm` on every existing fixture diffs clean, since none of them declares a `main`
      yet.
- [x] **Tests.** In `FunctionSemanticTests`, each diagnostic asserted whole: the wrong signature,
      a `fn main` nested in a function and in a bare block, `let main` inside a function and at
      file level, a parameter named `main`, a call to `main` from `main` itself and from another
      function, and a call to `main` in a file that declares none. An integration case
      for a program whose only output comes from `main`, and one where `main` calls a helper
      declared below it.

### 2a. Move the fixtures into `main` *(~2h)*

- [x] Every `tests/fixtures/*/main.suru` moves its statements into `fn main() void { … }`. Its
      functions stay at file level. A file-level `let` moves into `main` too, so this task
      changes no fixture's meaning. **No compiler change**: every fixture's stdout and every
      annotated line must be identical. The `directives*` fixtures' annotations are rewritten in
      the temp-dir copy, so their checked-in tails stay what they are.
      Two test assertions had to change, because they depended on where the code sits in the
      file rather than on what it does. `TestModeTests` asserts the failing `#assert`'s position,
      which moved from `(16,1)` to `(17,3)`. `EveryStackSlotSitsInTheEntryBlock` sliced the
      module's first `entry`, which is now the C wrapper's and holds no allocas, so it slices
      `@suru.main`'s instead.
      *Done when:* `dotnet test` is green with only `.suru` files and those two assertions in the
      diff.

### 2b. Move the unit tests into `main` *(~4h)*

- [x] The sources in `SemanticTests`, `FunctionSemanticTests`, `DumpTests` and the analyzer
      cases in `DirectiveTests` that rely on file-level statements wrap them in `main`, written out
      in each raw string literal. There is no `Source.InMain` helper that prepends a line,
      because the positions in an asserted diagnostic must refer to text the reader can see.
      Every asserted diagnostic's line and column moves with it. (`Source.MainBody` only *reads*
      `main`'s statements, for the tests that check a resolved type.)
      `ParserTests`, `LexerTests` and the parse-only cases in `DirectiveTests` and `DumpTests`
      **stay as they are**. The parser still accepts any statement at file level, so they keep
      testing what they test.
      Left at file level for task 3: `AcceptsAnEmptyModule`, `ABodyCannotSeeAFileLevelBinding`,
      `ReportsAReturnOutsideAFunction`, `ReportsAReturnInABlockOutsideAFunction`, and three that
      bind only literals at file level and so stay valid as constants:
      `ReportsALetNamedMainAtFileLevel`, `ReportsALetNamedMainBesideTheEntryPointOnce` and
      `DirectiveNamesAreNotKeywords`.
      A case whose point is the file level itself (`return` outside a function, or "a body cannot
      see a file-level `let`") is left alone here. Task 3 rewrites it, since that is where its
      meaning changes.
      *Done when:* green with only test sources in the diff. **The largest task by line count, and
      purely mechanical.** Worth doing class by class, one commit each.

### 3. The flip: constants, and nothing else at file level *(~3h)*

- [x] **Analyzer: the file pass.** `_Analyze` stops treating the file as a statement list and
      makes three passes over it:
      1. `DeclareSignatures`, unchanged, so a constant named like a function collides.
      2. Constants. For each file-level `let`: if its value is not a `BoolLiteral`, `IntLiteral`
         or `FloatLiteral`, report `a constant's value must be a literal` at the value and do not
         analyze it further. That keeps an `x + 1` from also reporting `unknown variable 'x'`.
         Otherwise type it, check it against the declared type with the existing `cannot bind`
         wording, and declare a `ConstantBinding`. Anything else at file level is
         `only 'let' and 'fn' may appear outside a function`, reported **once per statement and
         not analyzed further**, so a file-level `while` does not also cascade into its body's
         errors. A file-level directive is `a directive can only appear inside a function`.
      3. Function bodies, through `AnalyzeFunction` as today.

      After this pass, `'return' can only appear inside a function` could only fire for a
      file-level `return`, which the general rule now reaches first, so it is deleted (open
      question 3). A `return` with no enclosing function is a compiler bug from then on.
      A constant whose value was rejected is still declared, so its uses do not also read as
      `unknown variable`. `AnalyzeLet` and the constant pass share `DeclareBinding`, so the two
      cannot drift on what they say about a name or a type.
- [x] **Codegen.** `_Generate` declares a `ConstantSymbol` per file-level `let` before
      `EmitFunctions`, and the wrapper stops emitting file-level statements: it is frames,
      `call @suru.main`, `ret`. The "it needs no terminator guard" comment on `run-finished` stays
      true for a better reason now: the wrapper contains no user code at all.
- [x] **Tests.** In `SemanticTests`: every new diagnostic, asserted whole. Also a constant used in
      a function, a constant used in a function declared *above* it, shadowing by a parameter and
      by a body's `let`, a constant `#mock`ed in a function, a negative literal, and an operator in
      a constant's value. The task-2b leftovers get rewritten. An integration fixture `constants/`
      covers each type, a negative `i64` and `f64`, and a constant read from a nested function.
      *Done when:* the transitional path from task 1 is gone and the full suite is green.

### 4. Documentation *(~3.5h)*

- [x] **Examples are body fragments unless the file is the point.** Every example in `doc/*.md`
      is written as file-level statements today, and after task 3 almost none of them compile
      as written. Wrapping all of them in `fn main() void { … }` would put two lines of noise
      around every three-line example of an operator or a `while`, so most of them are **not**
      wrapped. They are read as the body of a function, and
      [README.md](../README.md) says so once, in a short "Reading the examples" note under the
      first program.
      An example is a **whole file**, with its `main` written out, when any of these holds:
      - it declares a `fn` or a constant, since then the reader needs to see what sits at file
        level and what sits in `main`;
      - it shows a diagnostic with a position, since `(line,col)` must refer to text the reader
        can see. That is the reason task 2b gave for refusing `Source.InMain`;
      - it is about the file itself: README's first program, program-structure, and testing's
        opening example.

      Each example is still recompiled against the build rather than edited by hand, as the
      functions docs were. A fragment is compiled wrapped in `main`, and only its stdout is
      compared, which is why a fragment never carries a position.
- [x] **[program-structure.md](../program-structure.md).** The TODO goes. A file is `let` and
      `fn`, a program starts at `main`, `main` is optional, the new file-level diagnostic, and
      the reserved-name rules with their three diagnostics.
- [x] **[bindings.md](../bindings.md).** A section on constants: literal values only, visible in
      every function regardless of order, and cannot be assigned or mocked.
- [x] **[functions.md](../functions.md).** The Scope section's TODO goes: a body sees constants.
      A short `main` section, or a pointer to program-structure. "One namespace" gains `main`
      beside `printLn` as a name that cannot be redeclared, and "Recursion and order" says `main`
      is the one function that cannot be called.
- [x] **[testing.md](../testing.md).** Directives live in functions. A directive in a function
      `main` never reaches reads `undefined`. The opening example is rewritten, and its
      `#mock width` becomes a mock of a local inside `main`. The mocking-a-constant item already in
      "Not yet supported" is reworded from "will not be" to "is not", since constants then exist.
- [x] **[todo_test.md](todo_test.md).** Phase B's first task becomes "rename the wrapper to
      `suru_run` and move its frames into the shim". The hang fixture in §5 is
      `fn main() void { while true { } }`. The `#spec` paragraph in testing.md's "Not yet
      supported" notes that every directive now has a function to belong to.
- [x] **`CHANGELOG.md`** under `## [Unreleased]`, and **`CLAUDE.md`**: the language summary, the
      codegen paragraph (the wrapper, `suru.main`, constants as immediates) and the analyzer
      paragraph (the three file passes).

---

## Open questions

None of the questions still open blocks a task.

1. ~~**Should `#mock` reach a constant?**~~ **Decided: not in this plan.** `#mock` on a constant is
   rejected as an assignment is. The feature is deferred as a per-test-case override, recorded in
   [testing.md §Not yet supported](../testing.md#not-yet-supported), which waits on
   function-as-test-unit and Phase B.
2. ~~**Is `main` reserved below file level?**~~ **Decided: reserved at every level, and never
   callable.** See the decisions table and task 1.
3. ~~**Specific or general file-level messages.**~~ **Decided: the general one only.** A
   file-level `return`, `break` or `continue` is `only 'let' and 'fn' may appear outside a
   function`, like every other misplaced statement, and `'return' can only appear inside a
   function` is deleted, since nothing can reach it any more. `'break'`/`'continue' can only
   appear inside a loop` stay, because a function body can still reach them.
4. **Constant expressions later.** Literal-only means `let area i64: 3 * 4` and a constant
   defined from another constant are both errors. If either is wanted later, it needs a
   compile-time evaluator whose arithmetic matches the runtime's exactly (wrapping `i64`, and
   errors where the runtime is undefined, such as division by zero). Nothing in this plan stands
   in its way: the check in task 3 is the one place that would widen.
5. **Unused constants and unused `main`.** No warnings exist yet, so nothing is reported. This is
   noted only so the first warning pass knows these are candidates.

---

**6 tasks, about 18 h.** Tasks 0 and 1 add without removing. Tasks 2a and 2b are mechanical and
need no compiler change. Task 3 is the one behavioural change, and task 4 is docs. After it,
function-as-test-unit (the TODO in `directives-in-function`) and Phase B both have the function
boundary they need.
