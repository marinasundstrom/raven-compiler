<a id="raven"></a>
<a id="start-without-ceremony"></a>
<a id="hello-world"></a>

# A tour of Raven

Raven is a typed language for .NET. If you know C#, you can bring your knowledge
of classes, interfaces, generics, collections, and asynchronous APIs. This tour
focuses on Raven's choices for expressing values, modeling outcomes, and
composing a workflow.

Each example uses familiar application code as its starting point. The
[language reference](lang/spec/index.md) covers the complete syntax and rules;
the [beginner guide](raven-for-absolute-beginners.md) introduces programming
concepts from the beginning.

<a id="a-quick-taste"></a>
<a id="target-typed-shorthand"></a>
<a id="data-shapes-and-patterns"></a>
<a id="records-and-primary-constructors"></a>

## Model each outcome with the data it needs

A quote can be ready or rejected. Give those states their own payloads instead
of asking callers to interpret a status alongside potentially missing fields.

<div data-raven-playground="source"></div>

```raven
import System.Console.*

record Shipment(Id: int, Weight: decimal)

union Quote {
    case Ready(total: decimal)
    case Rejected(reason: string)
}

func quote(shipment: Shipment) -> Quote {
    if shipment.Weight <= 0 {
        return .Rejected("Weight must be positive")
    }
    return .Ready(12.50m + shipment.Weight * 1.75m)
}

let message = quote(Shipment(42, 3.5m)) match {
    .Ready(let total) => "Total: $total"
    .Rejected(let reason) => reason
}

WriteLine(message)
```

The record and arithmetic provide the input; the union expresses the decision.
The caller handles both alternatives in a `match` expression. Exhaustiveness
checking helps keep that handling complete as the model changes.

A leading `.` uses the target type: the return annotation identifies `.Ready`,
and the matched value identifies its cases. `let total` introduces a capture.
Raven keeps binding explicit so it cannot be confused with a value comparison.

[Unions and their rules](lang/spec/unions.md) ·
[Choosing records, classes, and unions](lang/features/data-modeling.md)

<a id="bindings-and-mutability"></a>
<a id="expressions-and-matching"></a>

## Let a decision produce a value

Use an `if` block as a value when the branches compute the same result. This
lets a calculation stay next to the condition that selects it.

<div data-raven-playground="source"></div>

```raven
import System.Console.*

func shippingCost(weight: decimal, express: bool) -> decimal {
    let surcharge = if express {
        let handling = 4m
        handling + weight * 0.5m
    } else {
        0m
    }
    12.50m + surcharge
}

WriteLine(shippingCost(3.5m, true))
```

The last expression supplies each block's value, including the function's
result. An explicit `return` is also available, as in the previous example.
Loops, mutation, and early exits remain useful when the operation calls for them.

`let` prevents reassignment of a binding; `var` permits it. Neither makes a
referenced object deeply immutable. This distinction matters when working with
ordinary mutable .NET collections and objects.

[Expressions and inference](lang/spec/expressions-and-inference.md) ·
[Bindings and mutability](lang/spec/local-declarations.md)

<a id="result-and-option"></a>
<a id="propagation-expressions-"></a>
<a id="railroad-style-flow-with-carrier-methods"></a>

## Keep expected failure in the function's signature

Raven uses `Result<T, E>` for an expected success or failure, and `Option<T>`
for presence or absence. The caller can handle those cases or propagate them.

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

`?` yields the success payload or returns the failure from `endpoint`.
It requires a compatible enclosing return type. With `Option`, the same
operator propagates absence.

There is a deliberate .NET boundary choice here: Raven projects selected
framework APIs, including `int.Parse(string)`, into `Result`. The CLR method
has not changed; Raven supplies the source-level view and handling. Likewise,
`int.TryParse(string)` can produce an `Option<int>` without an `out` variable.
This is a defined set of projections, not a blanket conversion of every
throwing method. Use `try expression` to capture another throwing call.

[Option, Result, and propagation](lang/spec/async-and-error-propagation.md) ·
[Framework interoperability](lang/features/dotnet-interop.md)

## Compare a value or capture it explicitly

Patterns can test values from the current scope with a comparison operator.
A binding keyword introduces a new value instead.

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

`== preferred` compares against the parameter using Raven's ordinary equality
semantics. `let other` captures the value. A bare variable name is not a
substitute for that comparison. This is worth noticing if you come from a
language where bare names introduce pattern bindings.

For nullable values, a pattern can establish a separate non-null binding.
Direct `is not null` narrowing is also available as an opt-in compatibility
mode; Raven does not otherwise adopt a general nullable flow engine.

[Patterns and bindings](lang/spec/fundamental-patterns.md) ·
[Nullability](lang/nullability.md)

<a id="async-and-await"></a>
<a id="net-interop"></a>

## Compose with the .NET APIs you already use

`Task`, `HttpClient`, and `await` still serve their familiar roles. Raven's
contribution in this example is the error boundary and its composition with
propagation:

```raven
import System.*
import System.Net.Http.*
import System.Threading.Tasks.*

async func downloadLength(url: string) -> Task<Result<int, Exception>> {
    use http = HttpClient()
    let text = (try await http.GetStringAsync(url))?
    return .Ok(text.Length)
}
```

`try` captures the exception as a result; the following `?` propagates its
error. `use` scopes resource disposal to the function. The function advertises
both its asynchronous work and its possible failure in the return type.
Each `?` unwraps one carrier layer, so a call that already returns a `Result`
can require another explicit propagation step.

Ordinary .NET exception handling remains available. Choose capture when the
operation should expose exceptions as values, and `try`/`catch` when you want
to handle them in that form.

[Async functions](lang/spec/async-functions.md) ·
[Exceptions and capture](lang/spec/error-handling.md)

<a id="functions"></a>
<a id="extensions"></a>
<a id="accessibility-defaults"></a>

## Keep objects, functions, and libraries together

Classes and interfaces remain first-class modeling tools for identity, state,
lifetimes, and polymorphism. Standalone functions can live directly in a
namespace, and a single-operation dependency can be expressed as a function
parameter. Use the shape that describes the dependency.

The same program can use LINQ, NuGet packages, ASP.NET Core, and Raven's domain
types. Familiarity does not mean every source rule is identical: for example,
Raven uses `()` for `unit` rather than `void`, and ordinary classes are closed
to inheritance unless declared `open` or `abstract`. The reference explains
these differences where they affect the code you write.

<a id="where-to-go-next"></a>

## Continue with a real program

- [Install and run Raven](getting-started.md).
- [Build an ASP.NET Core API](workloads/web-api.md).
- [Compare Raven and C# idioms](raven-for-csharp-developers.md).
- [Find a language feature](lang/spec/index.md).
- [Check release compatibility](status.md) when using examples with a published SDK.
