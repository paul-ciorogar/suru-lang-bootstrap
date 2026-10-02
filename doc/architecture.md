# Compiler architecture, in pictures

Plain-text diagrams of the bootstrap compiler: its projects, the pipeline, which stage does
what for each language feature, and the pieces several stages share. Prose lives in
[CLAUDE.md](../CLAUDE.md); this page is the map. Edit the diagrams when the code moves.

## 1. Projects

```
                 +-----------+
                 | Suru.Lib  |   Result<T, S> - depends on nothing
                 +-----------+
                   ^       ^
                   |       |
        +--------------+  +-----------+
        | Suru.Compiler|<-| Suru.CLI  |   Program -> Spec -> Command
        |  (all logic) |  +-----------+   DumpSpec: "--dump=ast" -> Dump flags
        +--------------+
               ^
               |          +-----------+
               +----------| Suru.Tests|   mirrors src/ ; Integration/ runs binaries
                          +-----------+

        Suru.LSP  - empty scaffold
```

## 2. The pipeline (`Compiler.Build`)

```
  main.suru (text)
       |
       v
  +---------+  BuildMode decided HERE:
  |  Lexer  |  Production -> '#' line skipped like '//'
  +---------+  Test       -> '#' becomes a Hash token
       |  pulls one Token at a time (never a list)
       v
  +---------+
  | Tokens  |  cursor: Current / Next / Peek / PeekN (lookahead queue)
  +---------+
       |
       v
  +---------+                                   dump: tokens (re-lexes the source)
  | Parser  |--- ParseException / LexException --------------------+
  +---------+                                                       |
       |  Module (untyped AST)                  dump: ast           |
       v                                                            |
  +-----------------+                                               |
  | SemanticAnalyzer|--- error list (all errors, not the first) ----+
  +-----------------+    sets Expression.Type in place              |
       |  Module (same nodes, now typed)        dump: typed-ast     |
       v                                                            |
  +---------------+                                                 |
  | CodeGenerator |--- CodegenException ("internal compiler error") +
  +---------------+    told BuildMode: Test adds suru_frame_* calls |
       |  LLVMModuleRef                         dump: llvm          |
       v                                                            |
  +---------------+                                                 |
  | TryVerify     |--- invalid module (always checked) -------------+
  +---------------+                                                 |
       |                                                            |
       v                                                            |
  +---------------+                                                 |
  | Emit object   |  build/main.o                                   |
  +---------------+                                                 |
       |                                                            |
       v                                                            |
  +---------------+  cc main.o [runtime/suru_rt.c] -o build/main    |
  | Link (cc)     |  the shim only in Test mode --------------------+
  +---------------+                                                 |
       |                                                            v
       v                                              +-------------------+
  build/main  ------------------------------------->  | CompilationResult |
                                                      |   Ok / Fail       |
                                                      +-------------------+
                                                       only the CLI prints
```

`suru build` stops here. `suru test` goes on to section 6.

## 3. What each stage does for each feature

`-` means the stage has nothing specific for that feature: the token passes through
unchanged, or an earlier stage already turned the feature into something general.

