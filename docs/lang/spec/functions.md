# Functions

Functions define reusable operations. They can accept parameters, return values,
and be declared at namespace scope, as members of types, or locally inside
another function.

Functions are also first-class values in Raven. They can be assigned to
variables, passed to other functions, returned as values, and converted to
compatible .NET delegate types.

```raven
func add(a: int, b: int) -> int {
    a + b
}

let result = add(2, 3)
```

A named `func` declaration may be:

* a **top-level function** at namespace scope
* a **method** when declared as a member of a type
* a **local function** when declared inside another body

Top-level functions are implicitly static. Methods follow the usual instance and
static member rules.

See [Top-level code and entry points](top-level-code-and-entry-points.md) for
namespace-level code and entry points, [Classes and members](classes-and-members.md)
for method-specific member rules, and [Async functions](async-functions.md) for
asynchronous functions.

## Function bodies and return values

Functions can use a block body:

```raven
func add(a: int, b: int) -> int {
    a + b
}
```

The final expression of the block provides the function's result. An explicit
`return` can be used when returning earlier:

```raven
func absolute(value: int) -> int {
    if value < 0 {
        return -value
    }

    value
}
```

Functions may also use an expression body with `=>`:

```raven
func add(a: int, b: int) -> int => a + b
```

The return type is written after `->`. Returned expressions must be convertible
to the declared return type.

## Parameters

Parameters normally use the `name: Type` syntax:

```raven
func greet(name: string) {
    Console.WriteLine("Hello, ${name}")
}
```

When an implementation intentionally does not use a parameter, its name may be
replaced with the discard `_`:

```raven
func Handle(_: Request) {
    Console.WriteLine("Request received")
}
```

The discarded parameter remains part of the function signature but does not
introduce a name in the body.

Parameter types and `ref`/`out` modifiers participate in overload resolution.

`val` and `var` binding keywords are not used on ordinary function parameters.
Primary-constructor parameter promotion is the exception, where `val` and `var`
declare promoted members.

### Default arguments

Parameters can provide default values:

```raven
func greet(name: string, punctuation: string = "!") {
    Console.WriteLine("Hello, ${name}${punctuation}")
}

greet("Raven")
greet("Raven", "!!!")
```

A parameter with a default value is optional at the call site. Optional
parameters must appear after required parameters.

Default expressions must be compile-time constants and must be implicitly
convertible to the parameter type. This includes literals such as numbers,
strings, and `null`, parenthesized literals, and unary `+` or `-` applied to
numeric literals. As a deliberately narrow union exception, an `Option<T>`
parameter may default to `.None`:

```raven
func find(name: string, fallback: Option<int> = .None) -> Option<int> {
    // ...
}
```

Raven records this default with Raven-specific parameter metadata so callers
compiled from another assembly reconstruct the active `None` case rather than
the inactive CLR default state of the union carrier. Payload-bearing `.Some`
defaults and defaults for other union types are not compile-time parameter
constants.

Raven also recognizes optional parameters from imported .NET methods. Metadata
defaults, including those represented by
`System.Runtime.InteropServices.DefaultParameterValueAttribute` and
`System.Runtime.InteropServices.OptionalAttribute`, participate in calls in the
same way as source-declared defaults. When an imported optional parameter has no
stored literal default, Raven uses the parameter type's CLR default value.

## Generic functions

Functions and methods can declare type parameters after their name:

```raven
func identity<T>(value: T) -> T {
    value
}

let number = identity(42)
let text = identity("hello")
```

Type arguments can usually be inferred from the arguments and expected result.
They can also be supplied explicitly:

```raven
let text = identity<string>("hello")
```

Local functions may be generic as well.

### Generic constraints

Constraints restrict which types can be used for a type parameter. They can be
written inline:

```raven
func process<T: class>(value: T) {
    // ...
}
```

or with a `where` clause:

```raven
func process<T>(value: T) where T: class {
    // ...
}
```

