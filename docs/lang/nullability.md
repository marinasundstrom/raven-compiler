# Nullability and absence

Raven treats nullability as part of an expression's static type. A value of
type `T?` must be explicitly unwrapped, pattern-bound, converted, or suppressed
before it can be used as `T`.

Raven encourages programs to model intentional domain absence with `Option<T>`
and expected failure with `Result<T, E>`. Nullable types remain important for
.NET interoperability, reference state, and gradual adoption.

In Raven's modeling vocabulary, `null` describes runtime null state rather
than a domain-level “possibly absent” case. Use `Option<T>` when absence is a
meaningful state of the application instead of carrying that meaning in a null
reference.

> Use `Option<T>` when absence is part of the domain. Use `T?` when null is part
> of an interoperability or storage contract. Handle either form explicitly.

This distinction does not remove the practical tools needed at a nullable
boundary. A nullable value has a null case and a `T` case, so code can match
and extract its value similarly to handling an option or union case. The
similarity is in how the cases are eliminated, not in their representation or
meaning. Raven also supports conditional access, explicit suppression, and
opt-in `is not null` narrowing. `T?` describes a CLR null state, while
`Option<T>` makes domain absence an explicit case in the program's model.
Raven can enforce that distinction strictly because it does not need to
preserve a legacy nullable-unaware source model.

## One unified nullable type model

`T?` means that a value of `T` may also be null. Raven applies that rule
uniformly to reference and value types:

```raven
let name: string? = ReadName()
let count: int? = ReadCount()
```

Both declarations have a nullable wrapper in Raven's symbol model. Their CLR
representations differ: a nullable reference uses .NET nullable metadata,
while a nullable value uses the conventional `System.Nullable<T>` ABI shape.
That platform distinction does not create two source-language type systems.

Raven's type system is its own semantic model, designed to interoperate with
the .NET type system. `TypeInfo.Type` is the authoritative type of an
expression. Raven does not publish a second flow-sensitive type or nullability
result for the same expression.

## Nullable values must be handled explicitly

A member cannot be accessed through `T?`:

```raven
func PrintLength(value: string?) -> unit {
    // RAV0402: Nullable value must be unwrapped
    WriteLine(value.Length)
}
```

Prefer a pattern that both checks the value and introduces a non-null binding:

```raven
func PrintLength(value: string?) -> unit {
    if let text: string = value {
        WriteLine(text.Length)
    }
}
```

A direct type pattern has the same useful property:

```raven
if value is string text {
    WriteLine(text.Length)
}
```

The original storage remains `string?`; `text` is a separate `string` value.
This rule is predictable for locals, mutable variables, properties, and values
whose backing storage another call or thread could change.

This intentionally differs from C# nullable flow analysis and from languages
that smart-cast sufficiently stable storage after a null check. C# needed a
migration-friendly analysis for an existing nullable-unaware ecosystem. Raven
starts with nullable types as a strict source-language distinction: checking a
storage location does not change its type. A successful pattern names the
non-null value that subsequent code may use.

By default, null checks remain valid conditions but do not change a value's
type:

```raven
if value is not null {
    WriteLine(value.Length) // error: value is still string?
}

if value != null {
    WriteLine(value.Length) // error: value is still string?
}
```

These familiar .NET forms can still select a branch. Bind the checked value to
use it as non-null. Equality checks may also invoke user-defined equality;
`RAV9015` can replace an equality comparison with the stricter `is null` or
`is not null` form when that distinction matters.

### Opt-in compatibility narrowing

Projects that consume null-oriented .NET APIs and need the familiar direct
null-check style can opt into a narrow compatibility feature:

```xml
<EnableIsNotNullNarrowing>true</EnableIsNotNullNarrowing>
```

With that option, a direct `value is not null` check narrows a stable local or
parameter only inside the true branch:

```raven
if value is not null {
    WriteLine(value.Length) // value is string here
}
// value is string? again here
```

The declared symbol remains `string?`; `TypeInfo.Type` for references inside
the guarded branch is `string`, which also gives hover the contextual type.
This option does not enable a general nullable flow engine: it does not infer
through assignments, loops, early exits, equality operators, or arbitrary
boolean expressions. `if let` and type patterns remain the canonical Raven
forms because they introduce a stable, explicit non-null binding.

The option changes the spelling of the proof for stable storage, not the
strictness of nullable types. Code must still establish the `T` case before it
can use a `T?` as `T`; Raven does not adopt C#'s broader flow-based flexibility.

This remains an interop/storage facility, not a domain-modeling recommendation.
Use `Option<T>` when a Raven value may meaningfully be present or absent; use
nullable types when the underlying contract genuinely permits a null reference.
Code that only needs to continue through a nullable member chain can use `?.`
without enabling compatibility narrowing.