```
 feature            | Lexer          | Parser                 | Semantic                  | Codegen
 -------------------+----------------+------------------------+---------------------------+----------------------------
 literals           | Int/Float,     | (-)n folded into the   | bool / i64 / f64          | const of the LLVM type
  true 1 2.5        | True/False     | literal                |                           |
 -------------------+----------------+------------------------+---------------------------+----------------------------
 operators          | + - * / % ==   | NO precedence:         | operand types must agree; | instruction picked from
  a + b * c         | < and or not   | folds left to right    | sets result type          | the resolved TYPE, not
                    |                | ((a + b) * c)          |                           | from the node class
 -------------------+----------------+------------------------+---------------------------+----------------------------
 let x i64: 1       | Let            | LetStatement           | resolve type name, check  | alloca in 'entry',
                    |                | (+ TypePosition)       | value, Declare in scope   | store in 'body'
 -------------------+----------------+------------------------+---------------------------+----------------------------
 x: 2               | Ident Colon    | AssignmentStatement    | LookupVariable,           | store into the slot
                    |                |                        | types must match          |
 -------------------+----------------+------------------------+---------------------------+----------------------------
 { ... }            | braces         | BlockStatement         | EnterNew / Exit scope;    | EnterNew / Exit scope,
                    |                | Kind = Plain           | shadowing allowed         | NO new basic block
 -------------------+----------------+------------------------+---------------------------+----------------------------
 if / else          | If Else        | IfStatement; arms are  | condition must be bool    | if.then / if.else / if.end
 else if            |                | Kind = Branch blocks;  | (else if: no case)        | (else if: no case)
                    |                | 'else if' = Else holds |                           |
                    |                | another IfStatement    |                           |
 -------------------+----------------+------------------------+---------------------------+----------------------------
 while              | While          | WhileStatement, body   | condition must be bool    | while.cond / .body / .end
                    |                | Kind = Loop            |                           | condition emitted INSIDE
                    |                |                        |                           | while.cond, every pass
 -------------------+----------------+------------------------+---------------------------+----------------------------
 break / continue   | Break Continue | Break/ContinueStatement| TryFindEnclosing(Loop)    | br to the loop's end /
                    |                | (position only)        | else "not in a loop"      | cond block (from the scope)
 -------------------+----------------+------------------------+---------------------------+----------------------------
 printLn(x)         | Ident ( )      | CallExpression         | builtin: 1 printable arg, | printf, one format string
                    |                |                        | type void                 | per type
 -------------------+----------------+------------------------+---------------------------+----------------------------
 fn f(a i64) i64 {} | Fn Return      | FunctionDeclaration,   | signatures collected for  | NOT YET: throws
 return x           |                | body Kind = Function;  | the whole block first     | "cannot emit statement"
  (in progress)     |                | ReturnStatement (value | (recursion, calls before  |
                    |                | only on the same line) | the declaration); not in  |
                    |                |                        | if/while; every path must |
                    |                |                        | return                    |
 -------------------+----------------+------------------------+---------------------------+----------------------------
 #mock x: 5         | Hash (Test     | MockDirective          | checked like assignment   | store, like assignment
                    | only)          |                        |                           |
 #view x:           |                | ViewDirective  (id)    | subject must be printable | WriteFrame -> suru_field
 #assert(a, b):     |                | AssertDirective (id)   | a and b must be the same  | compare, WriteFrame
                    |                | one directive per line,| type                      |
                    |                | rest of line discarded |                           |
```

The `else if` row shows the design rule: a feature that the parser can express in terms of
an existing one costs nothing in any later stage.

## 4. `ScopeStack`: one structure, two users

```
                       ScopeStack<TEntry, TScope>
                       innermost scope is last; lookups walk outward
        +----------------------------------------------------------------+
        |  [file: Plain]  [Plain]  [Function]  [Loop]  [Branch]  <- top  |
        +----------------------------------------------------------------+
               ^                                         ^
               |                                         |
   SemanticAnalyzer                              CodeGenerator
   TEntry = Binding (type)                       TEntry = VariableSlot (alloca)
   TScope = NoScopeData                          TScope = (cond block, end block) for a Loop

   Scope kind is set by the PARSER on BlockStatement.Kind and read downstream:

     Plain     { }              a bare block    fn allowed here
     Function  fn body          the barrier     fn allowed here
     Loop      while body       break/continue target
     Branch    if / else arm    fn NOT allowed (it may never run)

   Three walks, three ways to treat a Function scope (the barrier):

     LookupVariable    walk on; after crossing the barrier, only function names
                       are visible (a body cannot see its caller's locals)
     LookupFunction    no barrier (recursion: the name lives outside the body)
     TryFindEnclosing  STOP at the barrier (a break inside fn can't reach the
                       loop that calls the fn)

   example: inner names shadow outer ones

     let x i64: 1        scope 0: x -> i64
     {                   scope 1
       let x f64: 2.0    scope 1: x -> f64     lookup "x" finds scope 1 first
     }                   exit scope 1          lookup "x" finds scope 0 again
```

