# Blocks

A block is statements wrapped in `{}`. It runs them in order, exactly as if the braces
were not there:

```suru
printLn(1)
{
  printLn(2)
  printLn(3)
}
printLn(4)
```

```
1
2
3
4
```

What a block adds is a **scope**. That is also what an [`if`](control-flow.md) takes as a body:
an arm is a block, so everything on this page is true of one. Written on its own, as above, the
scope is the only reason to write it.

## Scope

A binding made inside a block is visible until the closing `}` and no further:

```suru
{
  let count i64: 1
  printLn(count)
}
printLn(count)
```

```
error: hello.suru(5,9): unknown variable 'count'
```

Everything already in scope stays in scope, so a block can read and assign the bindings
around it:

```suru
let count i64: 1
{
  count: count + 1
}
printLn(count)
```

```
2
```

A block does not have to bind anything, and an empty block `{}` is a legal statement that
does nothing.

## Shadowing

Because a block's bindings are its own, `let` may reuse a name that is already bound
further out. The inner binding *shadows* the outer one: for the rest of the block, the
name means the new binding, and the outer one is untouched and comes back at the `}`.

```suru
let x i64: 1
{
  let x f64: 1.5
  printLn(x)
}
printLn(x)
```

```
1.5
1
```

A shadowing binding is a new binding, not an assignment, so it declares its own type —
`x` is an `f64` inside the block and still the original `i64` after it.

Shadowing works between nesting levels, not within one. Two `let`s of the same name in
the same block are still an error:

```suru
{
  let x i64: 1
  let x i64: 2
}
```

```
error: hello.suru(3,3): 'x' is already declared
```

## Nesting

Blocks nest to any depth, each one a scope of its own:

```suru
let x i64: 1
{
  let x i64: 2
  {
    let x i64: 3
    printLn(x)
  }
  printLn(x)
}
printLn(x)
```

```
3
2
1
```

A name that is not shadowed is found by looking outward through the enclosing blocks
until it is bound, however deep the nesting.

## Whitespace

A block is not indentation-sensitive: `{` and `}` are ordinary tokens, and like every
other statement a block needs no separator around it. These are the same program:

```suru
{
  printLn(1)
}
```

```suru
{ printLn(1) }
```

An unterminated block runs into the end of the file:

```suru
{
  printLn(1)
```

```
error: hello.suru(3,1): expected RightBrace, got Eof
```

## Not yet supported

A block is a statement, not an expression — it produces no value, so it cannot appear
where one is expected. The only thing that takes a block as a body is
[`if`](control-flow.md); loops and user-defined functions are still to come.
