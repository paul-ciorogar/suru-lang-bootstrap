# Expressions

An expression computes a value. Literals and variables are expressions; so is an
operator applied to them, and so is a call.

## Operators

| Kind | Operators | Operands | Result |
| --- | --- | --- | --- |
| Arithmetic | `+` `-` `*` `/` `%` | `i64` or `f64` | the operand type |
| Equality | `=` `<>` | `bool`, `i64` or `f64` | `bool` |
| Ordering | `<` `<=` `>` `>=` | `i64` or `f64` | `bool` |
| Logical | `and` `or` | `bool` | `bool` |
| Prefix | `-` `not` | `i64`/`f64`, `bool` | the operand type |

Equality is a single `=`; there is no `==`. Not-equal is `<>`. Assignment uses `:`, so
the two never compete for the same symbol — see [Bindings](bindings.md).

`and`, `or` and `not` are keywords, like `true` and `false`. There are no symbol forms
(`&&`, `||`, `!`).

## There is no operator precedence

Every binary operator has the same standing, and an expression is folded **left to
right**. A Suru expression is a recipe — a list of steps in order — not a formula to be
untangled by a precedence table.

```suru
printLn(1 + 2 * 3)
```

```
9
```

That is `(1 + 2) * 3`. In most languages it would be `7`.

Parentheses are the only way to say something else:

```suru
printLn(1 + (2 * 3))
```

```
7
```

The rule applies to every operator, not just the arithmetic ones. `a = 1 and b = 2`
folds as `((a = 1) and b) = 2`, which is a type error, not a mistake the compiler
guesses its way out of. Write `(a = 1) and (b = 2)`.

A prefix operator is not part of this: it binds to the operand that follows it, so
`-count + 2` is `(-count) + 2`. A `-` in front of a number is not even an operator —
`-1` is the literal −1 (see [Literals and types](literals-and-types.md)).

## Splitting an expression across lines

There is no statement terminator, so the compiler needs some way to know an expression
has finished. The rule is: an expression continues while the next thing is a binary
operator, wherever that operator sits. A line that begins with an operator continues the
line above.

```suru
let sum i64: 1
  + 2
  + 3
printLn(sum)
```

```
6
```

A line that begins with anything else starts a new statement. The consequence worth
knowing: a line starting with `-` continues the previous line rather than beginning a
new statement.

## Types do not convert

Both operands of a binary operator must already have the same type. Nothing is promoted
or widened:

```suru
printLn(1 + 1.5)
```

```
error: hello.suru(1,9): operator '+' cannot be applied to 'i64' and 'f64'
```

Write `1.0 + 1.5`. The same applies to an operator that does not accept the type at all:

```suru
printLn(true < false)
```

```
error: hello.suru(1,9): operator '<' cannot be applied to 'bool' and 'bool'
```

```suru
printLn(not 1)
```

```
error: hello.suru(1,9): operator 'not' cannot be applied to 'i64'
```

## Integer division truncates

`/` on two `i64` values is integer division and yields an `i64`, dropping the remainder
rather than producing a fraction. `%` gives that remainder.

```suru
printLn(7 / 2)
printLn(7 % 2)
printLn(7.0 / 2.0)
```

```
3
1
3.5
```

Dividing an integer by zero is not checked; the program's behaviour is undefined. That
is a gap in the bootstrap compiler, not a decision.

## Evaluation

`and` and `or` evaluate both sides. No expression can have a side effect yet — there are
no user-defined functions — so this is not observable, and short-circuiting will arrive
with the feature that makes it matter.

## Not yet supported

Bitwise operators, shifts, exponentiation, string concatenation, and any operator on a
type other than `bool`, `i64` and `f64`. A comparison is also what an
[`if`](control-flow.md) branches on.
