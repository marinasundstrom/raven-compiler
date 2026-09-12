# Raven for C# developers

Bring your knowledge of .NET types, generics, libraries, asynchronous APIs,
and object-oriented design. Raven uses those same foundations. This guide
concentrates on the source rules and modeling choices that affect how you
write Raven code.

For a runnable first look, start with the [language tour](introduction.md).
Use the [language reference](lang/spec/index.md) for the complete rules.
These examples target Raven 0.1.12; see [release compatibility](status.md).

<a id="gradually-adopt-idiomatic-raven"></a>
<a id="a-quick-translation-table"></a>

## What carries over, and what changes

| Area | Familiar foundation | Raven choice to notice |
| --- | --- | --- |
| Application structure | Top-level statements are available in both languages | Functions can also be declared directly in a namespace |
| Objects and data | Classes, interfaces, structs, records, generics, and mutable objects | Ordinary classes require `open` or `abstract` to permit inheritance |
| Locals | Type inference and reassignment | `let` prevents reassignment; `var` permits it |
| Decisions | Conditions and structural patterns | Blocks and `if` expressions can produce values; the final expression can supply the function result |
| Absence and failure | Nullable types and exception handling | `Option<T>`, `Result<T, E>`, and postfix `?` make absence and expected failure explicit |
| Framework calls | The same .NET libraries and CLR methods | Selected APIs have Raven projections, such as `TryParse` returning `Option` |
| Patterns | Constants, comparisons, destructuring, and guards | `let` introduces a capture; `== existingValue` compares with an existing variable |
| Async and resources | `Task`, `await`, and disposal | `use` scopes disposal; `try expression` captures exceptions as a result |

Records, pattern matching, function-valued dependencies, and top-level code
are useful in C# too. Choose them for what they model. Their presence alone
is not a reason to redesign a working application when moving to Raven.

## Entry points do not require a `Program` class

As with C# top-level statements, a small Raven program can start directly:

<div data-raven-playground="source"></div>

```raven
import System.Console.*

WriteLine("Hello from Raven")
```

A named entry point can be a plain `func Main() -> ()`. The `()` return type
is Raven's `unit`: an operation with no meaningful result.

## Utility classes become plain functions

A reusable operation can live in a namespace without a static utility class.
Put behavior on a type when it needs that type's state or encapsulation.

<div data-raven-playground="source"></div>

```raven
import System.Console.*

func normalizeCarrier(name: string) -> string {
    name.Trim().ToUpperInvariant()
}

WriteLine(normalizeCarrier("  raven express  "))
```

The final expression supplies the result. An explicit `return` is also valid.
See [functions](lang/spec/functions.md) for declarations and function types.

## Inject one operation as one function

Both languages support passing behavior as a value. Raven spells a function
type directly, for example `() -> Task<decimal>`:

```raven
import System.Threading.Tasks.*

async func isTooHot(read: () -> Task<decimal>, limit: decimal) -> Task<bool> {
    let temperature = await read()
    return temperature > limit
}
```

Use a function parameter for a single operation. An interface is useful for a
contract with related operations; a class can own state and resources.

## DTOs become explicit data shapes

Records provide structural equality in both languages. Raven supports record
classes and record structs. Reference versus value representation and
structural equality are separate choices; a record class still has structural
equality. A record also does not make every referenced object deeply immutable.

## Domain primitives become domain types

A validated domain value can be represented by an ordinary record with a
restricted constructor. This design is useful in either language. Raven's
`Result` and case shorthand make the possible outcomes explicit:

```raven
union YearError {
    case OutOfRange(value: int)
}

record struct Year private (Value: int) {
    static func Create(value: int) -> Result<Year, YearError> {
        if value < 1 {
            return .Error(.OutOfRange(value))
        }
        return .Ok(Year(value))
    }
}
```

See [data modeling](lang/features/data-modeling.md) for the choice between
records, ordinary classes, and unions.

## Absence becomes `Option`

Use `Option<T>` when absence is part of the domain contract. Nullable types
remain available for APIs that use null. `None` and `Some(null)` describe
different states when the payload type itself permits null.

<div data-raven-playground="source"></div>

```raven
import System.Console.*

let message = int.TryParse("8080") match {
    .Some(let port) => "Port: $port"
    .None => "No port supplied"
}

WriteLine(message)
```

