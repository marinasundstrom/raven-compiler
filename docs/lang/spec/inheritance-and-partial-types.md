# Inheritance and partial types

Inheritance lets one class build on another. Classes are sealed by default, so
an author must use `open` or `abstract` when other classes are meant to extend
them.

Classes are sealed by default. Marking a class `open` permits inheritance.
`abstract` classes are implicitly open and cannot be instantiated directly
(`RAV0611`).

Applying `sealed` to a class or record class creates a closed hierarchy whose
direct subtypes are fixed at compile time. A sealed class is implicitly
abstract and cannot be instantiated directly. Writing `sealed abstract` is
allowed, but `abstract` is redundant and produces warning `RAV0340`.

A base class is specified with a colon followed by the type name:

```raven
open class Parent {}
class Child : Parent {}
```

If the base list contains additional types after the base class, each of those
entries must be interfaces. When no base class is provided, the compiler uses
`object` and treats the first entry as an interface instead:

```raven
class Worker : IDisposable, ILogger {
    func Dispose() -> () { /* ... */ }
    func Log(message: string) -> () { /* ... */ }
}
```

Implementations are matched by name, parameter count, and `ref`/`out`
modifiers. See
[Interfaces](interfaces.md) for interface declaration rules and inheritance.

For struct-like declarations (`struct` and `record struct`), the base list is interface-only; the runtime base remains `System.ValueType`.

If a derived class omits a constructor, the base class's parameterless
constructor is invoked automatically. Access modifiers apply as usual. An
instance constructor may chain to a specific base overload by adding a
constructor initializer between its parameter list and body:

```raven
open class Base { init(value: int) {} }

class Derived : Base {
    init(value: int): base(value) {
        // The base invocation runs before the derived body executes.
    }
}
```

The initializer is only available on ordinary instance constructors. Static constructors report `RAV0312`, and named
constructors continue to behave as user-defined factories without chaining.

Only single inheritance is supported.

## Sealed hierarchies and `permits`

A class, record class, or interface declared with the `sealed` modifier creates a **sealed hierarchy**. For classes and
record classes, the `sealed` modifier implies `abstract`: a sealed type cannot be instantiated directly. Its set of direct
subtypes is fixed at compile time and is
determined in one of two ways:

**Same-file closure (default).** When no `permits` clause is given, any type in the same source file that directly
inherits from the sealed type is automatically part of the hierarchy. Types in other files may not inherit from it:

```raven
// shapes.rvn
sealed class Shape {}
class Circle : Shape {}   // OK — same file
class Square : Shape {}   // OK — same file
```

**Explicit permits.** An optional `permits` clause names the exact set of allowed direct subtypes. Only those types may
directly inherit from or implement the sealed type, regardless of file location:

```raven
sealed class Expr permits Lit, Add {}

class Lit : Expr {}
class Add : Expr {}
```

Sealed interfaces follow the same closure rules, but their direct subtypes are any classes, records, structs, or interfaces
that list the sealed interface directly in their base list.

**.NET interoperability:** closed interfaces are an intentional Raven extension.
C# 15 / .NET 11 RC 1 supports closed classes and record classes, but not closed
interfaces. Raven therefore keeps its own permitted-type metadata for sealed
interfaces on both .NET 10 and .NET 11. They remain ordinary CLR interfaces to
C# consumers: C# does not enforce their closed family or treat a switch covering
Raven's permitted implementations as exhaustive. Use a default arm at that
boundary. `System.Text.Json` does not infer the permitted implementations from
Raven's interface metadata; configure interface polymorphism explicitly when
serializing through that interface.

