# User-defined functions

## Context

Suru has no user-defined functions. Every program is a flat sequence of statements emitted into a single `main`, and `printLn` is the only callable thing in the language — hard-coded by name in [SemanticAnalyzer.cs:365](../../src/Suru.Compiler/Semantic/SemanticAnalyzer.cs#L365) and special-cased again in [CodeGenerator.cs:211](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L211).

This is the feature the codebase has been building toward and explicitly deferring to. Five checked-in places wait on it:

- The `TODO(scope-kinds)` design note at [ScopeStack.cs:14-35](../../src/Suru.Compiler/ScopeStack.cs#L14-L35) says a `Function` scope kind is the barrier that makes `break`-across-a-function-boundary impossible by construction, and to "add the member when functions exist, not before". Matching TODOs sit in both stages.
- [CodeGenerator.cs:405-409](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L405-L409) and [expressions.md](../expressions.md) both state that `and`/`or` need no branching because no expression can have a side effect, and that "short-circuiting arrives with user-defined functions". A call is that side effect.
- [control-flow.md](../control-flow.md): "There is no `return`, because there are no functions to return from."
- [todo.md](todo.md) blocks `#spec`/`#save` on functions ("a test case is per-function; Suru has no functions") and blocks `undefined`-as-a-value on them ("no parameter can go unmocked without one").
- [blocks.md](../blocks.md): "user-defined functions are still to come."

**Outcome:** `fn` declarations with typed parameters and an always-written return type, `return`, direct calls including recursion and mutual recursion, short-circuiting `and`/`or`, and the scope-kinds refactor the codebase has been holding a place for.

## Decisions

These are the plan's premises, not open questions.

| | |
|---|---|
| **Syntax** | `fn name(a i64, b i64) i64 { ... }` — return type always written, `void` included |
| **Return** | `return <expr>`, and bare `return` in a `void` function |
| **Value follows a `return`?** | **Same-line rule**: only if it begins on the `return`'s own line. This is the language's first newline-sensitive statement rule; it is worth it because the token-set alternative silently reparses the next statement as the returned value |
| **Where `fn` may appear** | File top level, and directly in another function's body. **Not** in an `if` arm, `while` body, or bare block |
| **Enforced by** | The **semantic analyzer**, not the parser — so a misplaced `fn` is collected alongside every other error rather than throwing at the first one |
| **Function-name scope** | Lexical. A nested `fn` is visible only inside the body it sits in |
| **Namespace** | One, shared with variables. `let f i64: 1` + `fn f() void {}` collide with the existing `'f' is already declared`. `printLn` is reserved |
| **Body sees** | Its parameters, and function names from enclosing scopes. **No** enclosing variables — no closures |
| **Recursion** | Yes, including mutual, via a per-statement-list signature pre-pass |
| **Missing return** | Semantic error. `while` never counts as returning, so `fn f() i64 { while true { return 1 } }` is rejected — accepted cost, no constant folding exists |
| **Short-circuit `and`/`or`** | In this slice |
| **`#view`/`#assert` in a body** | Work exactly as today — frames wherever they sit, file stays the test unit |
| **Non-void call as a statement** | Allowed (result discarded), with a `TODO` to require a `_` binding once one exists |
| **First-class function values** | Out of scope |

## The crux: a per-query barrier

One namespace + lexical scoping puts function names in the same `ScopeStack` as variables. But a body must not see the enclosing function's locals, while recursion requires it to see its *own* name — declared in the scope **outside** the body. So the `Function` scope cannot block everything uniformly.

**Rule: crossing a `Function` scope hides variable bindings and lets function bindings through.** Three named queries, and the barrier applies per-query:

| Query | Crossing a `Function` scope |
|---|---|
| variable lookup (`IdentifierExpression`, assignment, `#mock`) | **stops** — outer locals invisible |
| function lookup (`CallExpression`) | **passes through** — recursion, siblings, parent |
| loop lookup (`break`/`continue`) | **stops** — a `break` in a function called from a loop sees no loop |

```suru
fn outer(n i64) i64 {
  let local i64: 1
  fn inner(a i64) i64 {
    // sees: inner, outer, printLn, a
    // NOT:  n, local
    return outer(a)
  }
  return inner(n)
}
```

This **amends the checked-in note** at [ScopeStack.cs:14-35](../../src/Suru.Compiler/ScopeStack.cs#L14-L35), which assumes a uniform barrier. Rewriting that note is part of the work, not a side effect.

The rule lives in **one place**, not in each stage. `ScopeStack`'s entry type gains a tag saying whether the binding survives a barrier; each stage only says which of its two binding shapes is which:

```csharp
public interface IScopeEntry { bool SurvivesFunctionBoundary { get; } }
// variable binding => false      function binding => true
```

This is what the note already demands: "the kind must be derived from the AST node being walked, never written by hand per call site, or the two stages can drift on it."

**Payoff in codegen:** because the barrier already hides variables, a nested function's emitter can *share* the one module-level `ScopeStack` and simply enter a `Function` scope on it. No copying of visible bindings, no per-function stack, no save/restore — the barrier is the mechanism. Only the two LLVM builders are per-function.

---

## Task 1 — scope-kinds refactor (no behaviour change)

- [x] **Done.** Pure refactor, no behaviour change. The only test edits were five `ParserTests` dump literals gaining ` loop` on a `while` body, which is the change being asserted.

Two deviations from the plan as written, both narrowing task 1 rather than the end state:

- **`IScopeEntry` and the split `TryLookupVariable`/`TryLookupFunction` are deferred to task 4.** With no function bindings in existence every entry would answer `SurvivesFunctionBoundary` the same way, and `TryLookupFunction` would have no caller — a member no lookup distinguishes, which is the thing the design note warns against adding early. `TryLookup` stays uniform for now; the barrier it will need is already described in the rewritten note. Task 4 adds the two entry types and splits the query.
- **`TryFindEnclosing` already honours the `Function` barrier**, and `ScopeStackTests` enters a function scope directly to pin it, so the rule is tested even though no stage creates one yet.

**[ScopeStack.cs](../../src/Suru.Compiler/ScopeStack.cs)** — becomes `ScopeStack<TEntry, TScopeData> where TEntry : IScopeEntry`. A scope is `(ScopeKind Kind, TScopeData Data, Dictionary<string, TEntry> Names)`. Add `enum ScopeKind { Plain, Loop, Function }`. API:

- `EnterNew(ScopeKind kind, TScopeData data = default)` / `Exit()` / `Declare` / `DeclaredHere` — as today plus the kind.
- `TryLookupVariable(name, out entry)` — outward walk, stops admitting non-surviving entries once a `Function` scope is crossed.
- `TryLookupFunction(name, out entry)` — outward walk, no barrier.
- `TryFindEnclosing(ScopeKind kind, out TScopeData data)` — outward walk, stops at `Function`.

Introduce `ScopeKind.Function` **now, unused**, together with the rewritten design note explaining the per-query barrier — so task 2 adds no vocabulary, only uses it.

**[Statement.cs:35](../../src/Suru.Compiler/Parse/Ast/Statement.cs#L35)** — `BlockStatement` gains `ScopeKind Kind`, set by the parser (`Parser.cs:62-65`, `:124` carry the TODOs). `ParseBlock` takes the kind; `ParseWhile` passes `Loop`, everything else `Plain`. **[AstPrinter.cs:46-49](../../src/Suru.Compiler/Debug/AstPrinter.cs#L46-L49)** renders it — this changes existing expected dumps, so the parser/dump test literals get a mechanical update in this task and none later.

**[SemanticAnalyzer.cs](../../src/Suru.Compiler/Semantic/SemanticAnalyzer.cs)** — `_scopes` becomes `ScopeStack<Binding, Unit>`; the `BlockStatement` case (`:88`) passes `block.Kind`; `_loopDepth` (`:47`) and the bracketing lines in `AnalyzeWhile` (`:149-151`) are **deleted**; `RequireInLoop` (`:177`) becomes `TryFindEnclosing(ScopeKind.Loop, …)`. The diagnostic text is unchanged and its tests must not move.

**[CodeGenerator.cs](../../src/Suru.Compiler/Codegen/CodeGenerator.cs)** — `_scopes` payload gains the loop data; `_loops` (`:67`) is **deleted**; `EmitWhile` (`:321-337`) enters the loop scope with `(condBlock, endBlock)` as payload and emits `loop.Body.Statements` directly, per the TODO's own instructions; `Loop(keyword)` (`:383`) becomes the `TryFindEnclosing` search, keeping its throw.

---

## Task 2 — lexer

- [ ] **[TokenKind.cs](../../src/Suru.Compiler/Lex/TokenKind.cs)** — `Fn`, `Return`, placed with the statement keywords. **[Lexer.cs:82-96](../../src/Suru.Compiler/Lex/Lexer.cs#L82-L96)** — two arms in `ReadIdentifierOrKeyword`.

`void` does **not** become a keyword. It stays an ordinary `Identifier` like `i64`, so the parser never decides where it is writable — the analyzer does.

Tests: `TokenPrinter.Print` shows the new kinds; `fnord`/`returns` still lex as `Identifier`.

---

## Task 3 — AST and parser

- [ ] **`FunctionDeclaration : Statement`** (new file under `Parse/Ast/`) — it *is* a statement, because placement is a semantic rule and it must be parseable anywhere a statement can go. Carries `Name`, `IReadOnlyList<Parameter> Parameters`, `ReturnTypeName` + `ReturnTypePosition`, `BlockStatement Body`, and a mutable `SuruType? ReturnType { get; internal set; }`.

**`Parameter`** — `Name`, `TypeName`, `TypePosition`, mutable `SuruType? Type`. Mirrors `LetStatement` ([Statement.cs:18](../../src/Suru.Compiler/Parse/Ast/Statement.cs#L18)) exactly: types have no node hierarchy in this compiler and resolution stays a dictionary lookup in the analyzer. Mutable annotation matches the existing `Expression.Type` convention — codegen needs a resolved type and there is no initialiser to carry one.

**`ReturnStatement : Statement`** — `Expression? Value`.

`Module` is **unchanged** — declarations stay in `Statements`.

**[Parser.cs](../../src/Suru.Compiler/Parse/Parser.cs)** — `ParseStatement` gains `Fn` and `Return` cases.

```csharp
private FunctionDeclaration ParseFunction()
{
    var keyword = Expect(TokenKind.Fn);
    var name = Expect(TokenKind.Identifier);
    _ = Expect(TokenKind.LeftParen);
    var parameters = ParseParameters();
    _ = Expect(TokenKind.RightParen);
    var returnType = Expect(TokenKind.Identifier);   // 'void' is just an identifier here
    var body = ParseBlock(ScopeKind.Function);
    return new FunctionDeclaration(
        PositionOf(keyword), name.Text, parameters,
        returnType.Text, PositionOf(returnType), body);
}
```

`ParseParameters`/`ParseParameter` mirror `ParseArguments` ([Parser.cs:367-379](../../src/Suru.Compiler/Parse/Parser.cs#L367-L379)) line for line — comma-separated, zero allowed, no trailing comma.

```csharp
private Statement ParseReturn()
{
    var keyword = Expect(TokenKind.Return);
    // The same-line rule: a value belongs to this 'return' only if it starts on its line.
    // The one place a newline is significant, because without it the next statement would
    // silently become the returned value.
    var value = _tokens.Current().Line == keyword.Line && StartsAnExpression(_tokens.Current().Kind)
        ? ParseExpression()
        : null;
    return new ReturnStatement(PositionOf(keyword), value);
}
```

Note `return a` followed by a line starting `+ b` still continues the expression — `ParseExpression`'s leading-operator continuation is unaffected, since the value *began* on the `return`'s line.

**Critical constraint:** `ParseFunction`, `ParseParameters` and `ParseReturn` must use **only `Expect` and `Current()`, never `Peek()`** — the same discipline `ParseIf`/`ParseWhile` keep ([Parser.cs:86-90](../../src/Suru.Compiler/Parse/Parser.cs#L86-L90)). `Tokens.DiscardRestOfLine` ([Tokens.cs:63-71](../../src/Suru.Compiler/Lex/Tokens.cs#L63-L71)) throws if anything is buffered, so a `Peek()` here would break `#view`/`#assert` as a body's first statement. State this in the doc comments.

**[AstPrinter.cs](../../src/Suru.Compiler/Debug/AstPrinter.cs)** — cases for both new nodes, or dumps silently degrade to a bare type name. A bare `return` renders with no child, which is exactly the distinction tests need.

At the end of this task the analyzer ignores `FunctionDeclaration`, so an `fn` program parses and then reports `unknown function` — an honest intermediate state.

---

## Task 4 — semantics

- [ ] **`Binding`** (new, `Semantic/`) — `VariableBinding(SuruType Type)` with `SurvivesFunctionBoundary => false`, and `FunctionBinding(FunctionSignature Signature)` with `=> true`.

**`FunctionSignature(string Name, IReadOnlyList<SuruType> Parameters, SuruType ReturnType, SourcePosition Position)`** — deliberately **not** a `SuruType`. [SuruType.cs](../../src/Suru.Compiler/SuruType.cs) is a sealed record compared structurally by `Name` at a dozen sites; with no first-class function values nothing can *hold* a function type, and there is no syntax to write one, so a `FunctionType` node would be unreachable speculative structure. It becomes a type the day a function is a value.

**`void` writable in one position.** Keep `NamedTypes` ([SemanticAnalyzer.cs:20-25](../../src/Suru.Compiler/Semantic/SemanticAnalyzer.cs#L20-L25)) exactly as it is and split the lookup in two:

- `ResolveBindingType(name, pos)` — the existing `NamedTypes` lookup; used by `let` and by every parameter.
- `ResolveReturnType(name, pos)` — `void`, or `ResolveBindingType`. The only caller is signature collection.

So `let x void: …` and `fn f(a void) i64` both keep reporting `unknown type 'void'`, with no message of their own.

**Per-statement-list signature pre-pass.** Every signature in a list is collected before any statement in it is analyzed — that is the whole of what makes recursion, mutual recursion and forward references work, and why it is a pass rather than a lazy lookup. It runs at scope entry: for the module's list, and for each `Function`-kind block.

```
DeclareSignatures(statements, isDeclarationContext):
  for each FunctionDeclaration in statements:
    resolve return type and each parameter type (annotating the nodes)
    reject a duplicate parameter name        -> "'a' is already declared"
    reject the name 'printLn'                -> "'printLn' is a builtin and cannot be redeclared"
    reject if not isDeclarationContext       -> "a function can only be declared at the top
                                                 level of a file or of another function"
    Declare(name, FunctionBinding(sig))      -- DeclaredHere gives the one-namespace collision
                                                check against a 'let' for free
```

Register the signature **even when a type name failed**, so calls report their own problems instead of cascading into `unknown function` — the same reasoning `AnalyzeLet` already gives at [SemanticAnalyzer.cs:202-205](../../src/Suru.Compiler/Semantic/SemanticAnalyzer.cs#L202-L205).

**`AnalyzeFunction`** — the body block enters the `Function` scope; **parameters are declared in that same scope**, so a `let` naming a parameter is `'a' is already declared` rather than a shadow. (Simpler than a second nested scope, and shadowing a parameter inside its own body reads as a mistake.) Track the enclosing declaration in a field so `return` knows its type; restore it on exit. No `_loopDepth` to save — the barrier handles it.

**`ResolveCall`** generalizes: `printLn` keeps its existing path *verbatim*, including the exact arity string `'printLn' expects 1 argument, got 2`; otherwise `TryLookupFunction`, then arity, then a per-argument type check. `SemanticTests` must pass **unedited** — it is the regression guard.

**New diagnostics** (all in the existing `path(line,col): message` form):

- `a function can only be declared at the top level of a file or of another function`
- `'printLn' is a builtin and cannot be redeclared`
- `'f' is already declared` — reused for duplicate function and duplicate parameter
- `'f' expects 2 arguments, got 1`
- `argument 2 of 'f' is of type 'f64'; expected 'i64'`
- `'return' can only appear inside a function`
- `'f' returns 'void'; 'return' cannot carry a value`
- `'f' must return a value of type 'i64'`  (bare `return` in a non-void function)
- `cannot return a value of type 'f64' from 'f' of type 'i64'`  (echoes `AnalyzeLet`'s phrasing)
- `'f' must return a value of type 'i64' on every path`

**Diagnostics that need no new code** — verify with tests only:

- A void call in expression position is caught by every existing consumer: `let x i64: v()` → `cannot bind a value of type 'void'…`; `printLn(v())`, `v() + 1`, `if v() { }` likewise.
- `break` inside a body → the existing `'break' can only appear inside a loop`, now **by construction** via the barrier rather than by a zero counter.
- A body reaching an outer local → the existing `unknown variable 'x'`.

**Missing-return analysis** — a pure static recursion, no state:

```csharp
private static bool AlwaysReturns(Statement statement) => statement switch
{
    ReturnStatement => true,
    // Any, not the last: a 'return' mid-block makes the rest unreachable, the same rule
    // 'EmitStatements' already applies.
    BlockStatement block => block.Statements.Any(AlwaysReturns),
    // Both arms, and there must be two. 'else if' arrives as a nested IfStatement and
    // needs no case of its own.
    IfStatement b => b.Else is not null && AlwaysReturns(b.Then) && AlwaysReturns(b.Else),
    // A loop's body may never run, and nothing folds 'while true' apart from 'while ready'.
    WhileStatement => false,
    _ => false,
};
```

It must agree with codegen's terminator discipline: if this says a body always returns and codegen leaves the end reachable and unterminated, the module fails verification. `TerminateFunction` below is the other half of that contract.

Add a `TODO` on the `ExpressionStatement` case ([SemanticAnalyzer.cs:71](../../src/Suru.Compiler/Semantic/SemanticAnalyzer.cs#L71)) recording that a discarded non-void result should require a `_` binding once one exists.

---

## Task 5 — codegen restructure (no behaviour change)

- [ ] `_allocas` is pinned to `main`'s `entry` and documented as "never moved" ([CodeGenerator.cs:28-33](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L28-L33)). A second function needs a second entry block, so **extract a `FunctionEmitter`** rather than repositioning a shared builder — "moved, but carefully" turns a wrong-frame slot into a silent bug.

**`FunctionEmitter` owns only what is truly per-function**: the two builders and the current `LLVMValueRef`. It builds the `entry`/`body` pair in its constructor and terminates `entry` with `BuildBr(body)` in `Finish()`, and is `IDisposable` so a `CodegenException` mid-emit no longer leaks the builders that [CodeGenerator.cs:140-141](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L140-L141) disposes in a straight line today.

`_scopes` **stays on `CodeGenerator`** — shared across functions, because the `Function` barrier already hides the parent's variables. That is the refactor paying for itself.

Everything builder-touching moves in unchanged: `EmitStatement`/`EmitStatements`/`EmitExpr`/`EmitIf`/`EmitWhile`/`EmitLet`/`EmitBinary`/`EmitUnary`/`EmitPrintLn`/`EmitView`/`EmitAssert`/`Render`/`WriteFrame`/`Field`/`Printf`/`BranchTo`/`Terminated`.

**Gotcha:** `String()` ([CodeGenerator.cs:611](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L611)) interns via `BuildGlobalStringPtr`, which **requires a positioned builder**. The cache stays module-level on `CodeGenerator` (globals are module-scope, so sharing is correct); the signature becomes `String(LLVMBuilderRef builder, string value)`. Calling it unpositioned is an LLVM assertion, not a C# exception.

`main` is emitted through a `FunctionEmitter` like anything else — the only special things about it are its `run-started`/`run-finished` frames and its `BuildRet(0)`. Its unguarded `BuildRet` comment ([CodeGenerator.cs:128-131](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L128-L131)) gains `return` to its list of statements semantics rejects at top level.

**Acceptance: every existing integration test passes with zero edits.** This is the task to review hardest.

---

## Task 6 — codegen: functions

- [ ] **`Binding`** in codegen mirrors semantics: `VariableBinding(Slot, Type)` (`SurvivesFunctionBoundary => false`) and `FunctionBinding(Fn, FnType)` (`=> true`).

**Declare-all pass, keyed by node not name.** A recursive walk over the module's statements and every function body creates one `LLVMValueRef` per `FunctionDeclaration` into `Dictionary<FunctionDeclaration, (LLVMValueRef Fn, LLVMTypeRef Type)>` before any body is emitted — the codegen counterpart of the analyzer's pre-pass, and what makes forward and mutual references resolve to a real value. Keying by the **node** sidesteps nested-name collisions entirely (two sibling functions may each contain a `helper`).

- Symbol names are **qualified** — `outer.helper` — with `LLVMLinkage.LLVMInternalLinkage`. Nothing outside the module calls a Suru function, and internal linkage keeps a Suru name from colliding with libc's (`printf` in particular; benign anyway since every call goes through the stored value, never by name).
- `LlvmReturnType(type)` is `void ? LLVMTypeRef.Void : LlvmType(type)`, kept apart so `LlvmType` ([CodeGenerator.cs:478](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L478)) keeps throwing for `void` — a slot of no type is a bug.
- Each `LLVMValueRef` parameter gets its source name, so the IR reads.

**At scope entry, declare-and-emit the list's functions before its statements.** Not lazily when the declaration is reached: `EmitStatements` stops at the first terminated statement ([CodeGenerator.cs:349](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L349)), so an `fn` after a `return` would be declared to LLVM and never given a body — invalid IR. Each nested function is emitted with its own `FunctionEmitter` while the parent's scope stack is live; the parent's builders are untouched.

**Parameter prologue** — an alloca in `entry`, a store of `fn.GetParam(i)` at the top of `body`, a `Declare`. Everything downstream assumes an alloca (`Variable` at `:473`, the load at `:214`, the store at `:157`), and this is also what makes a parameter assignable and `#mock`-able for free.

**`EmitCall`** — the `printLn` special case stays *ahead* of the general one. Arguments are evaluated left to right into an explicitly typed array (`BuildCall2` has array and span overloads and a collection expression cannot choose). **A void call must be named `""`** — naming a void instruction is invalid IR.

**`ReturnStatement`** — `BuildRetVoid()` or `BuildRet(EmitExpr(value))`, and nothing else. `Terminated` (`:372`), `BranchTo` (`:365`) and `EmitStatements` (`:349`) already do the rest, so `return` inside an arm, inside a loop, or mid-block works through machinery written for `break`. This is the `while`/`break` work paying off.

**`TerminateFunction` — the trap.** For `fn f() i64 { if c { return 1 } else { return 2 } }`, `EmitIf` appends `if.end`, both `BranchTo` calls are no-ops, and the builder is left in a block with **zero predecessors and no terminator** — invalid IR. Today that cannot happen because a trailing `while` always branches back. So:

```csharp
private void TerminateFunction(SuruType returnType)
{
    if (Terminated) return;
    if (returnType == SuruType.Void) _builder.BuildRetVoid();
    else _builder.BuildUnreachable();   // AlwaysReturns proved it; the IR now says so
}
```

This is the one place a bug in `AlwaysReturns` becomes invalid IR rather than a bad program. `TryVerify` catches it, but test the both-arms-return case explicitly.

---

## Task 7 — short-circuiting `and` / `or`

- [ ] Dispatched from `EmitBinary` **before** the type switch, since it must not evaluate the right operand eagerly. Emits `and.rhs`/`and.end` (or `or.*`), a `cond-br`, and a phi over `Int1`.

Both incoming blocks are **re-read** from `_builder.InsertBlock` after emitting each operand, never assumed — emitting an operand may itself have been a short-circuit and moved the insert block. `EmitIf` already reads its function after the condition ([CodeGenerator.cs:247-251](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L247-L251)) in anticipation of exactly this, and `EmitWhile` reading it before (`:305`) stays correct because emission moves the insert *block*, never the function.

**The `if.end`-no-predecessors licence narrows rather than dies.** `and.end` always has at least one predecessor by construction (the `cond-br` edge), so the new phis are never orphaned. The invariant at [CodeGenerator.cs:236-243](../../src/Suru.Compiler/Codegen/CodeGenerator.cs#L236-L243) becomes: *a block with no predecessors is legal as long as it holds no phi; every phi this compiler emits sits in a block it just created with an edge into it.* Rewrite that comment and the matching [CHANGELOG.md](../../CHANGELOG.md) bullet.

Fixture must prove a call on the dead side is **not** made — a function that prints, on the right of an `and` whose left is false.

---

## Task 8 — test-mode directives in function bodies

- [ ] **[TestRun.cs:36](../../src/Suru.Compiler/Testing/TestRun.cs#L36)** seeds `CollectDirectives` from `module.Statements` only. A directive in a body would be collected by nobody, its frame would match no directive, and `TestRun` would **throw and leave the source file unwritten** — the exact failure its own comment warns about. Fix: recurse into `FunctionDeclaration.Body`.

`ReturnStatement` needs no case — it carries an `Expression`, and directives are statements.

Nothing else in `Testing/` changes: no protocol change, no shim change, no `Frame` change. Repeats and `undefined` are already right — a directive in a function called three times sends three frames with one id, and `TestRun` already keys by id (last-wins for `#view`, sticky for `#assert`) because `while` forced it. A directive in a never-called function is `undefined`, exactly as an untaken `if` arm is.

---

## Task 9 — docs and changelog

- [ ] Update every page that functions falsify.

| File | Change |
|---|---|
| `doc/functions.md` | **New.** Syntax, the always-written return type, the same-line `return` rule, recursion and mutual recursion, params-only scope, the `while true` false positive, every new diagnostic verbatim |
| [README.md](../README.md) | "There are no user-defined functions" → gone; add the `Functions` row |
| [program-structure.md](../program-structure.md) | Statement table gains `return`; drop functions from "Not yet supported"; note the file is statements *and* declarations |
| [control-flow.md](../control-flow.md) | "There is no `return`, because there are no functions to return from" |
| [blocks.md](../blocks.md) | "user-defined functions are still to come" → a body **is** a block, except that it sees only its parameters |
| [expressions.md](../expressions.md) | The Evaluation section — `and`/`or` now short-circuit |
| [printing.md](../printing.md) | "`printLn` is the only function" → "the only **builtin**" |
| [todo.md](todo.md) | Mark the `#spec`/`#save` per-function unit and `undefined`-from-unmocked-parameters as unblocked. Do not build them |
| [CLAUDE.md](../../CLAUDE.md) | The Project paragraph, and the Codegen paragraph's "everything is emitted into a single `main`" / "no function-declaration support yet". Also fix the stale `Parser.RequireLineToItself` reference — the method is `RequireNothingElseOnTheLine` |
| [CHANGELOG.md](../../CHANGELOG.md) | `## [Unreleased]` entries, in the existing long-prose style including rejected alternatives |

---

## Testing

**Unit layer first** — whole-dump `Assert.Equal` against raw string literals, never `Assert.IsType` chains; diagnostics as whole strings using `Source.Path`.

`Compiler/Lex/LexerTests` — new kinds; `fnord`/`returns` still identifiers.

`Compiler/Parse/FunctionTests` *(new)* — dumps for: no params/void; two params returning `i64`; bare `return`; a body containing a block, an `if/else`, a `while` with `break`; a nested `fn`; the same-line `return` rule (a `return` followed on the next line by `printLn(1)` must dump as a bare return plus a call). `ParseException` messages for a missing return type, a missing `(`, `fn f(a) i64 { }`.

`Compiler/Debug/DumpTests` — one `withTypes: true` dump proving `ReturnType` and each `Parameter.Type` land.

`Compiler/Semantic/FunctionSemanticTests` *(new)* — **accepts**: recursion; mutual recursion; a call to a function declared later; a nested `fn` calling its parent and its sibling; `return` inside an arm and inside a loop; a `void` function falling off its end; bare `return` in a `void` function. **Rejects**: every diagnostic listed in task 4; `fn` inside an `if` arm; a body reaching a file-level `let`; a body reaching the *enclosing function's* local; `break` in a body; `while true { }` as a whole `i64` body; the four void-in-expression-position messages.

`Compiler/Semantic/SemanticTests` — **must not be edited.**

`tests/fixtures/functions/main.suru` + `Integration/FunctionTests` — one printed sequence where any wrong edge changes the output, in the style of `tests/fixtures/while/main.suru`: a `void` function called twice; an `i64` function inside a larger expression; factorial; `isEven`/`isOdd`; a call to a later declaration; a nested helper; an early `return` from an arm and from a loop; `break`/`continue` in a body; a parameter assigned in the body; `f64` and `bool` returns; a zero-parameter function.

`tests/fixtures/short-circuit/main.suru` + test — a printing function on the dead side of `and`/`or` that must not run.

`tests/fixtures/directives-in-function/main.suru` + a class in `Compiler/Testing/` joined to `[Collection("Integration")]` via `GetTestRun` — `#view`/`#assert` in a body called three times (last-wins / sticky); a directive in an uncalled function (`undefined`); a `#mock` on a parameter. **This fixture is what fails loudly without the task 8 fix.**

## Verification

```bash
dotnet build Suru.slnx
dotnet test                                    # green at the end of every task
dotnet test --filter FullyQualifiedName~Function

dotnet run --project src/Suru.CLI -- build --dump=llvm tests/fixtures/functions/main.suru
./tests/fixtures/functions/build/main           # compare against the fixture's comments

cp -r tests/fixtures/directives-in-function /tmp/dif   # 'suru test' REWRITES its input
dotnet run --project src/Suru.CLI -- test /tmp/dif/main.suru
dotnet run --project src/Suru.CLI -- test /tmp/dif/main.suru   # re-parse the annotations
```

`TryVerify` runs unconditionally, so malformed IR surfaces as an internal compiler error rather than a crashing binary — the `--dump=llvm` step is for reading the shape, not for catching invalidity.

## Risks

1. **`TerminateFunction`'s `BuildUnreachable`** is the only place a semantic bug becomes invalid IR. `AlwaysReturns` and the terminator discipline must agree; the both-arms-return `if/else` is the case to pin.
2. **`String(builder, …)`** — `BuildGlobalStringPtr` needs a positioned builder; getting it wrong is an LLVM assertion, not a C# exception.
3. **`DumpIntegrationTests.EveryStackSlotSitsInTheEntryBlock`** slices from the *first* `entry:`. Emit `main` first so it keeps measuring what it claims to.
4. **Task 1 touches every existing AST dump literal** (the new `BlockStatement.Kind`). Mechanical, but it is the task most likely to produce a large diff in test files.
5. **Parameters share the function scope with body `let`s**, so `let a` on a parameter name is a collision rather than a shadow. Deliberate; reversible by giving the body a nested `Plain` scope.
