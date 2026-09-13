# Control flow

Suru has two control-flow statements. `if` runs a block once when a condition holds, and `while`
runs one over and over for as long as a condition holds; `break` and `continue` leave a loop
early. That is all of it — there is nothing that leaves a *function* early, because there are no
functions yet.

## `if`

`if` runs a block when a condition holds, and an optional `else` runs another when it does not:

```suru
let x i64: 2

if x > 1 {
  printLn(1)
}

if x = 99 {
  printLn(0)
} else {
  printLn(2)
}
```

```
1
2
```

### The condition

The condition must already be a `bool`. Nothing converts implicitly, so a number is not a truth
value:

```suru
if 1 {
  printLn(1)
}
```

```
error: hello.suru(1,4): 'if' cannot branch on a value of type 'i64'; expected 'bool'
```

The usual way to get one is a comparison, which yields a `bool` however its operands are typed —
see [Expressions](expressions.md):

```suru
let ratio f64: 1.5

if ratio > 1.0 {
  printLn(ratio)
}
```

```
1.5
```

A `bool` binding works just as well, and so do `and`, `or` and `not`. Remember that `and` and
`or` still evaluate both sides, and that there is no operator precedence: `if a and b or c` folds
left to right like every other expression.

### `else` and `else if`

There is no `else if` keyword. `else` is followed by a block **or by another `if`**, and writing
the second is what `else if` is:

```suru
let x i64: 2

if x = 0 {
  printLn(0)
} else if x = 2 {
  printLn(2)
} else {
  printLn(9)
}
```

```
2
```

That is worth knowing for one reason: a chain is nested, not flat, so a final `else` belongs to
the nearest `if` above it. In the program above the `else` is the alternative to `x = 2`, not to
`x = 0`.

### The body is always a block

Both arms must be braced. There is no single-statement form:

```suru
if true printLn(1)
```

```
error: hello.suru(1,9): expected LeftBrace, got Identifier
```

The same applies after `else`, which must be followed by `{` or by `if`:

```suru
if true { } else printLn(1)
```

```
error: hello.suru(1,18): expected LeftBrace, got Identifier
```

An empty arm is legal and does nothing.

### Scope

An arm is a [block](blocks.md), so it is a scope, and it needs no rule of its own. A binding made
in an arm is gone at the `}`:

```suru
if true {
  let count i64: 1
  printLn(count)
}
printLn(count)
```

```
error: hello.suru(5,9): unknown variable 'count'
```

It may shadow a binding from outside, which comes back untouched afterwards:

```suru
let x i64: 1
if true {
  let x f64: 1.5
  printLn(x)
}
printLn(x)
```

```
1.5
1
```

Assignment is the other direction: an arm can store into a binding it can see, and the store
outlives the branch.

```suru
let total i64: 0
if true {
  total: total + 8
}
printLn(total)
```

```
8
```

Because each arm is its own scope, two arms may bind the same name without colliding.

### Whitespace

An `if` is not indentation-sensitive, and — like every other statement — needs no separator
around it. The condition is an ordinary expression, so the ordinary
[line-continuation rule](program-structure.md) applies: it carries on while the next line starts
with a binary operator.

```suru
if x
  > 1 {
  printLn(1)
}
else {
  printLn(0)
}
```

A `{` on the line after the condition still opens the body rather than being a block of its own,
because an `if` requires one. And `else` need not share a line with the `}` before it, as above.

### Testing inside a branch

A [`#` directive](testing.md) inside an arm behaves like one inside any other block, with one
addition: a directive in a branch that is not taken never reports, so the compiler writes
`undefined` into its line.

```suru
let x i64: 2

if x = 0 {
  #view x: undefined
  #assert(x, 0): undefined
}
```

An unreached `#assert` is neither a pass nor a failure — it is counted separately and does not
make `suru test` exit non-zero.

## `while`

`while` runs a block over and over for as long as its condition holds:

```suru
let i i64: 1
while i <= 3 {
  printLn(i)
  i: i + 1
}
```

