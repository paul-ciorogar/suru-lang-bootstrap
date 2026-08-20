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

An integer literal is one or more digits, written in decimal or behind a base prefix:

| Base | Prefix | Example |
| --- | --- | --- |
| decimal | *none* | `1000000` |
| hexadecimal | `0x` | `0xff` |
| binary | `0b` | `0b1010` |
| octal | `0o` | `0o755` |

The prefix is lowercase — `0X`, `0B` and `0O` are errors, so a literal has one spelling and `0o`
never has to be told apart from `00`. The hexadecimal *digits* may be written in either case:
`0xff` and `0xFF` are the same literal.

Digits can be grouped with `_`, which the compiler ignores. A `_` must sit **between** two
digits, so it can neither open nor close a literal nor follow another `_` — `1_`, `1__0`
and `0x_ff` are all errors.

```suru
printLn(0)
printLn(1_000_000)
printLn(0xff)
printLn(0b1010)
printLn(0o755)
```

```
0
1000000
255
10
493
```

There is no suffix form: `1i64` is rejected, and so is anything else butted up against the
end of a literal.

```suru
printLn(1i64)
```

```
error: hello.suru(1,10): invalid digit 'i' in decimal literal
```

A `-` written directly in front of a number is part of the literal, not an operator applied
to it: `-1` is the literal −1. This holds only in front of a number and only where an
operand is expected — the `-` of `1 - 2` is still subtraction, and `-count` is still the
negation operator. Written with a space, `- 1` is the same literal: the sign attaches to the
number, not to the spacing.

Every integer literal is `i64`, so the range is −9223372036854775808 to
9223372036854775807 whatever base it is written in. The lower bound is reachable only with
its sign — 9223372036854775808 on its own is out of range.

```suru
printLn(-9223372036854775808)
```

```
-9223372036854775808
```

```suru
printLn(9223372036854775808)
```

```
error: hello.suru(1,9): integer literal is out of range for 'i64'
```

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
write `0.5`. A float takes `_` separators on either side of the `.` under the same rule as
an integer — `1_000.000_1` — but no base prefix: a float is always decimal. A leading `-`
folds into a float the same way it folds into an integer, so `-1.5` is one literal.

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
