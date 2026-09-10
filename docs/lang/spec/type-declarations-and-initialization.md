# Type declarations and initialization

Classes and structs group related data and behavior into a type. Their
initialization rules ensure that a new value has the members it needs before it
is used.

Raven supports classes and structs with properties, methods, constructors,
indexers, fields, and const members. The model is property-first: `val`/`var`
declarations in type bodies define properties by default. Explicit `field` and
`const` declarations provide explicit storage forms when needed.

Modifiers follow the .NET object model. For example, an `abstract` member
requires an abstract containing type, and `override` requires a matching
virtual base member.

```raven
class Counter(name: string) {
    val Name: string => name
    private field _value: int = 0

    var Value: int {
        get => _value
        private set => _value = value
    }

    // Indexer
    this[i: int]: int {
        get => _value + i
    }

    func Increment() -> () => _value = _value + 1
}
```

## Member rules

* `val`/`var` type members declare properties.
* `field` declarations are explicit storage members.
* `const` declarations are compile-time constants and map to CLR literal fields.
* Accessor-level access (e.g., `private set`) is supported.
* Methods/ctors/properties/indexers may use arrow bodies.
* Members can be marked `static` to associate them with the type rather than an instance.
* Members that intentionally hide inherited members should use the `new`
  modifier; otherwise the compiler reports `RAV0331`.
* A member name cannot match its immediate containing type name.

Delegate types are documented in [Delegate declarations](delegate-declarations.md).

## Static classes

Classes marked `static` are utility containers. They are implicitly `abstract`
and `sealed`, and all members must be `static`. Instance fields, methods,
constructors (including primary constructors), properties, events, or indexers
are not permitted inside a static class.

Use `final` alongside `override` to seal an override and prevent further
overrides in derived types (`final override` in Raven, equivalent to C#'s
`sealed override`). Applying `final` without `override` reports `RAV0309`.

## Ref structs

Ref structs are available for specialized stack-only and interop abstractions.
Their declarations, managed-reference fields, lifetime rules, and generic
anti-constraints are documented under
[Ref structs and ref safety](ref-structs-and-ref-safety.md).

## Fields and constants

Fields are explicit CLR storage members. Use them when source code needs direct
control over storage/layout (for example interop with `StructLayout` and
`FieldOffset`, fixed buffers, or ABI-sensitive layouts).

```raven
class Counter {
    private field _count: int
    private readonly field _id: int = 42
}
```

`const` declarations are separate member declarations:

```raven
class MathConstants {
    const Pi: double = 3.141592653589793
}
```

They remain compile-time constants and are emitted as metadata constants
(implicitly static), similar to other .NET languages.

## External constants

An `extern const` declares a typed constant whose value may be supplied by the
build host:

```raven
extern const SampleRate: int = 1000
extern const DeviceName: string = "RavenDevice"
extern const DeviceId: string
```

The declaration must include an explicit type; omitting it reports `RAV0178`.
An initializer is a source default and may be overridden externally. A
declaration without an initializer is required, and reports `RAV0176` unless
the build supplies a value. Supplied text is converted using invariant
formatting and validated against the declared Raven type; a failed conversion
reports `RAV0177`. It is never parsed as Raven source code. When a build value
overrides a source initializer, informational diagnostic `RAV0179` names the
declaration but deliberately omits both values so build secrets are not copied
into logs.

Once its value is selected, an external constant behaves like an ordinary
typed constant throughout the program. Provider precedence is:

1. command-line value;
2. evaluated `.rvnproj` value;
3. source initializer.

See [Raven compiler](../../compiler/raven-compiler.md) and
[Project system](../../compiler/project-system.md) for provider syntax.

## Generic types

Classes and structs optionally declare type parameters immediately after the
type name. The parameters become part of the type's identity and are available
throughout the member list.

```raven
class Box<T> {
    val Value: T { get; }

    init(value: T) { Value = value }
}

let ints = Box<int>(1)
let words = Box<string>("ok")
```

