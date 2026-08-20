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
[block](blocks.md) — or to the end of the file, for one not inside a block. A name cannot
be used before it is bound:

```suru
printLn(count)
let count i64: 1
```

```
error: hello.suru(1,9): unknown variable 'count'
```

## Errors

The type must be one that exists:

```suru
let count int: 1
```

```
error: hello.suru(1,11): unknown type 'int'
```

`void` is not among them — it is the type of a `printLn` call, not something a program
can write down.

A name can be bound only once in a scope. A binding inside a block may
[shadow](blocks.md#shadowing) one further out, but not one of its own:

```suru
let count i64: 1
let count i64: 2
```

```
error: hello.suru(2,1): 'count' is already declared
```

The value must have the declared type. Nothing converts implicitly, so an `i64` binding
does not accept an `f64`:

```suru
let count i64: 1.5
```

```
error: hello.suru(1,16): cannot bind a value of type 'f64' to 'count' of type 'i64'
```

The same holds when assigning:

```suru
let count i64: 1
count: true
```

```
error: hello.suru(2,8): cannot assign a value of type 'bool' to 'count' of type 'i64'
```

Assigning to a name that was never bound is an error rather than a way to create one:

```suru
count: 1
```

```
error: hello.suru(1,1): unknown variable 'count'
```

## Not yet supported

Every binding is mutable. There is no type inference — a `let` always writes its type out.