## Match every nullable state

`match` makes all states visible in one place:

```raven
let description = match value {
    string text => "Length: ${text.Length}"
    null => "No value"
}
```

For a nullable closed hierarchy, exhaustiveness includes every permitted leaf
type plus `null`:

```raven
let description = match value {
    SubClassA a => Describe(a)
    SubClassB b => Describe(b)
    null => "No value"
}
```

An open hierarchy also needs a base-type or discard arm:

```raven
let description = match value {
    SubClassA a => Describe(a)
    SubClassB b => Describe(b)
    null => "No value"
    BaseClass other => DescribeUnknown(other)
}
```

Use `_` instead when the remaining non-null value is intentionally ignored.

## Explicit escape hatches

Conditional access handles a nullable receiver without changing the receiver's
type:

```raven
let length: int? = value?.Length
```

Postfix `!` explicitly suppresses nullability for one expression and reports
warning `RAV0403`:

```raven
let length = value!.Length
```

For references, `!` inserts no runtime null check. For nullable value types,
it extracts the underlying value and throws `InvalidOperationException` if
the value is absent. This value-type extraction goes beyond C#'s
null-forgiving operator; see [Nullable suppression](spec/type-system.md#nullable-suppression).

Suppression is appropriate only when the programmer has knowledge the type
does not express. Prefer a pattern when the program can prove the state.

## .NET metadata boundaries

Raven imports and emits .NET nullable annotations so public contracts retain
their ABI meaning:

- nullable metadata determines whether an imported type is `T` or `T?`;
- `[MaybeNull]` makes an imported call result statically nullable when the
  underlying type can represent null;
- `[AllowNull]` and `[DisallowNull]` affect accepted input values;
- conditional flow attributes such as `[NotNullWhen]`, `[NotNullIfNotNull]`,
  and `[MemberNotNull]` do not refine Raven storage locations.

The last rule is an intentional language boundary. Raven consumes the contract
that determines a value's static type, but it does not adopt C#'s contextual
null-state machinery.

Generic substitution preserves Raven's unified nullable symbol while retaining
the projection selected by the original CLR signature. For example, an
unconstrained `T?` constructed with `int` is still `int?` in the Raven semantic
model, but it projects to CLR `int` because the generic signature cannot become
`Nullable<int>`. A `T?` whose type parameter has a value-type constraint
projects to `Nullable<T>`. The same projection is used by conversions and
emission, so constructed symbols, hover information, and emitted calls agree.

This distinction is especially important at metadata boundaries. C# nullable
annotations on an unconstrained type parameter are imported as Raven nullable
types for a unified API experience, while their original underlying-type
projection is retained. Concrete `int?` continues to project to
`System.Nullable<int>`, and concrete `string?` continues to project to CLR
`string` with nullable metadata.

Compiler API consumers can inspect this distinction without depending on an
internal symbol implementation:

```csharp
if (type.TryGetNullableUnderlyingType(out var underlyingType))
{
    var projection = type.GetNullableAbiProjection();
    // AnnotatedUnderlyingType or NullableValueType
}
```

`GetNullableAbiProjection()` is total: it returns `None` for a non-nullable
type, `AnnotatedUnderlyingType` for nullable reference types and nullable
unconstrained type parameters, and `NullableValueType` for types represented as
`System.Nullable<T>`. ABI projection is not part of semantic symbol
identity. `SymbolEqualityComparer.Default` compares Raven nullability but treats
two nullable types with the same underlying semantic type as equal even when
their CLR projections differ. `SymbolEqualityComparer.IgnoringNullability`
additionally ignores the nullable decoration. Emitters and other CLR-boundary
consumers must query the projection explicitly.

## Prefer `Option` for domain absence

If a missing value is an expected application state, represent it in the API:

```raven
func FindCustomer(id: CustomerId) -> Option<Customer> {
    // ...
    None
}

let message = match FindCustomer(id) {
    Some(let customer) => "Found ${customer.Name}"
    None => "Customer not found"
}
```

`Option<T>` gives absence a named, closed shape and supports exhaustive
matching. `Result<T, E>` serves the corresponding role when an operation can
fail with useful error information.

Do not mechanically replace every nullable type at a .NET boundary. A program
can accept `string?`, handle it once with a pattern, and pass a `string` or
`Option<string>` into the rest of its domain code.

The built-in `RAV9012` analyzer identifies simple nullable code that may be
better expressed with `Option`. Analyzer severity is configurable through
`.editorconfig`; the meaning of `T?` is not configurable.

Continue with [Pattern matching](features/patterns.md),
[Raven.Core and `Option`](../compiler/raven-core-library.md), and
[Analyzer configuration](../compiler/analyzers/configuration.md).
