# Control flow

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

`if` is the only control flow there is today: there are no loops, and nothing that leaves a
block early.

## The condition

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

## `else` and `else if`

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

## The body is always a block

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

## Scope

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

## Whitespace

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

## Testing inside a branch

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

## Not yet supported

`if` is a statement, not an expression: it produces no value, so it cannot be bound or printed,
and there is no ternary form of it.

There are no loops, and nothing that leaves a block early — no `return`, `break` or `continue`.
Every arm runs to its `}` and carries on after the `if`.
