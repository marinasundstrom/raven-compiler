# .NET Implementation Notes

This document explains how Raven constructs map to the .NET runtime and metadata. For source-language semantics, start with the [language reference](index.md).

## Unit type
When interacting with .NET, methods that return `void` are projected as returning `unit`, and Raven's `unit` emits as `void` unless the value is observed. After any call that returns metadata `void`, the compiler loads `Unit.Value` so the invocation still produces a `unit` result. In an expression statement that value is discarded, enabling nested `unit`-returning calls such as `Console.WriteLine(Console.WriteLine("foo"))`. The `unit` type is a value type (`struct`) and participates in generics, tuples, and unions like any other type.

## Return statements
A `return` without an expression in a method that returns `unit` emits IL with no value. If the underlying method returns `void`, `Unit.Value` is loaded to produce a `unit` result before the `ret` instruction.

## Attributes
Source attributes are bound using the same import and namespace lookup rules as other type references, so `import System.*` enables `[Obsolete]` without a fully qualified name. When Raven creates metadata `AttributeData`, it accepts the subset of argument expressions supported by the runtime: literals (including `null`), enum constants, `typeof` expressions, array/collection literals (including empty collections when a target type is known), constant enum flag expressions such as `.Class | .Delegate`, and conversions among those forms. Enum constants may be written with a qualified name such as `AttributeTargets.Delegate`, or with target-typed member syntax such as `.Delegate` when the attribute constructor parameter supplies the enum type. The compiler lowers these argument forms to typed constants before emitting the attribute payload.

Raven also validates explicit target prefixes (`assembly:`, `return:`, etc.)
against declaration position:

* `assembly:` binds only at the compilation-unit level.
* `return:` binds to callable return metadata, not to declaration-level method attributes.
* `class:` followed by a blank line at namespace scope binds to the synthesized
  `NamespaceMembers` class for that lexical namespace. Attribute contributions
  from block-scoped, file-scoped, and repeated namespace declarations are
  merged in compilation syntax-tree order before metadata emission.
* Target prefixes used in an invalid declaration context are rejected with an
  attribute-target diagnostic.

## Extension members
Raven both declares and consumes extension members using CLR extension metadata,
but it classifies extension semantics per emitted member rather than treating an
entire container as one kind of extension surface. Source extensions arise from
two forms:

* An `extension` declaration emits a `static` class named after the container.
  Instance extension members become `static` methods whose first parameter
  represents the `self` receiver. The compiler synthesizes that parameter,
  applies the `ExtensionAttribute`, and copies any explicit parameters written
  in source onto the emitted method signature.
* Existing static methods annotated with `[Extension]` continue to be recognised
  as extensions.

Computed instance extension properties declared inside an `extension` body lower
to accessor methods that follow the same pattern. The compiler synthesizes
`get_` and `set_` methods, inserts the receiver as the leading parameter, and
marks each accessor with `ExtensionAttribute`. Property metadata is emitted
alongside the accessors so reflection reports a property with the expected
accessor pair even though the backing logic is implemented by static methods.

To interoperate with C# extension blocks (C# 14), Raven also emits an extension
marker nested type for each `extension` declaration. The marker type is named
`<>__RavenExtensionMarker` and contains a single `<Extension>$` method whose
parameter encodes the receiver type. Each emitted extension member (methods and
properties, including static extension members) is annotated with
`System.Runtime.CompilerServices.ExtensionMarkerNameAttribute` pointing to the
marker type name, enabling C# to recover the extension receiver signature when
consuming Raven-compiled assemblies.

When importing metadata, Raven distinguishes classic extension methods from
static extension members per member. Method-level `ExtensionAttribute`
continues to identify classic extension methods, while receiver-marker metadata
identifies static extension members that participate in `Type.Member` lookup
without being treated as classic extension methods. This keeps mixed extension
containers compatible with .NET/C# lookup expectations.

In both cases the emitted metadata matches C#'s expectations. A member-style
extension call passes its receiver as the leading argument to the underlying
static method. Extension-property getters receive the target as their first
argument, while setters receive both the target and assigned value.

## Properties and fields

Raven is property-first for type members:

* `val`/`var` declarations in classes/structs emit CLR properties with accessor
  methods (`get_`/`set_`/`init` as applicable).
* Stored and auto-style properties synthesize backing storage when needed.
* `field` used inside a property accessor refers to that synthesized backing
  field for the current property.