```
1
2
3
```

The condition is tested **before every pass, including the first**, so a condition that is
already false runs the body no times at all. Nothing about the loop changes the counter for you:
`i: i + 1` is an ordinary statement, and leaving it out is how you write a loop that never ends.

Everything `if` says about its condition applies here unchanged. It must already be a `bool`:

```suru
while 1 {
}
```

```
error: hello.suru(1,7): 'while' cannot loop on a value of type 'i64'; expected 'bool'
```

The body is always braced, there is no single-statement form, and an empty body is legal:

```suru
while true printLn(1)
```

```
error: hello.suru(1,12): expected LeftBrace, got Identifier
```

### `break` and `continue`

`break` leaves the loop. `continue` abandons the current pass and goes back to the condition:

```suru
let i i64: 0
while i < 5 {
  i: i + 1
  if i = 2 { continue }
  if i = 4 { break }
  printLn(i)
}
```

```
1
3
```

Read it in order: 2 is skipped by the `continue` before it reaches the `printLn`, and 4 ends the
loop before it, so 5 never happens either even though `i < 5` would have allowed it.

Both take no operand. There are **no labels**, so each one belongs to the innermost loop it is
written inside — and an `if` or a block in between changes nothing, because neither is a loop:

```suru
let row i64: 0
while row < 2 {
  row: row + 1
  let col i64: 0
  while col < 3 {
    col: col + 1
    if col = 2 { break }
    printLn(col)
  }
  printLn(row)
}
```

```
1
1
1
2
```

The `break` is inside an `if` inside the inner loop, and it leaves *the inner loop* — the outer
one carries on, which is why each row still prints.

Written where there is no loop to leave, either is an error:

```suru
break
```

```
error: hello.suru(1,1): 'break' can only appear inside a loop
```

A statement after a `break` never runs. That is not an error — it is simply unreachable, and the
compiler drops it.

### Scope

A loop body is a [block](blocks.md), so it is a scope, and — exactly as with an `if` arm — that
is all that needs saying. A binding made in the body is gone at the `}`:

```suru
let i i64: 0
while i < 2 {
  i: i + 1
  let doubled i64: i * 2
  printLn(doubled)
}
printLn(doubled)
```

```
error: hello.suru(7,9): unknown variable 'doubled'
```

A `let` in the body runs afresh on every pass, so it is re-initialised each time round rather
than carrying a value over from the last one. It may shadow a binding from outside, which comes
back untouched at the `}`:

```suru
let x i64: 1
let n i64: 0
while n < 2 {
  n: n + 1
  let x f64: 1.5
  printLn(x)
}
printLn(x)
```

```
1.5
1.5
1
```

Assignment is the other direction, and is how a loop usually gets anything done: the body stores
into a binding it can see from outside, and the store outlives the loop.

### Whitespace

A `while` condition is an ordinary expression, so it follows the same
[line-continuation rule](program-structure.md) an `if` condition does — it carries on while the
next line starts with a binary operator — and a `{` on the following line still opens the body.

### Testing inside a loop

A [`#` directive](testing.md) in a loop body reports once per pass, and its line holds one
answer. A `#view` shows the **last** value it saw; an `#assert` that ever failed reads `fail`.
Each counts once in the summary, however many times it ran. See
[Testing](testing.md#directives-inside-a-loop).

A loop in a branch that is never taken, or one whose condition is false on arrival, leaves its
directives `undefined`, for the same reason an untaken `if` arm does: nothing reported.

## Not yet supported

`if` and `while` are statements, not expressions: they produce no value, so neither can be bound
or printed, and there is no ternary form of `if`.

There is no `for`, and no iterator form like `while item in items` — there is nothing to iterate
over yet. `while` is the only loop.

`break` and `continue` take no label, so an inner loop cannot leave an outer one in one step; the
usual workaround, a `bool` the outer condition also tests, works. There is no `return`, because
there are no functions to return from.
