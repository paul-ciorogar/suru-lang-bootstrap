# Literals and types

Suru is statically typed. Today there are three types you can write a value of — and
name in a binding — plus `void`, which you can only observe in an error message.

| Type | Meaning | Written as |
| --- | --- | --- |
| `bool` | a truth value | `true`, `false` |
| `i64` | a 64-bit signed integer | `0`, `42`, `1000000` |
| `f64` | a 64-bit floating point number | `1.2`, `0.5`, `3.14159265358979` |
| `void` | no value — the result of a `printLn` call | *cannot be written* |

A literal's type is fixed by how it is written: an integer literal is always `i64`, and a
float literal is always `f64`. There is no inference to do and no conversion between the
two — `1` and `1.0` are different types, and neither becomes the other.

## Booleans

`true` and `false` are keywords, not identifiers. They are the only two `bool` values.

```suru
printLn(true)
printLn(false)
```

```
true
false
```

## Integers

An integer literal is one or more decimal digits. There are no separator, base prefix or
suffix forms — `1_000`, `0xff` and `1i64` are all rejected. A literal carries no sign
either: `-1` is the negation operator applied to the literal `1`, which prints the same
but is an expression rather than a literal (see [Expressions](expressions.md)).

```suru
printLn(0)
printLn(1000000)
```

```
0
1000000
```

Every integer literal is `i64`, so the range is −9223372036854775808 to
9223372036854775807. A literal larger than that is a known gap in the bootstrap
compiler: instead of reporting an error it crashes with an unhandled overflow.

## Floats

A float literal is digits, a `.`, then **at least one more digit**. The trailing-dot form
`1.` is not a float — the lexer reads `1` and then rejects the `.`:

```suru
printLn(1.)
```

```
error: hello.suru(1,10): unexpected character '.'
```

Write `1.0` instead. There is no exponent form (`1e9`) and no leading-dot form (`.5`);
write `0.5`.

```suru
printLn(1.2)
printLn(0.5)
```

```
1.2
0.5
```

Floats are printed with six significant digits, so a value carrying more precision than
that is shortened on the way out:

```suru
printLn(3.14159265358979)
```

```
3.14159
```

The stored value is the full `f64`; only the printed form is shortened. See
[Printing](printing.md).

## `void`

`void` is the type of an expression that yields no value. A `printLn` call has type
`void`, which is why one cannot be passed to another:

```suru
printLn(printLn(1))
```

```
error: hello.suru(1,9): 'printLn' cannot print a value of type 'void'; expected 'bool', 'i64', 'f64'
```

There is no way to write a `void` value, and nothing else in the language has that type
yet.

## Not yet supported

Strings and characters, unsigned and narrower integer types (`u64`, `i32`, …), `f32`,
and any form of composite type — structs, arrays, pointers. A type is also never
inferred: a binding always writes its type out.
