# Bindings

A binding gives a name to a value.

```suru
let count i64: 42
printLn(count)
```

```
42
```

A `let` is the keyword, then the name, then the **type**, then `:`, then the expression
whose value the name is bound to. The type is written out — it is not inferred from the
value.

Using a name is just writing it where a value is expected:

```suru
let width i64: 3
let height i64: 4
printLn(width * height)
```

```
12
```

## Assignment

A name followed by `:` stores a new value into an existing binding. There is no `let`,
and no type — the binding already has one.

```suru
let count i64: 1
count: count + 1
printLn(count)
```

```
2
```

The right-hand side is evaluated first, so a binding can be updated in terms of itself,
as above.

`:` is the only assignment form: there is no `+:`, `+=` or `++`.

## Scope

A binding is visible from the line it appears on to the end of the enclosing
[block](blocks.md), and a function's body is a block. A name cannot be used before it is
bound:

```suru
fn main() void {
  printLn(count)
  let count i64: 1
}
```

```
error: hello.suru(2,11): unknown variable 'count'
```

## Constants

A `let` at file level, outside every function, is a **constant**. It is spelled like any
other binding, and every function in the file can read it:

```suru
let rate i64: 12
let verbose bool: false

fn scaled(n i64) i64 {
  return n * rate
}

fn main() void {
  printLn(scaled(2))
  printLn(verbose)
}
```

```
24
false
```

- **Its value is a literal**: `true`, `false`, an integer or a float, a negative one
  included. The compiler does not evaluate anything at compile time, so an operator, a call
  and another constant's name are all rejected:

  ```suru
  let area i64: 3 * 4
  ```

  ```
  error: hello.suru(1,15): a constant's value must be a literal
  ```

  The value must still have the declared type, as with any `let`.
- **Order does not matter.** A constant's value names nothing, so every function sees every
  constant, wherever the `fn` and the `let` are written. A function declared above a constant
  can use it.
- **It cannot be assigned**, and so it cannot be [`#mock`ed](testing.md#mock) either, since a
  mock is an assignment:

  ```suru
  let rate i64: 12

  fn main() void {
    rate: 13
  }
  ```

  ```
  error: hello.suru(4,3): 'rate' is a constant and cannot be assigned
  ```

- **It can be shadowed.** A function's body is a new scope, so a parameter or a `let` in it
  may reuse a constant's name, as a block's `let` may reuse an outer one. At file level, a
  constant and a function cannot share a name.

## Errors

The type must be one that exists:

```suru
fn main() void {
  let count int: 1
}
```

```
error: hello.suru(2,13): unknown type 'int'
```

`void` is not among them — it is the type of a `printLn` call, not something a program
can write down.

A name can be bound only once in a scope. A binding inside a block may
[shadow](blocks.md#shadowing) one further out, but not one of its own:

```suru
fn main() void {
  let count i64: 1
  let count i64: 2
}
```

```
error: hello.suru(3,3): 'count' is already declared
```

The value must have the declared type. Nothing converts implicitly, so an `i64` binding
does not accept an `f64`:

```suru
fn main() void {
  let count i64: 1.5
}
```

```
error: hello.suru(2,18): cannot bind a value of type 'f64' to 'count' of type 'i64'
```

The same holds when assigning:

```suru
fn main() void {
  let count i64: 1
  count: true
}
```

```
error: hello.suru(3,10): cannot assign a value of type 'bool' to 'count' of type 'i64'
```

Assigning to a name that was never bound is an error rather than a way to create one:

```suru
fn main() void {
  count: 1
}
```

```
error: hello.suru(2,3): unknown variable 'count'
```

## Not yet supported

Every binding inside a function is mutable: there is no immutable local, and a constant
cannot be declared inside a function. There is no type inference — a `let` always writes its type out.