This call demonstrates a Raven-specific framework projection: the selected
`TryParse` overload returns `Option<int>` without an `out` variable. Projections
use explicit mappings backed by Raven.Core; they do not rewrite every method
whose name starts with `Try`. Set `RavenFrameworkProjections` to `None` in the
project file to use the ordinary CLR signatures instead.

Raven's nullable checking also differs from C#. By default, use a pattern to
obtain a non-null binding. With `<EnableIsNotNullNarrowing>true</EnableIsNotNullNarrowing>`,
a direct `is not null` check can narrow a stable local or parameter inside its
true branch. It does not provide general flow analysis for arbitrary properties.
Postfix `!` suppresses nullability for one expression and reports `RAV0403`.
For references it adds no runtime check; for nullable value types it extracts
the underlying value and throws if no value is present.
See [nullability](lang/nullability.md) for the exact boundary rules.

## Expected failure becomes `Result`

`Result<T, E>` puts expected failure in the return type. Postfix `?` extracts
the successful payload or returns the failure from the enclosing function.
The enclosing return type must support that propagation.

<div data-raven-playground="source"></div>

```raven
import System.Console.*

func parsePort(text: string) -> Result<int, string> {
    int.Parse(text) match {
        .Ok(let port) when port > 0 && port <= 65535 => .Ok(port)
        .Ok(_) => .Error("Port must be between 1 and 65535")
        .Error(_) => .Error("Port must be a number")
    }
}

func endpoint(text: string) -> Result<string, string> {
    let port = parsePort(text)?
    return .Ok("http://localhost:$port")
}

WriteLine(endpoint("8080"))
WriteLine(endpoint("invalid"))
```

`int.Parse` is another selected framework projection. For other throwing calls,
`try expression` captures an exception as a result. `(try expression)?` composes
capture with propagation; each `?` handles one carrier layer.
Ordinary `try`/`catch` remains available.

## State plus payload becomes a union

A union associates each alternative with the data it needs. Use an enum for
named constants, a union for alternatives with payloads, and an open class or
interface when external implementations should extend the model.

Raven targets .NET 11's union and closed-hierarchy contracts and also supports
.NET 10 through Raven.Core compatibility metadata. One difference is intentional:
Raven supports closed interfaces, while C# 15 / .NET 11 RC 1 does not. A C# caller
can consume such an interface as an ordinary interface, but cannot rely on C#
recognizing the same closed family for exhaustive analysis. See
[closed hierarchies and interoperability](lang/spec/inheritance-and-partial-types.md)
and [unions](lang/spec/unions.md) for the supported shapes and limitations.

## Immutability is the visible default

`let` prevents reassignment of a local binding; `var` allows it. Neither makes
a referenced list, dictionary, or object deeply immutable. Use mutation when
it expresses the operation clearly, and choose the binding keyword accordingly.

## Pattern matching replaces scattered inspection

As in C#, patterns can combine structural inspection with guards. In Raven,
introducing a capture is explicit even when its type can be inferred:

<div data-raven-playground="source"></div>

```raven
import System.Console.*

func describePort(port: int, preferred: int) -> string {
    port match {
        == preferred => "Preferred port"
        let other => "Alternative: $other"
    }
}

WriteLine(describePort(8080, 8080))
WriteLine(describePort(9000, 8080))
```

`== preferred` uses ordinary equality semantics. `let other` declares a new
binding. Constants can appear directly; an existing variable needs an explicit
comparison. See [patterns](lang/spec/fundamental-patterns.md).

## Classes are still the Raven way when the domain has objects

Classes remain appropriate for identity, state, lifetimes, and encapsulation.
Ordinary classes are closed to inheritance by default; mark an extensible base
class `open` or `abstract`. A `closed` hierarchy describes a known family of
subtypes, which is a different modeling decision from allowing no subclasses.

## A practical decision sequence

Start with the shape the application needs: a function for an operation, a record
for structural data, a union for alternatives, or a class for identity and state.
Use `Option` and `Result` when absence and failure belong in the public contract.
These choices can coexist with ASP.NET Core, LINQ, NuGet libraries, and existing
C# assemblies.

Continue with [the language tour](introduction.md),
[domain modeling](lang/domain-modeling.md), or the
[language reference](lang/spec/index.md).