Explicit `field` declarations are emitted as CLR fields and are intended for
storage/layout-sensitive scenarios (for example interop with
`StructLayout(LayoutKind.Sequential|Explicit)` and `FieldOffset`).
`readonly field` emits an `initonly` field. Dedicated `const` declarations emit
metadata literal fields (`static`/`literal`).

### Private storage properties

Raven may represent a private storage property as a field only when doing so
preserves its observable behavior:

```raven
private val count: int
private var score: int
```

The declaration remains a property in Raven's semantic model and tooling. For a
property that requires no accessor logic, reads and writes may become direct
field access and accessor methods may be omitted from metadata. Computed
properties are never represented this way.

## Lifecycle declarations

Raven lifecycle declarations map to CLR methods:

* `init { ... }` lowers as an instance constructor body (`.ctor`) with no
  explicit parameter list in source.
* `static init { ... }` lowers as a static constructor (`.cctor`).
* `init(...)` remains constructor-shape syntax and maps to `.ctor` overloads.
* `finally { ... }` lowers as a `Finalize` override.

## Union interop (C#)
Raven unions compile into a carrier type plus independent case types that C#
can consume directly. A non-generic carrier physically contains its cases. A
generic carrier has a non-generic, same-name companion that contains its cases,
so a case carries only the generic parameters used by its payload:

```text
Result`2        // carrier
Result          // RavenUnionCompanion("Result`2")
 ├─ Ok`1
 └─ Error`1
```

Raven treats the carrier and annotated companion as one logical declaration
when loading metadata. Case symbols are members of the union for lookup,
`import Result.*`, preludes, completion, and matching, even though their CLR
metadata owner can be the companion. The association is explicit and is never
inferred from the shared name alone.

Each case is a public type with a constructor that accepts the payload values,
a set of get-only properties for those payloads, and a `Deconstruct(out ...)`
method that mirrors the payload order. The union carrier exposes overloaded
`TryGetValue(out CaseType value)` helpers to safely extract a case instance. For
parenthesized unions such as
`union Either<T1, T2>(T1 | T2)`, the variants are declared by existing types
instead of `case` declarations, so the carrier constructor and `TryGetValue`
overloads operate directly on those variant types.

Raven follows the C# union ABI for metadata recognition: a union carrier is a
class or struct with `System.Runtime.CompilerServices.UnionAttribute`, a public
`Value` property of `object` or `object?`, and at least one public
one-parameter constructor. Those constructors define the variant set. Public
`TryGetValue(out T)` methods are an access pattern and do not introduce
additional variants when constructors are present. Nullable active contents are
derived from nullable constructor parameter types, not from `Value` being
`object?`.

A C# union can instead delegate its contract to a directly nested public
`IUnionMembers` interface that the carrier implements. Its public static
one-parameter `Create` methods define the variants and return the carrier;
by-value and `in` parameters are supported. Raven calls these factories for
union conversions and uses the interface's `Value`, optional `HasValue`, and
optional `TryGetValue` members for patterns. Carrier members absent from the
provider interface do not participate in the union contract. Nullable contents
come from factory parameter types. The provider interface is not a union case.

Positional case payloads use stable metadata names even though ordinary Raven
symbol display preserves the unnamed source form. A single unnamed payload is
emitted as constructor parameter `value` and property `Value`; multiple
payloads use `item1`, `item2`, ... and `Item1`, `Item2`, .... The compiler marks
the generated parameter names so API consumers can request them explicitly
without making them part of the default Raven signature presentation.

For nullable members in a parenthesized union declaration, Raven emits
nullable-capable constructor parameter types for the listed members and does not
emit a synthetic null constructor:

```raven
union JsonValue(string? | double | bool | JsonObject | JsonValue[])
```

Raven pattern matching still treats nullable variant contents as the non-null
listed variants plus a distinct `null` branch.

Producing and consuming a body-declared union from C# uses its nested case
types. For example:

```csharp
// Raven
// public union Result<T, E> {
//     case Ok(value: T)
//     case Error(error: E)
// }

Result<int, string> result = new Result.Ok<int>(42);

if (result is Result.Ok<int> ok)
{
    Console.WriteLine(ok.Value);
}

