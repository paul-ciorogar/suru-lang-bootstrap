# Printing

`printLn` is the only function in the language. It is a builtin — there is nothing to
import, and it cannot be redefined.

```
printLn(value)
```

It takes **exactly one** argument, which must be a [`bool`, `i64` or
`f64`](literals-and-types.md). It writes that value followed by a newline to standard
output, and yields nothing: a `printLn` call has type `void`.

## Example

```suru
printLn(true)
printLn(false)
printLn(1)
printLn(1.2)
printLn(0.5)
printLn(100)
```

```
true
false
1
1.2
0.5
100
```

## How each type is printed

| Type | Printed as |
| --- | --- |
| `bool` | the words `true` and `false`, lowercase |
| `i64` | decimal digits |
| `f64` | the shortest decimal form carrying six significant digits |

Six significant digits is a hard limit for `f64` today, so extra precision is dropped in
the output even though the value itself keeps it:

```suru
printLn(3.14159265358979)
```

```
3.14159
```

There is no way to change the format, and no unbuffered/flush control — output is what C's
`printf` does with `%s`, `%lld` and `%g`.

## Errors

### Unknown function

`printLn` is the only name that resolves. Anything else — including a near miss — is
rejected. Names are case-sensitive.

```suru
print(1)
```

```
error: hello.suru(1,1): unknown function 'print'
```

### Wrong number of arguments

The parser accepts any number of arguments; the check happens during semantic analysis, so
the error points at the call rather than at an argument.

```suru
printLn(1, 2)
```

```
error: hello.suru(1,1): 'printLn' expects 1 argument, got 2
```

```suru
printLn()
```

```
error: hello.suru(1,1): 'printLn' expects 1 argument, got 0
```

### Unprintable argument type

Only the three value types can be printed. The error points at the argument, not the call:

```suru
printLn(printLn(1))
```

```
error: hello.suru(1,9): 'printLn' cannot print a value of type 'void'; expected 'bool', 'i64', 'f64'
```

## Not yet supported

Printing without a trailing newline, printing more than one value in a call, format
strings, writing to standard error, and printing anything other than the three value
types.
