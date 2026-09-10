# Unsafe code and interoperability

Unsafe and interop features are for working with native APIs, pointers, and
storage that cannot be expressed with ordinary managed values. Keeping them in
an explicit unsafe context makes that boundary visible.

The `*Type` form declares a native pointer to `Type`. Pointer types are
distinct from by-reference types but interoperate with address-of
expressions: taking the address of a local or field produces an address
handle that implicitly converts to the matching pointer type.

Pointer declarations and pointer-type usage require unsafe mode. Enable
unsafe mode with the compiler's `--unsafe` option; otherwise pointer type
syntax is rejected.

Unsafe context can be enabled either globally (compiler option `--unsafe`)
or locally with `unsafe` contexts:

* `unsafe func Name(...) { ... }` for function/method scope
* `unsafe { ... }` as a statement for block scope
* `unsafe { ... }` as an expression for a value-producing block scope

An unsafe block expression has the same value and type as its inner block
expression. It only enables unsafe operations while binding that block; code
before and after the expression remains in the surrounding unsafe context.

```raven
let value = 42
let pointer: *int = &value
```

## Pointer operations

Pointer operations read, write, or move through native memory directly.

Pointer operations are also gated by unsafe mode. In unsafe mode:

* `*ptr` dereferences a pointer (or by-reference value) and reads the pointed value.
* `*ptr = value` writes through the pointer.
* `ptr->Member` accesses a member on the pointed-at type and is equivalent to
  `(*ptr).Member`, while preserving direct storage semantics (no implicit struct copy for
  instance member calls/field writes).
* `ptr + n` / `n + ptr` advances a pointer by `n` elements.
* `ptr - n` rewinds a pointer by `n` elements.
* `ptr1 - ptr2` returns the element-distance as `nint` when both pointers share the same element type.
  Arithmetic offsets are scaled by `sizeof(element-type)`.

```raven
var value = 41
let pointer: *int = &value

*pointer = 42
let result = *pointer // 42
```

```raven
struct Holder {
    field Foo: int = 0
}

unsafe func assignThroughPointer() -> int {
    var holder = Holder()
    let pointer: *Holder = &holder
    pointer->Foo = 2
    holder.Foo // 2
}
```

## Stack allocation

`stackalloc T[count]` reserves contiguous storage for `count` values of `T` in
the current stack frame. Without another target it produces `System.Span<T>`;
an explicit `System.ReadOnlySpan<T>` target produces a read-only view. An
explicit `*T` target selects native-pointer behavior.

```raven
func sumFour() -> int {
    let values = stackalloc int[4]
    values[0] = 10
    values[1] = 20
    values[2] = 30
    values[3] = 40
    values[0] + values[1] + values[2] + values[3]
}

unsafe func pointerForm() -> int {
    let values: *int = stackalloc int[1]
    *values = 42
    *values
}
```

Rules:

* The element type must be unmanaged: it cannot contain managed references.
* The count must be implicitly convertible to `int` and may be computed at
  runtime.
* A negative constant count is rejected during compilation.
* `Span<T>` and `ReadOnlySpan<T>` targets are available in safe code.
* An explicit pointer target requires an unsafe context.
* The resulting storage is valid only while the declaring stack frame remains
  active and must not be returned or otherwise allowed to escape. This applies
  both to the allocation result itself and to pointer, `Span<T>`, or
  `ReadOnlySpan<T>` locals and aliases backed by that allocation. Other span
  values, such as parameters and spans backed by managed arrays, may be
  returned.

## Pinning managed storage

Pinning keeps managed storage at a stable address while native code or a pointer
uses it.

Raven uses a `use`-scoped pinning form instead of a dedicated `fixed (...) { ... }`
statement. The initializer syntax is:

```raven
unsafe {
    use pointer: *int = fixed &value
}
```

Rules:

