# Functions

`fn` declares a function. It names its parameters and their types, and always names the type it
returns:

```suru
fn area(width i64, height i64) i64 {
  return width * height
}

fn greet() void {
  printLn(true)
}

fn main() void {
  greet()
  printLn(area(3, 4))
}
```

```
true
12
```

A function's body is a [block](blocks.md). Calling the function runs the body with each parameter
bound to its argument. A program starts at the function named `main`, which is the one function
that cannot be called; see [Program structure](program-structure.md#main).

## Declaring

```
fn name(param type, param type) returnType { body }
```

- **The return type is always written**, and that includes `void` for a function that returns
  nothing. `fn f() { }` is a parse error, not a `void` function:

  ```
  error: hello.suru(1,8): expected Identifier, got LeftBrace
  ```

- **Every parameter has a type.** A parameter may be `bool`, `i64` or `f64`, but not `void`,
  because nothing can be bound to it:

  ```suru
  fn f(a void) void { }
  ```

  ```
  error: hello.suru(1,8): nothing can be bound to 'void'
  ```

- Zero parameters is fine: `fn greet() void`. Parameters are separated by commas, and a trailing
  comma is not allowed.
- Two parameters cannot share a name:

  ```suru
  fn f(a i64, a i64) i64 {
    return a
  }
  ```

  ```
  error: hello.suru(1,13): 'a' is already declared
  ```

## Calling

A call is the name and its arguments in parentheses. Arguments are evaluated left to right, and
each must already have its parameter's type, since nothing converts. The compiler checks the
number of arguments first and then each one's type:

```suru
fn f(a i64, b i64) i64 {
  return a
}
fn main() void {
  f(1)
  f(1, 2.0)
}
```

```
error: hello.suru(5,3): 'f' expects 2 arguments, got 1
error: hello.suru(6,8): argument 2 of 'f' is of type 'f64'; expected 'i64'
```

A call is an expression, and it may appear wherever a value of its return type may. It may also
stand alone as a statement, in which case any result is discarded:

```suru
fn tick(n i64) i64 {
  printLn(n)
  return n + 1
}
fn main() void {
  tick(7)
}
```

```
7
```

A `void` function returns nothing, so a call to one cannot be used as a value. It cannot be bound,
printed, added to or branched on, and each of those reports the error it already reports for
`void`:

```suru
fn v() void { }
fn main() void {
  let z i64: v()
}
```

```
error: hello.suru(3,14): cannot bind a value of type 'void' to 'z' of type 'i64'
```

## `return`

`return` leaves the function. In a function that returns a value, it carries one of the declared
type:

```suru
fn h() i64 {
  return 1.5
}
```

```
error: hello.suru(2,10): cannot return a value of type 'f64' from 'h' of type 'i64'
```

In a `void` function it carries nothing. A `void` function may also simply run off the end of its
body, which returns as well.

```suru
fn shout(v i64) void {
  printLn(v)
  return
  printLn(v + 1)
}
fn main() void {
  shout(5)
}
```

```
5
```

Code after a `return` is unreachable. It is still checked, but it never runs.

Writing the wrong form of `return` is an error:

```suru
fn v() void {
  return 1
}
fn g() i64 {
  return
}
```

```
error: hello.suru(2,3): 'v' returns 'void'; 'return' cannot carry a value
error: hello.suru(5,3): 'g' must return a value of type 'i64'
```

A `return` at file level is code outside a function, which
[a file cannot hold](program-structure.md#what-a-file-may-hold).

### The value starts on the `return`'s line

Since nothing ends a statement, the compiler needs a way to tell `return` followed by a value
apart from `return` followed by the next statement. **The value belongs to the `return` only if it
starts on the same line.** This is the one place in the language where a newline matters.

```suru
fn next(n i64) i64 {
  return
    n + 1
}
```

```
error: hello.suru(2,3): 'next' must return a value of type 'i64'
error: hello.suru(3,5): only call expressions are allowed as statements
```

Once the value has started, the usual [line-continuation rule](expressions.md#splitting-an-expression-across-lines)
applies, so an operator at the start of the next line carries it on:

```suru
fn sum(a i64, b i64) i64 {
  return a
    + b
}
fn main() void {
  printLn(sum(1, 2))
}
```

```
3
```

Without the rule, a bare `return` in a `void` function followed by `printLn(1)` would silently
become `return printLn(1)`.

### Every path must return

A function with a return type other than `void` must return on every path through its body, and
the compiler checks this before running anything:

```suru
fn k(n i64) i64 {
  if n > 0 { return 1 }
}
```

```
error: hello.suru(1,1): 'k' must return a value of type 'i64' on every path
```

The rule is structural:

- A block returns if **any** statement in it returns. The rest of the block is unreachable.
- An `if` returns only if it has an `else` **and** both arms return. An `else if` chain counts
  when every arm in it returns, including the last `else`.
- A `while` **never** counts, because its body might not run.

That last rule has one false positive. The compiler does not evaluate conditions, so it does not
know that `while true` never ends:

```suru
fn f() i64 {
  while true {
    return 1
  }
}
```

```
error: hello.suru(1,1): 'f' must return a value of type 'i64' on every path
```

The workaround is a `return` after the loop, which never runs:

```suru
fn firstOver(limit i64) i64 {
  let i i64: 0
  while true {
    i: i + 1
    if i * i > limit { return i }
  }
  return 0
}
fn main() void {
  printLn(firstOver(50))
}
```

```
8
```

## Recursion and order

A function may call itself, and two functions may call each other, with one exception: `main`
cannot be called, from anywhere. A function may also be called before the point in the file where
it is declared. All the functions in a block are known before any statement in that block runs:

```suru
fn isEven(n i64) bool {
  if n = 0 { return true }
  return isOdd(n - 1)
}
fn isOdd(n i64) bool {
  if n = 0 { return false }
  return isEven(n - 1)
}
fn main() void {
  printLn(isEven(10))
  printLn(later())
}
fn later() i64 {
  return 42
}
```

```
true
42
```

## Scope

**A body sees its own parameters and locals, the functions declared around it, and the file's
[constants](bindings.md#constants). It sees no other variables.** Functions do not capture
anything, so a body cannot read a local of the function that calls it, or of the function it is
nested in:

```suru
fn f() i64 {
  return x
}
fn main() void {
  let x i64: 1
  printLn(f())
}
```

```
error: hello.suru(2,10): unknown variable 'x'
```

A function that needs a value takes it as a parameter. A value that never changes can be a
constant instead, which every function sees, whichever order the `let` and the `fn` are written
in:

```suru
fn f() i64 {
  return x
}
fn main() void {
  printLn(f())
}
let x i64: 1
```

```
1
```

### Parameters

A parameter is a variable like any `let`. The body can read it and assign it, and an assignment
changes only the function's own copy:

```suru
fn countdown(n i64) void {
  while n > 0 {
    printLn(n)
    n: n - 1
  }
}
fn main() void {
  let n i64: 3
  countdown(n)
  printLn(n)
}
```

```
3
2
1
3
```

Parameters live in the same scope as the body's top-level bindings, so a `let` cannot shadow a
parameter. Both of them may shadow a constant, since the body is a scope of its own:

```suru
fn f(a i64) i64 {
  let a i64: 2
  return a
}
```

```
error: hello.suru(2,3): 'a' is already declared
```

A block nested inside the body can still shadow a parameter, as it can [any binding](blocks.md#shadowing).

### Nested functions

A function may be declared inside another function's body, and inside a bare `{ }` block. Its
name is visible only within the body or block where it is declared:

```suru
fn outer(n i64) i64 {
  fn double(a i64) i64 {
    return a * 2
  }
  return double(n) + 1
}
fn main() void {
  printLn(outer(5))
  printLn(double(5))
}
```

```
error: hello.suru(9,11): unknown function 'double'
```

A nested function can call the function it sits in, and any other function in scope. It can
**not** see the enclosing function's parameters or locals, because the scope rule above applies at
every level.

A function **cannot** be declared inside an `if` arm or a `while` body, because those may run
once, many times or not at all:

```suru
fn main() void {
  if true {
    fn f() void { }
  }
}
```

```
error: hello.suru(3,5): a function cannot be declared inside an 'if' or a 'while'
```

### One namespace

Functions, variables and constants share names. A `let` and an `fn` with the same name collide,
and the error is reported at the `let` regardless of which one is written first, because a
block's functions are known before its statements, and a file's functions before its constants:

```suru
let f i64: 1
fn f() void { }
```

```
error: hello.suru(1,1): 'f' is already declared
```

Using a name as the other kind of thing has an error of its own:

```suru
fn f() i64 { return 1 }
fn main() void {
  let y i64: 2
  let x i64: f
  y(1)
}
```

```
error: hello.suru(4,14): 'f' is a function and cannot be used as a value
error: hello.suru(5,3): 'y' is not a function
```

`printLn` is a [builtin](printing.md) and its name is taken. So is `main`, which may only name
the [entry point](program-structure.md#main):

```suru
fn printLn(a i64) void { }
```

```
error: hello.suru(1,1): 'printLn' is a builtin and cannot be redeclared
```

### `break` and `continue`

A loop outside the function is not a loop inside it. A `break` in a body that is called from a
loop does not leave that loop. It is an error:

```suru
fn f() void {
  break
}
fn main() void {
  while true {
    f()
  }
}
```

```
error: hello.suru(2,3): 'break' can only appear inside a loop
```

Inside a body, `break` and `continue` work in the body's own loops as usual.

## Testing inside a function

[`#` directives](testing.md) can only appear in a function body. They follow the rules a
[loop](testing.md#directives-inside-a-loop) already set, because a body runs once per call:

- A `#view` in a function that is called three times shows the value from the **last** call.
- An `#assert` that failed on any call reads `fail`, with the value it got the first time it
  failed.
- A directive in a function that is never called, or one placed after a `return`, reads
  `undefined`.
- A `#mock` on a parameter replaces the argument, whatever the caller passed:

```suru
fn echo(n i64) i64 {
  #mock n: 7
  #view n: 7
  return n
}
fn main() void {
  printLn(echo(1))
}
```

## Not yet supported

Functions are not values. A function cannot be bound, passed or returned, and there is no
syntax for a function type. Closures, default and named arguments, overloading, generic
parameters and returning more than one value are all missing too.

A non-`void` call whose result is discarded is accepted silently. Once a `_` binding exists, the
intent is to require it, so that dropping a result is always written on purpose.