For a given type parameter, use either inline constraints or `where` clauses,
not both.

Supported constraints include:

* `class` — a reference type
* `struct` — a non-nullable value type
* `notnull` — a non-null type
* `unmanaged` — an unmanaged value type
* a base class type
* interface types
* `new()` — a public parameterless constructor

Multiple constraints are conjunctive: the type argument must satisfy all of
them.

At most one `class` or `struct` constraint and one base-class constraint may be
specified. Any number of interface constraints may be used, and `new()` may
appear at most once. Duplicate constraints are not permitted.

When several constraints are written, their order is:

1. `class` or `struct`
2. base class
3. interfaces
4. `new()`

Where supported by the enclosing declaration, generic type parameters may also
use `out` for covariance or `in` for contravariance.

## Local functions

Functions can be declared inside other functions, methods, and block bodies:

```raven
func calculate(value: int) -> int {
    func double(value: int) -> int => value * 2

    double(value) + 1
}
```

A local function is visible within its containing body and can capture values
from the enclosing scope:

```raven
func createCounter(start: int) {
    var current = start

    func next() -> int {
        current = current + 1
        current
    }

    // ...
}
```

Local functions support the same generic syntax and constraints as other
functions.

Bodies may also contain local `class`, `struct`, `record`, and `enum`
declarations when a helper type should remain local to that body.

## Function expressions

Functions do not need to have a declaration name. A function expression creates
a function value that can be stored, passed, or returned:

```raven
let add = (a: int, b: int) => a + b
```

The explicit `func` form is also available:

```raven
let addA = func (x: int) => x + 42

let addB = func (x: int) {
    x + 42
}
```

The shorter lambda form is convenient when the surrounding context already
makes it clear that a function value is expected:

```raven
let add = x => x + 42
```

Function expressions may use `async`, `static`, or both:

```raven
let load = async func (url: string) =>
    await client.GetStringAsync(url)
```

A function expression can optionally declare a local name for recursion:

```raven
let fib = func Fib(n: int) =>
    if n <= 1 then n else Fib(n - 1) + Fib(n - 2)
```

The name is visible only inside the function expression.

### Target typing

Function expressions are target-typed. When a compatible delegate type is known
from the surrounding context, Raven can infer parameter types from the
delegate's `Invoke` signature:

```raven
let write: (string) -> () = value => Console.WriteLine(value)
```

The same function expression can therefore be assigned to, passed to, or
returned as any compatible delegate type.

Compatibility is based on the delegate's parameter types, `ref`/`out`
modifiers, and return type. Delegate types themselves are not implicitly
convertible to one another merely because their signatures match; converting
between distinct delegate types requires an explicit cast.

### Destructuring parameters

Function-expression parameters can destructure their input.

Positional deconstruction can unpack tuples and other positional values:

```raven
let pickSecond: ((int, string)) -> string =
    ((a, b)) => b
```

Sequence deconstruction can unpack collections:

```raven
let sumTail: (int[]) -> int =
    ([head, ..tail]) => head + tail[0]
```

Patterns can be nested:

```raven
let project: (((int, string), int[])) -> string =
    (((id, name), [head, ..tail])) =>
        "$id:$name:$head:${tail.Length}"
```

Both `..name` and `...name` are accepted as rest syntax in sequence
deconstruction.

When binding keywords are omitted in a destructuring function parameter,
elements are bound as immutable values by default.

## Functions as values

Named functions and methods are first-class values. Referencing a function or
method without invoking it produces a callable value:

```raven
let writeLine: (string) -> () = Console.WriteLine

writeLine("Hello from Raven!")
```

This makes functions easy to pass to other functions:

```raven
func run(action: (string) -> ()) {
    action("ready")
}

run(Console.WriteLine)
```

The expected function type provides context for selecting a compatible method
overload.

If a method has a unique signature, Raven can often infer the function type:

```raven
let increment = Counter.Increment
```

For overloaded methods, an explicit function type or another target-typed
context is needed to select the intended overload:

```raven
let writeLine: (string) -> () = Console.WriteLine
```

### Method-reference diagnostics

A method group cannot be used where no function or delegate type is available.
For example, `let callback = Logger.Log` reports `RAV2201` when the declaration
does not otherwise determine a callable signature. If more than one overload
matches the target, Raven reports `RAV2202`; if no overload has the required
signature, it reports `RAV2203`.

Instance method references capture their receiver:

```raven
class Counter {
    value: int = 3

    func Increment(delta: int) -> int {
        self.value + delta
    }

    func Run() -> int {
        let increment = self.Increment
        increment(7)
    }
}
```

Here, `increment` continues to invoke `Increment` on the same `Counter`
instance.

## Delegate conversions

Values of the same generic delegate type follow its declared variance: return
values may widen covariantly and parameters may narrow contravariantly through
implicit reference conversions. Nullable reference widening is supported;
boxing and unsafe nullable narrowing do not make a variant conversion valid.

## Captured values

Local functions and function expressions can capture locals, parameters, and
`self` from their enclosing scope. Captured mutable variables remain shared, so
changes are visible to every function that captures the same variable.

```raven
var count = 0

let next = () => {
    count = count + 1
    count
}
```

`static func` declarations and static function expressions do not capture
enclosing state. `self` is likewise unavailable in static contexts.

`base` is available in instance members of classes that have a base class.
Calling a member through `base` dispatches directly to the selected base member
rather than through an override on the current class.

Raven implements captures using compiler-generated closure storage. Nested
capturing functions share the relevant closure state so that all references to
a captured variable observe the same value. Non-capturing function expressions
use the same general callable representation without capture fields.

## Attributes

Functions and their parameters may carry .NET attributes:

```raven
[Trace]
func compute(x: int) -> int => x * 2
```

Attributes can also target the return value:

```raven
[return: MaybeNull]
func find(name: string) -> string {
    // ...
}
```

Parameter attributes use the same attribute syntax supported by other
function-like declarations.

Explicit attribute targets are validated according to where they appear:

* `[assembly: ...]` and `[module: ...]` apply at compilation-unit scope.
* `[type: ...]` applies to type declarations.
* `[method: ...]` applies to functions and methods, and can target a synthesized
  primary constructor where applicable.
* `[return: ...]` applies to callable return metadata.
* `[param: ...]` and `[parameter: ...]` apply to parameters.
* `[property: ...]` applies to properties.
* `[field: ...]` applies to fields or synthesized backing fields where
  applicable.
* `[event: ...]` applies to events.
* `[class: ...]`, when followed by a blank line at namespace scope, applies to
  the synthesized `NamespaceMembers` class of the enclosing lexical namespace.
  Without that separation it is treated as a target on the following member
  and rejected there.

## .NET delegate interoperability

Raven function values interoperate with .NET delegates. When a function
expression is used where a delegate type is expected, its parameters and return
value are checked against the delegate's `Invoke` signature.

Method references use the same target-typing rules. For example:

```raven
func Run(action: System.Action<string>) {
    action("ready")
}

Run(Console.WriteLine)
```

selects the `Console.WriteLine(string)` overload.

When a referenced method requires implicit conversions to match the target
delegate signature, Raven may synthesize an internal bridge that performs those
conversions before invoking the method.

If a function signature cannot be represented by an existing framework
delegate—for example because it contains `ref` or `out` parameters—Raven can
synthesize a compatible delegate type:

```raven
class Accumulator {
    static func TryAccumulate(ref state: int, out doubled: int) -> bool {
        state = state + 1
        doubled = state * 2
        true
    }

    static func Execute(value: int) -> int {
        let callback = Accumulator.TryAccumulate

        var current = value
        var doubled = 0

        callback(&current, &doubled)

        current + doubled
    }
}
```

Imported methods may also use
`System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute`. When
multiple applicable candidates belong to the same overload set, Raven keeps the
highest-priority candidates before applying normal overload comparison.
