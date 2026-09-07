# R&D: Arrays of Plain Data over the C FFI

Suru is library-driven with no garbage collector, so a growable array must be an
ordinary library type built on the platform allocator — not a compiler built-in.
This note surveys how three no-GC-friendly languages express exactly that shape
(`malloc` / `realloc` / `free` behind a `ptr` + `len` + `cap` struct), and what
each one needs from its FFI to make it work.

The element type throughout is a plain data struct:

```c
struct Point { double x, y, z; };   // 24 bytes, no pointers, trivially copyable
```

This is deliberate. The array stores **the points themselves, laid out
contiguously** — `[x y z][x y z][x y z]…` in one `malloc` block — not pointers
to points. One allocation holds the whole array, growing it is one `realloc`,
and there is nothing per-element to free. That is the case Suru needs to get
right first, and it is the case where the three languages differ least in
capability and most in ceremony.

The three chosen are Rust, Zig, and Go — Rust and Zig because they are the
closest neighbours to what Suru wants to be, and Go because it shows what the
same code costs when a GC is in the way.

Each example implements the same thing: a growable array of `Point` with `push`,
indexed access, a borrowed view of the live elements, and explicit release.

---

## 1. Rust

Rust declares the C entry points in an `extern` block and wraps the raw pointer
in a type whose `Drop` impl owns the deallocation.

```rust
use std::ffi::c_void;
use std::mem::size_of;
use std::ptr::{self, NonNull};

unsafe extern "C" {
    fn malloc(size: usize) -> *mut c_void;
    fn realloc(ptr: *mut c_void, size: usize) -> *mut c_void;
    fn free(ptr: *mut c_void);
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Point {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

pub struct PointArray {
    ptr: NonNull<Point>,
    len: usize,
    cap: usize,
}

impl PointArray {
    pub fn new() -> Self {
        // No allocation until the first push.
        PointArray { ptr: NonNull::dangling(), len: 0, cap: 0 }
    }

    pub fn push(&mut self, p: Point) {
        if self.len == self.cap {
            self.grow();
        }
        // Writes 24 bytes into the block; does not read the old contents.
        unsafe { ptr::write(self.ptr.as_ptr().add(self.len), p) };
        self.len += 1;
    }

    pub fn get(&self, i: usize) -> Option<Point> {
        (i < self.len).then(|| unsafe { *self.ptr.as_ptr().add(i) })
    }

    pub fn as_slice(&self) -> &[Point] {
        unsafe { std::slice::from_raw_parts(self.ptr.as_ptr(), self.len) }
    }

    fn grow(&mut self) {
        let new_cap = if self.cap == 0 { 4 } else { self.cap * 2 };
        let bytes = new_cap
            .checked_mul(size_of::<Point>())   // 24 bytes each
            .expect("capacity overflow");

        let raw = unsafe {
            if self.cap == 0 {
                malloc(bytes)
            } else {
                // Moves all existing points to the new block for us.
                realloc(self.ptr.as_ptr().cast(), bytes)
            }
        };

        self.ptr = NonNull::new(raw.cast()).expect("allocation failed");
        self.cap = new_cap;
    }
}

impl Drop for PointArray {
    fn drop(&mut self) {
        // Point owns nothing, so there is no per-element cleanup —
        // one free releases every point at once.
        if self.cap != 0 {
            unsafe { free(self.ptr.as_ptr().cast()) };
        }
    }
}
```

```rust
let mut pts = PointArray::new();
pts.push(Point { x: 1.0, y: 2.0, z: 3.0 });
pts.push(Point { x: 4.0, y: 5.0, z: 6.0 });
println!("{:?}", pts.as_slice());
// dropped here: one call to free
```

What the language has to provide:

- **A layout guarantee.** `#[repr(C)]` pins field order and padding so that
  `size_of::<Point>() == 24` and the block really is `x y z x y z …`. Without it
  the compiler may reorder fields — fine internally, not across an FFI.
- **A raw pointer type that is not a reference.** `*mut Point` can be null,
  unaligned and aliased; `&mut Point` cannot. The array holds the former and
  only manufactures the latter at the boundary (`get`, `as_slice`).
- **Uninitialised memory as a first-class idea.** `malloc` hands back bytes, not
  values, so the slot at `len` is garbage until written. `ptr::write` writes
  without dropping whatever was there.
- **Pointer arithmetic in units of the element.** `.add(i)` advances by
  `i * 24` bytes, which is what makes `ptr[i]` an address computation rather
  than a lookup.
- **A destructor hook.** `Drop` is what makes this usable without a GC:
  ownership is static, and the `free` happens at the end of the binding's scope.

Caveats worth carrying forward: `malloc` only guarantees `max_align_t`
alignment, which covers `Point`'s 8-byte requirement but would not cover an
over-aligned (e.g. SIMD `#[repr(align(32))]`) struct — that needs
`aligned_alloc`. And `realloc` returning null does *not* free the old block, so
the old pointer must not be overwritten before the null check.