Instantiating a generic type supplies concrete type arguments between `<` and
`>` in the same order the parameters were declared. The compiler emits standard
CLR constructed types, so Raven generics interoperate seamlessly with existing
.NET APIs. When a type argument itself is generic, nest the constructions as
needed (`Dictionary<string, List<int>>`).

Type parameters support constraints using the `:` syntax. After the colon,
specify `class`, `struct`, and/or nominal types that the argument must derive
from or implement. Constraints are comma-separated and may appear in any order.

```raven
class Repository<TContext: class, IDisposable> {
    init(context: TContext) { /* ... */ }
}
```

The compiler enforces the constraint set whenever the generic type is
constructed. An argument that violates a constraint reports `RAV0320` and
identifies the unmet requirement.

Generic type arguments may be inferred from constructor arguments when the type
name is invoked without an explicit `<...>` list. This includes function
expressions passed to function-type or delegate-shaped constructor parameters:

```raven
open class Endpoint {
    init(handler: Delegate) {}
}

class Route<T> : Endpoint {
    init(pattern: string, handler: T -> string) : base(handler) {}
}

let route = Route("/{id:int}", func (id: int) => id.ToString())
// route : Route<int>
```

When same-named non-generic and generic types are both in scope, a matching
non-generic constructor is selected first. If the non-generic constructor is not
applicable, Raven may infer and select a same-named generic type. Multiple
successful generic candidates are ambiguous.

## Accessibility

Types and members accept the standard access modifiers. Applying more than
one keyword produces the expected CLR combinations:

| Modifier syntax            | Meaning |
|----------------------------|---------|
| `public`                   | Visible from any assembly. |
| `internal`                 | Visible only within the current assembly. |
| `protected`                | Visible to the declaring type and to derived types. |
| `fileprivate`             | Visible only from the current source file. |
| `protected internal`       | Visible to derived types or any code in the same assembly. |
| `private protected`        | Visible to derived types declared in the same assembly. |

Default accessibility depends on the declaration context:

* Namespace-level types—including classes, structs, records, interfaces,
  enums, unions, delegates, and extension declarations—default to `internal`.
  A namespace-level declaration must use `public` to become part of the
  assembly's exported API. Because `public` changes the assembly boundary in
  this position, it is not redundant.
* Nested types are type members and default to `public`.
* Other type members (fields, methods, properties, indexers, constructors, and
  lifecycle blocks) default to `public` for classes, structs, and interfaces.
  Narrower visibility requires an explicit modifier such as `private`,
  `internal`, or `protected`.

Constructors and lifecycle declarations follow these rules as well.

Accessibility composes through containment. A public member of an internal
type is effectively internal because callers outside the assembly cannot name
its containing type. This keeps the assembly export boundary explicit without
requiring access modifiers throughout implementation-only types.

This asymmetry is intentional. In an application or in implementation-only
library code, namespace-level declarations need no modifier because they stay
inside the assembly. In a class library, `public` visually identifies the
declarations deliberately exposed to referencing assemblies. Within a type,
members need a modifier only when access is narrowed. The result is less
modifier noise and a smaller cognitive burden when scanning either an
assembly's exported surface or a type's private implementation details.

An explicit `public` modifier remains legal on a type member even when public
is the default. The compiler may report the style diagnostic `RAV0908` for the
redundant modifier; projects that prefer explicit member accessibility may
suppress that diagnostic and enforce their convention with an analyzer.

`fileprivate` is a source-level restriction for type-like declarations. The compiler enforces same-file visibility during binding, and mangles the emitted metadata name for the generated type so file-local helpers do not publish a stable CLR-facing type name.

## Initialization model

Raven supports type-header parameters plus `init` blocks as its object
initialization model:

* a type parameter list on the class/struct header (primary-constructor parameters), plus
* one or more `init { ... }` blocks for initialization logic.

`static init { ... }` provides type initialization, and `finally { ... }`
provides finalization logic.

```raven
class Widget(name: string) {
    static init {
        // type initialization
    }

    init {
        // primary initialization logic
        // primary parameters are in scope here
    }

    finally {
        // finalization
    }
}
```