* `fixed` is only valid as the initializer of a `use` declaration.
* `fixed` requires an explicit address-of operand written as `&expr`.
* The result type is a native pointer `*T`, where `T` is the addressed storage type.
* The pin lasts for the lifetime of the enclosing `use` scope and is released when that scope exits, including exceptional exits.
* `fixed` is gated by unsafe mode just like other pointer-producing operations.

The explicit `&` keeps address selection separate from pinning: `&expr` identifies the storage, while `fixed` guarantees that storage remains stable for pointer use within the `use` scope.

```raven
unsafe func writeSecond(values: int[]) -> int {
    use pointer = fixed &values[1]
    *pointer = 42
    values[1]
}
```

## Extern declarations

Use an `extern` declaration for a function whose implementation is supplied
outside Raven, such as a native library entry point.

Methods and function statements can be marked `extern` to declare that their
implementation is provided externally (for example via P/Invoke). Extern
declarations do not include a body.

Rules:

* Type members marked `extern` must also be `static`.
* `extern` members/functions cannot declare a block body or expression body.
* P/Invoke declarations are expressed with `[DllImport(...)]` on static extern methods.

```raven
import System.Runtime.InteropServices.*

class Native {
    [DllImport("kernel32", EntryPoint: "GetTickCount")]
    extern static GetTickCount() -> uint;
}
```

## Pass by reference

Passing or returning a value by reference lets code work with the caller's
existing storage instead of a copy.

By-reference types can annotate locals and return values. A local
declared with `&Type` acts as an alias to the underlying storage, so
assignments flow through to the referenced location. Functions may
return by-reference values to expose existing storage to the caller. If
you plan to reassign the alias, declare it with `var` so the reference
itself remains mutable.

Taking the address of a value with `&expression` implicitly produces a
by-reference type when the target binding has no explicit annotation.
Use a pointer annotation to force the result into a native pointer
instead of a managed alias.

```raven
func headSlot(values: int[]) -> &int {
    return &values[0]
}

var numbers: int[] = [10, 20, 30]
var slot = headSlot(numbers)
slot = 42 // numbers[0] is now 42

let value = 0
let alias = &value      // alias : &int
let raw: *int = &value  // raw : *int
```

By-reference returns must point to storage that outlives the callee. Returning
`&local` or `&valueParameter` is rejected because those addresses become invalid
after the function returns.

## `ref`/`out` arguments

Parameters can also be declared with an explicit by-reference type using
`&Type`. That form is valid when the type itself is intentionally part of
the signature. Separately, `ref`, `out`, and `in` declare by-reference
parameter passing at the call boundary and already imply aliasing, so
their parameter types are written as plain `Type`, not `&Type`. For
example, write `ref value: int`, not `ref value: &int`. When a
by-reference parameter is passed **into** a function, it behaves just
like a by-reference local: the callee receives an alias to the caller's
storage and can both read and write through that reference. Reading a parameter
in a value expression loads the stored value; passing it onward by reference
preserves the original storage location. These rules also apply to generic
by-reference parameters. To mark a
parameter that must be assigned by the callee before returning, place
`out` before the parameter name. `ref` and `out` parameters are writable
aliases; `in` parameters are read-only. At call sites, pass the argument with
the address operator `&`. The argument must refer to assignable storage.

By-reference locals and fields never use the `out` modifier—`out` is
only meaningful at the call boundary to signal definite assignment
responsibilities between caller and callee. Declaring a local with
`ref`, `out`, and `in` parameters immediately alias existing storage
locations. The caller provides that storage with `&expr`; `out`
requires the callee to assign the aliased storage before returning.

Every local declaration has an initializer, so local initialization is checked
at the declaration itself. An `out` parameter instead must be assigned on every
normal exit from its callable. Exhaustive `if` and `match` branches join their
assignment state. A branch that throws, or a callable that cannot complete
because it remains in a non-terminating loop, has no normal exit and therefore
does not need to assign the parameter.

```raven
func TryParse(text: string, out result: int) -> bool { /* ... */ }

var total = 0
if !TryParse(arg, out total) {
    Console.WriteLine("Expected number")
}
```