Note what `Point` being plain data buys: the `Drop` impl is three lines. If the
element owned a heap string, `Drop` would have to walk all `len` elements
calling `drop_in_place` before freeing the block — and `realloc` would still be
legal only because relocating 24 bytes is a bit-copy.

---

## 2. Zig

Zig imports C symbols with `extern "c"` (or `@cImport` of `<stdlib.h>`). Since
the element type is fixed here, no `comptime` generics are needed.

```zig
const std = @import("std");

extern "c" fn malloc(size: usize) ?*anyopaque;
extern "c" fn realloc(ptr: ?*anyopaque, size: usize) ?*anyopaque;
extern "c" fn free(ptr: ?*anyopaque) void;

pub const Point = extern struct {
    x: f64,
    y: f64,
    z: f64,
};

pub const PointArray = struct {
    ptr: [*]Point = undefined,
    len: usize = 0,
    cap: usize = 0,

    pub fn deinit(self: *PointArray) void {
        // Point owns nothing: one free releases the whole block.
        if (self.cap != 0) free(@ptrCast(self.ptr));
        self.* = .{};
    }

    pub fn push(self: *PointArray, p: Point) !void {
        if (self.len == self.cap) try self.grow();
        self.ptr[self.len] = p;   // 24-byte store at ptr + len*24
        self.len += 1;
    }

    pub fn get(self: PointArray, i: usize) ?Point {
        return if (i < self.len) self.ptr[i] else null;
    }

    pub fn slice(self: PointArray) []Point {
        return self.ptr[0..self.len];
    }

    fn grow(self: *PointArray) !void {
        const new_cap = if (self.cap == 0) 4 else self.cap * 2;
        const bytes = try std.math.mul(usize, new_cap, @sizeOf(Point));

        const raw = if (self.cap == 0)
            malloc(bytes)
        else
            realloc(@ptrCast(self.ptr), bytes);

        self.ptr = @ptrCast(@alignCast(raw orelse return error.OutOfMemory));
        self.cap = new_cap;
    }
};
```

Usage is explicit about the release, because Zig has no destructors:

```zig
var pts = PointArray{};
defer pts.deinit();

try pts.push(.{ .x = 1, .y = 2, .z = 3 });
try pts.push(.{ .x = 4, .y = 5, .z = 6 });

for (pts.slice()) |p| std.debug.print("{d} {d} {d}\n", .{ p.x, p.y, p.z });
```

What the language has to provide:

- **`extern struct` for the layout.** Same role as Rust's `#[repr(C)]`: C ABI
  field order, `@sizeOf(Point) == 24`.
- **Distinct pointer kinds.** `*Point` (one), `[*]Point` (many, unknown length),
  `[]Point` (many, known length) are different types. The array stores
  `[*]Point` and hands out `[]Point` — the slice *is* the `ptr` + `len` pair, so
  the safe view costs nothing and `for (pts.slice()) |p|` walks the block
  directly.
- **`defer` instead of destructors.** Cleanup is a statement at the allocation
  site, not a property of the type. Much simpler to implement; much easier to
  forget.
- **Allocation failure as an ordinary error value.** `error.OutOfMemory` flows
  through `try`, so `push` is fallible in its signature.

Zig's standard library already exposes the platform allocator as
`std.heap.c_allocator`, and `std.ArrayList(Point)` over it is the idiomatic
version of the code above — with the allocator passed to the container as a
runtime *value* instead of a hardcoded global. That indirection is the single
most transferable idea in this document.

---

## 3. Go

Go can do this via cgo, and the exercise mostly demonstrates the friction of
manual memory in a GC'd language. It works here precisely *because* `Point` is
pointer-free.

```go
package pointarray

/*
#include <stdlib.h>
*/
import "C"

import "unsafe"

// Point is pointer-free, so it is legal to store in C memory.
type Point struct {
	X, Y, Z float64
}

const elemSize = C.size_t(unsafe.Sizeof(Point{})) // 24

// Array is a growable []Point backed by the C heap.
// The GC does not see this storage; Free must be called.
type Array struct {
	ptr unsafe.Pointer
	len int
	cap int
}

func (a *Array) Push(p Point) {
	if a.len == a.cap {
		a.grow()
	}
	unsafe.Slice((*Point)(a.ptr), a.cap)[a.len] = p
	a.len++
}

func (a *Array) Get(i int) (Point, bool) {
	if i < 0 || i >= a.len {
		return Point{}, false
	}
	return unsafe.Slice((*Point)(a.ptr), a.len)[i], true
}

// Slice aliases the C block; it is invalid after the next Push or Free.
func (a *Array) Slice() []Point {
	if a.len == 0 {
		return nil
	}
	return unsafe.Slice((*Point)(a.ptr), a.len)
}

func (a *Array) Free() {
	if a.ptr != nil {
		C.free(a.ptr)
	}
	a.ptr, a.len, a.cap = nil, 0, 0
}

func (a *Array) grow() {
	newCap := 4
	if a.cap != 0 {
		newCap = a.cap * 2
	}
	bytes := C.size_t(newCap) * elemSize

	var raw unsafe.Pointer
	if a.ptr == nil {
		raw = C.malloc(bytes)
	} else {
		raw = C.realloc(a.ptr, bytes)
	}
	if raw == nil {
		panic("pointarray: out of memory")
	}
	a.ptr, a.cap = raw, newCap
}
```