if (result.TryGetValue(out Result.Ok<int> extracted))
{
    var (value) = extracted;
    Console.WriteLine(value);
}
```

The explicit `int` on `Result.Ok<int>` is required by current C#. Raven does
not emit speculative `CreateOk` or `CreateError` factories; factory ergonomics
are deferred until the C# generic-constructor-inference direction is settled.
The case-to-carrier assignment remains a C# union conversion backed by the
carrier's public one-parameter constructor.

For a parenthesized union, extraction is performed directly on the variant type:

```csharp
// Raven
// union Either<T1, T2>(T1 | T2)

Either<int, string> value = 42;
if (value.TryGetValue(out int left))
{
    Console.WriteLine(left);
}
```

These members allow C# callers to work with Raven unions without
needing reflection, while Raven still relies on the synthesized metadata
attributes to preserve the union semantics for other tools.

## Struct union default state

Raven source `union` declarations emit struct carriers by default, matching the
C# union direction. Like any value type, a struct union can be zero-initialized
with `default(U)` before any union constructor has populated it. In that
inactive carrier state, `Value` is `null`, `HasValue` is `false`, and no case is
active.

The inactive carrier state is a runtime representation state, not a declared
union case. Raven therefore keeps it separate from the source case set:

* A local value initialized from a union case is known active, so matching it is
  exhaustive when every declared case/member is covered. A catch-all arm after
  all cases is redundant.
* A local initialized with `default`, or a local that may flow from `default`,
  may still use a catch-all arm to intentionally handle the inactive carrier,
  but the inactive carrier is not a source exhaustiveness case.
* Function parameters and `self` are active inside the callee because the call
  boundary rejects possibly inactive arguments before entry. Forwarding them
  across another call or return boundary is allowed.
* Fields and properties are storage/interop boundaries that may be
  inactive/default. Forwarding those values across another call or return
  boundary requires local flow to prove an active value, usually by copying or
  reconstructing a declared case.
* Passing a possibly inactive struct-union value to a struct-union parameter is
  rejected at the call site with `RAV0405`.
* Returning a possibly inactive struct-union value is rejected at the return
  boundary with `RAV0406`.

`union class` carriers do not have an extra zero-initialized carrier state. A
class union value exists only after construction or conversion through one of
its union cases or constructors, subject to normal nullable-reference rules for
the carrier reference itself.

## Sealed hierarchies

A Raven sealed-hierarchy root is emitted as an abstract CLR type rather than an
IL-sealed type, allowing its permitted cases to inherit from it. The root also
receives the framework's `System.Runtime.CompilerServices.IsClosedTypeAttribute`
with its `DerivedTypes` property when targeting .NET 11. C# consumers recognize the closed family, and
`System.Text.Json` can infer its derived types when closed-type polymorphism is
explicitly enabled. Raven imports this marker from C# assemblies and discovers
direct subclasses in the declaring module; deriving from an imported closed
root is an error.

Imported generic base types retain their type arguments. Constructing a case
substitutes those arguments into its base, including reordered parameters, so
matching a constructed closed root uses constructed case types.

Closed interfaces are a Raven extension, outside C# 15's closed-class contract.
They retain Raven's `ClosedHierarchyAttribute(Type[])` metadata, as do targets
whose reference assemblies do not provide `IsClosedTypeAttribute`.

## Generic variance

The Raven compiler surfaces the CLR's variance metadata directly. When importing
types from reference assemblies, the `GenericParameterAttributes` flag on a type
parameter controls the reported `VarianceKind`: `Covariant` maps to `out` and
`Contravariant` maps to `in`. These annotations influence interface
implementation checks and reference conversions so that, for example,
`IEnumerable<string>` is recognised as an implementation of
`IEnumerable<object>`, and `IComparer<object>` satisfies a requirement for
`IComparer<string>`.

Source interface declarations may annotate their type parameters with `out` or
`in`. Raven maps those modifiers onto the same metadata flags when emitting
symbols, so variant source interfaces interoperate with metadata-defined
counterparts without requiring any special handling.

## Ref-like metadata

A `ref struct` carries `System.Runtime.CompilerServices.IsByRefLikeAttribute`.
A `readonly ref struct` also carries `IsReadOnlyAttribute`. Managed-reference
fields use the CLR `BYREF` signature form.

The `allows ref struct` anti-constraint sets the standard CLI
`AllowByRefLike` (`0x20`) generic-parameter flag. Scoped parameters imported
from .NET honor `ScopedRefAttribute`, matching Raven's `scoped` lifetime rules.