Closed classes targeting .NET 11 use the native framework contract. Targeting
.NET 10 preserves Raven's existing union and sealed-hierarchy contracts; running
the compiler on .NET 11 does not change the selected target's metadata. See
[.NET implementation notes](dotnet-implementation.md#sealed-hierarchies).

For example:

```raven
sealed interface HttpResponse permits Success, NotFound {}

record class Success(status: int, body: string) : HttpResponse {}
record class NotFound(message: string) : HttpResponse {}
```

Direct cases in a sealed hierarchy are still ordinary named types. They may be declared next to the sealed root:

```raven
sealed interface HttpResponse

record class Success(status: int, body: string) : HttpResponse {}
record class NotFound(message: string) : HttpResponse {}
```

Nested declarations are also allowed, so a sealed interface can host its direct cases inside the interface body when the
developer wants that organization:

```raven
sealed interface HttpResponse {
    record class Success(status: int, body: string) : HttpResponse {}
    record class NotFound(message: string) : HttpResponse {}
}
```

For generic sealed hierarchies, nested direct cases are treated as hierarchy cases rather than ordinary CLR-style nested
generic types. They do **not** implicitly capture the outer type parameters. Instead, each case must explicitly choose the
instantiation of the sealed root that it implements or inherits from:

```raven
sealed interface Expr<T> {
    record NumericalExpr(value: float) : Expr<float>
    record StringExpr(value: string) : Expr<string>
    record AddExpr(left: Expr<float>, right: Expr<float>) : Expr<float>
}
```

Outer type parameters are therefore not in scope inside such direct cases unless the case declares its own type parameters.

This affects only the nested case declaration form. The sealed root itself still follows the normal generic rules everywhere
it is used as a type:

```raven
func Evaluate<T>(expr: Expr<T>) -> T { ... }   // OK
func Broken(expr: Expr) { ... }                // Error: missing type arguments
```

Sealed-hierarchy constituents are still ordinary Raven types, so they use the same generic parameter and `where`
constraint rules as any other type declaration. Member methods over sealed hierarchies likewise honor their own declared
constraints during type checking. This allows constrained generic evaluators to stay fully native to Raven and .NET-style
generic interfaces:

```raven
import System.Numerics.*

sealed interface Expr<T>
    where T: INumber<T> {
    record Literal<T>(Value: T) : Expr<T>
        where T: INumber<T>

    record Add<T>(Left: Expr<T>, Right: Expr<T>) : Expr<T>
        where T: INumber<T>
}

func Evaluate<T>(expr: Expr<T>) -> T
    where T: INumber<T> {
    match expr {
        .Literal(let value) => value
        .Add(let left, let right) => Evaluate(left) + Evaluate(right)
    }
}
```

The sealed hierarchy contributes closed-family reasoning and nested-case lookup. Operator validity still comes from the
ordinary generic constraint system rather than a sealed-hierarchy-specific rule.

When nested cases are used, the containing sealed root acts as a logical qualifier for construction:

```raven
let value = Expr.NumericalExpr(40)
```

In pattern position, the same nested cases can be referenced through target-typed case patterns when the scrutinee already
determines the sealed root:

```raven
match expr {
    .NumericalExpr(let value) => value
    .AddExpr(let left, let right) => Evaluate(left) + Evaluate(right)
}
```

This target-typed `.Case(...)` form is ergonomic sugar for nested sealed cases. It is not required when the case types are
declared outside the sealed root.

When a `permits` clause is present, any attempt to derive from the sealed type outside the permitted list produces
`RAV0335`. If a type in the permits list does not actually inherit from the sealed base, the compiler reports `RAV0338`.

**Sealed record classes** follow the same rules. Applying `sealed` to a record class creates a sealed hierarchy of
records:

```raven
sealed record class Node {}
record class Leaf : Node {}
record class Branch : Node {}
```

**Match exhaustiveness.** The compiler uses the sealed hierarchy's permitted
subtypes for exhaustiveness analysis of `match` expressions. Closed branches
(sealed intermediates) are checked by concrete leaf type. Open intermediates
(non-sealed sub-hierarchies) must be covered by matching the intermediate type
itself.

When all required coverage types are handled, the match is considered exhaustive:

```raven
let result = match expr {
    Lit lit => lit.value
    Add add => eval(add.left) + eval(add.right)
}
```

Example with an open intermediate:

```raven
sealed record Expr
record Lit(Value: int) : Expr
abstract record BinaryExpr(Left: Expr, Right: Expr) : Expr
record Add(Left: Expr, Right: Expr) : BinaryExpr(Left, Right)
record Sub(Left: Expr, Right: Expr) : BinaryExpr(Left, Right)

let result = match expr {
    Lit(let value) => value
    BinaryExpr(let left, let right) => eval(left) + eval(right)
}
```

The [.NET implementation notes](dotnet-implementation.md#sealed-hierarchies)
describe the metadata representation used for sealed hierarchies.

For guidance on choosing sealed hierarchies versus discriminated unions, see
[Choosing a closed-shape type](unions.md#choosing-a-closed-shape-type).

## Partial types and members

Applying the `partial` modifier to a class, struct, record, or interface declaration allows the type to be defined across
multiple declarations in the same assembly. All declarations must use the `partial` modifier; omitting it on any declaration
produces a diagnostic and prevents the declarations from merging. When two declarations with the same name and containing scope
appear without `partial`, the compiler reports a duplicate-type diagnostic.

Partial type declarations combine their members and share a single type identity. Accessibility, `fileprivate` usage, and type parameters must match
across all parts. Class/struct/record parts also share the same nominal identity, so later parts should agree with the base type
shape established by the earlier declarations. File-scoped partial types must keep all parts in the same file. Interface bases contributed by different parts are merged.

Raven also supports partial methods, partial properties, and partial events inside partial nominal types.

A partial method must be split into:

- a declaration part with no body
- an implementation part with a block or expression body

```raven
partial class Logger {
    partial func WriteCore(message: string) -> unit;
}

partial class Logger {
    partial func WriteCore(message: string) -> unit {
        Console.WriteLine(message)
    }
}
```

A partial property must likewise have a declaration part and an implementation part. The declaration part uses accessor
signatures without bodies, while the implementation part must provide at least one accessor body.

```raven
partial class Person {
    partial var Name: string {
        get;
        set;
    }
}

partial class Person {
    partial var Name: string {
        get => field;
        set => field = value;
    }
}
```

Partial events follow the same pattern. Field-like `partial event` declarations act as the declaration part, and the
implementation part provides `add`/`remove` bodies.

```raven
partial class Notifier {
    partial event Changed: System.Action;
}

partial class Notifier {
    partial event Changed: System.Action {
        add { }
        remove { }
    }
}
```

All partial member parts must use the `partial` modifier, live inside a partial type, and have the same signature. A declaration
without an implementation, or an implementation without a declaration, produces a diagnostic. Partial properties and partial
events do not allow an auto/field-like implementation part; the implementing declaration must provide real accessor bodies.