What this costs:

- **Two heaps that must not point at each other.** The cgo pointer-passing rules
  forbid storing a Go pointer inside C memory. `Point` is three `float64`s, so
  it is fine; add a `Name string` field and this code becomes illegal — the
  array would have to hold C strings instead.
- **No generics across the FFI boundary.** cgo types (`C.size_t`, `C.malloc`)
  cannot appear in generic code, so `Array[T]` is unavailable; the element type
  is monomorphised by hand or code-generated.
- **No reliable destructor.** `runtime.SetFinalizer` is not guaranteed to run,
  so `Free` stays manual and `defer a.Free()` is a discipline, not a guarantee.
- **Every call is a boundary crossing.** cgo calls cost tens of nanoseconds,
  which is why `grow` — not `Push` — is the only place that touches C.
- **The struct's layout is not guaranteed.** Go says nothing formally about
  padding, so `unsafe.Sizeof` is the only honest source of the element stride.

The interesting part for Suru is the negative result: a GC does not remove the
need for a contiguous array of plain structs, it just makes it awkward to write
and impossible to make generic.

---

## Comparison

| | Rust | Zig | Go |
|---|---|---|---|
| FFI declaration | `unsafe extern "C" { fn malloc(...) }` | `extern "c" fn malloc(...)` | `import "C"` + `#include <stdlib.h>` |
| Layout guarantee | `#[repr(C)]` | `extern struct` | none (rely on `unsafe.Sizeof`) |
| Element stride | `size_of::<Point>()` | `@sizeOf(Point)` | `unsafe.Sizeof(Point{})` |
| Safe view type | `&[Point]` | `[]Point` | `[]Point` via `unsafe.Slice` |
| Generic over element | yes, monomorphised | yes, via `comptime` | no — cgo types are not generic |
| Release | `Drop` (automatic, scope-based) | `defer deinit()` (manual) | `Free()` (manual, no finalizer) |
| Alloc failure | panic, or `Layout` + result | `error.OutOfMemory` value | nil check + panic |
| Per-element cleanup | none needed for plain data | none needed for plain data | n/a (only plain data allowed) |
| Allocator choice | global, or `Allocator` trait | value passed to the container | fixed (the C heap) |

---

## Implications for Suru

Assuming "minimalist, library-driven, data oriented, structural typing, no GC",
the shape that falls out of the three:

1. **`Array` must be a library type**, roughly `{ ptr: *T, len: usize, cap:
   usize }` with a `[]T`-equivalent view. The compiler's job is to make that
   struct expressible and give it decent ergonomics (indexing, iteration), not
   to know what an array is.
2. **Value semantics for structs are the prerequisite.** `Point` must be stored
   *inline* — assigning a `Point` copies 24 bytes, an array of 1000 points is
   one 24 KB block, and iteration is a linear walk. A language where structs are
   implicitly boxed cannot express this array at all. For a data-oriented
   language that is the whole ballgame.
3. **The minimum FFI surface** is: `extern` declarations with the C calling
   convention, a raw pointer type distinct from any safe reference,
   element-sized pointer arithmetic (or an indexing primitive), `usize`, and a
   C-compatible struct layout with a `sizeOf`. That is the actual prerequisite
   work before `Array` can be written in Suru itself.
4. **Follow Zig on allocators, not Rust.** An allocator passed as a value —
   rather than a global `malloc` baked into the container — keeps the FFI
   surface to a single `malloc`/`realloc`/`free` wrapper and makes arenas and
   testing allocators possible later. It also fits structural typing neatly: "an
   allocator" is any value with `alloc`/`resize`/`free` members, no nominal
   interface declaration required.
5. **Decide destruction early.** `Drop` and `defer` are the two viable answers
   without a GC, and the choice leaks into every library type. `defer` is far
   cheaper to implement in a bootstrap compiler; scope-based destructors are
   much harder to retrofit than to design in. For plain-data elements the
   difference barely shows — it only bites once an element owns memory itself.
6. **Uninitialised memory needs a story.** `cap > len` means the tail of the
   block legally contains garbage `Point`s. Whatever the type system says about
   initialisation must have an escape hatch for exactly this, or the array
   cannot be written in the language itself.
7. **Fallible allocation is a language-design question, not a library one.**
   Zig's `!T` makes `push` visibly fallible; Rust's default is to abort. This
   decides whether Suru needs an error-union type before it needs an array.

### Suggested ordering for the bootstrap compiler

structs with value semantics and a known layout → `usize` and pointer types →
`extern` declarations and C calls → element-sized pointer indexing → a concrete
`PointArray` as a proof of concept → generics → `defer` → allocator interface →
generic `Array`.