Classes and structs may declare primary-constructor parameters by adding an
argument list to the type header. The compiler synthesizes an instance
constructor whose signature matches those parameters.

An access modifier between the type name (and any type parameters) and the
primary-constructor parameter list controls the synthesized constructor's
accessibility. The default is `public`:

```raven
class Session internal (token: string) {}

record struct Year private (val Value: int) {
    static func Create(value: int) -> Year => Year(value)
}
```

Constructor accessibility is independent of promoted-property accessibility.
For example, `record struct Year private (val Value: int)` has a private
constructor and a public `Value` property, while
`record struct Year(private val Value: int)` has a public constructor and a
private property. `public`, `internal`, and `private` are valid on every primary
constructor. `protected` is also valid on class and record-class primary
constructors, but is diagnosed on struct and record-struct declarations because
value types cannot be inherited.

For `class` / `struct`, parameter promotion is explicit:

* `val` parameter: promoted to an instance `val` auto-property.
* `var` parameter: promoted to an instance `var` auto-property.
* promoted parameters may specify an access modifier (`public`, `internal`, `protected`, `private`) before `val`/`var` to control synthesized property accessibility (default is `public`).
* no binding keyword: captured in synthesized private instance storage for member access, but not promoted to a public property.
* constructor calls must use invocation syntax (`Foo()`); a standalone type name (`Foo`) is not a value expression.
* within members, an unqualified reference to a captured or promoted parameter
  refers to that primary-constructor value.

```raven
class Person(val name: string, var age: int) {
    func GetName() -> string => name
    func GetAge() -> int => age
}

let person = Person("Ada", 42)
let years = person.GetAge()
```

## Secondary constructors

`init(...)` member declarations are constructor-shape declarations.

* When used alongside the primary model above, they are **secondary constructors**.
* When used without type-header parameters/`init {}` blocks, they are a valid
  alternative syntax for initializing the object.

```raven
class Widget {
    init(name: string) { /* constructor-shape init */ }
    init(name: string, age: int) { /* secondary overload */ }
}
```

An instance constructor in a class without an explicit base initializer calls
the base type's parameterless constructor. This also applies to a primary
constructor without a base argument list and to a synthesized default
constructor. If the base type has no parameterless constructor, compilation
reports an error; supply the required arguments using `init(...): base(...)`
or a primary-constructor base argument list.

## Records

Records provide value semantics and support three declaration forms:

```raven
record Person(name: string, age: int);        // defaults to record class
record class Person(name: string, age: int);  // explicit record class
record struct Point(x: int, y: int);          // explicit record struct
```

Primary-constructor semantics differ between nominal types and records:

* `class` / `struct`: only `val`/`var` parameters are promoted to properties; parameters without a binding keyword are captured in synthesized private instance storage for member access. Access modifiers on primary-constructor parameters are valid only when the parameter is promoted.
* `record class` / `record struct`: positional parameters are promoted to properties by default (as `val` when no binding keyword is specified, or `var` when `var` is specified). The compiler synthesizes value-based members from the complete primary-constructor parameter list (`Equals`, `GetHashCode`, deconstruction, `ToString`, copy/with behavior, and record equality operators), regardless of the promoted property's accessibility.

Record instance data is limited to the primary-constructor parameters. Record bodies may declare computed properties, methods, operators, nested types, `static` storage, and `const` members, but may not declare additional instance fields, instance storage/auto-properties, instance events, or instance constructor/initializer declarations. Prefer static factory methods for alternate construction names:

```raven
record class Person(Name: string, Age: int) {
    static func Newborn(name: string) -> Person => Person(name, 0)
}
```

Outside primary-constructor promotion, parameters are ordinary value/by-ref
parameters and must not use `val`/`var` binding keywords. This applies to
functions, methods, operator declarations, and indexer parameter lists.

```raven
record class Person(name: string, age: int);

let a = Person("Ada", 42)
let b = Person("Ada", 42)

let same = a == b
```