## 5. Codegen: basic blocks inside `main`

Everything goes into one function, `main`, for now.

```
  main:
  +-----------------------------+
  | entry:                      |  only allocas, emitted by a second builder
  |   %x = alloca i64           |  that stays parked here; 'entry' dominates
  |   %y = alloca f64           |  every block, so no phi nodes are needed
  |   br body                   |
  +-----------------------------+
                |
                v
  +-----------------------------+
  | body:                       |  the statements, in order
  |   store 1, %x               |
  |   ...                       |
  +-----------------------------+

  if c { A } else { B }                   while c { S }

      [current]                               [current]
      cond c                                      |
      br c, then, else                            v
       /          \                         +-> while.cond:  <- condition emitted here,
   if.then:     if.else:                    |     cond c        re-evaluated each pass
    A            B                          |     br c, body, end
    br end       br end                     |      |          \
       \          /                         |      v           \
        if.end:                             |   while.body:     \
                                            |     S              \
                                            +---  br cond         \
                                       continue ->  while.cond     v
                                       break    ->  while.end:  while.end:

  Rules that keep it valid:
   - every jump to an end block goes through BranchTo (does nothing if the
     block already has a terminator)
   - EmitStatements stops at the first statement that terminates the block,
     so code after break/continue is analyzed but never emitted
   - an if.end with no predecessors is fine only because there are no phis
```

## 6. `suru test`: running the program and annotating the file

```
  Compiler.Test
       |
       |  Build(..., BuildMode.Test)       '#' lexed, frames emitted, shim linked
       v
  +-------------+   1. bind a unix socket in the temp dir, start accepting
  | TestChannel |   2. start child with SURU_TEST_CHANNEL=<socket path>
  +-------------+   3. read socket AND child stdout CONCURRENTLY
       |      ^                (doing them one after the other deadlocks when a pipe fills)
       |      |
       |   frames over the socket                stdout (passed through)
       |      |                                       ^
       v      |                                       |
  +----------------------------------------------------------+
  |  build/main  (child process)                             |
  |    run-started                                           |
  |    ... suru_frame_begin / suru_field / suru_frame_end    |  <- runtime/suru_rt.c
  |    run-finished                                          |
  +----------------------------------------------------------+
       |
       |  deadlines:  --connect-timeout (200 ms)  child never connected
       |              --timeout         (1000 ms) program never ended
       |  any channel failure -> TestChannelException -> source NOT rewritten
       v
  +-------------+   FrameReader: bytes -> Frame  (wire format: doc/test-protocol.md)
  | FrameReader |
  +-------------+
       |
       v
  +-------------+   match frame.id -> ViewDirective / AssertDirective.Id
  |   TestRun   |   rewrite each directive line in the source file
  +-------------+
       |
       v
   directive line outcomes:
     frame received              -> value / pass / fail written after the ':'
     run-finished, no frame      -> undefined  (the directive was never reached)
     no run-finished (crash)     -> line blanked (nothing is known)
```

## 7. Debug dumps

```
  --dump=tokens,ast,typed-ast,llvm   or   SURU_DUMP=...
        |
   DumpSpec (CLI) parses the names -> Dump [Flags]
        |
   DumpOptions(flags, stderr)  ->  Section(stage, title, () => body)
                                         the body callback runs only if
                                         that stage is enabled

   tokens     TokenPrinter   lexes the source again; the parser's cursor is untouched
   ast        AstPrinter     after parse
   typed-ast  AstPrinter     withTypes: true, printed even when analysis failed
   llvm       PrintToString  printed BEFORE verification
```
