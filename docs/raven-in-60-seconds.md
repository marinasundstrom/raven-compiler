# Raven in 60 seconds

Raven brings expression-oriented code and explicit state modeling to .NET.
If you write C#, the records, decimal arithmetic, and console call below will
look familiar. Focus on how the quote's possible outcomes carry their own data
and how the caller handles them.

<div data-raven-playground="source"></div>

```raven
import System.Console.*

record Shipment(Id: int, Weight: decimal)

union QuoteResult {
    case Quoted(amount: decimal)
    case Rejected(reason: string)
}

func Quote(shipment: Shipment) -> QuoteResult {
    if shipment.Weight <= 0 {
        return .Rejected("Weight must be positive")
    }

    let amount = 12.50m + shipment.Weight * 1.75m
    return .Quoted(amount)
}

let shipment = Shipment(42, 3.5m)

let message = match Quote(shipment) {
    .Quoted(let amount) => "Quote: $amount"
    .Rejected(let reason) => "Cannot quote: $reason"
}

WriteLine(message)
```

`Quoted` carries an amount; `Rejected` carries a reason. Each outcome has the
data it needs, and `match` makes the handling visible in one place.

A few Raven spellings carry the rest of the example:

- `let` declares a binding that cannot be reassigned; `var` allows reassignment.
- `.Quoted` and `.Rejected` use the type supplied by their context.
- `let amount` captures a case's payload. The binding keyword makes capture
  explicit, distinct from comparing with an existing value.
- `func` declares a function that can live directly in a namespace.

The surrounding .NET APIs remain available in the same program.

## Expected failure is data

Raven uses `Option<T>` for a value that may be absent and `Result<T, E>` for an
operation that may succeed or fail in an expected way.

```raven
func ParsePort(text: string) -> Result<int, string> {
    return int.Parse(text) match {
        Ok(let port) when port > 0 => Ok(port)
        Ok(_) => Error("Port must be positive")
        Error(_) => Error("Port must be a number")
    }
}
```

Raven projects the known framework method `int.Parse(string)` to a `Result`
whose error channel preserves its framework exception types, so `match` handles
its expected failures directly.
The `try` expression remains available for genuinely throwing APIs, and the
propagation operator `?` can return an error from the current function when no
local handling is needed.

## Keep familiar .NET design where it fits

Use a class when identity, mutable state, lifecycle, or open polymorphism is
part of the model. Use records and unions for value-shaped and closed-domain
data. Raven does not require choosing between functional and object-oriented
programming for an entire application.

## Continue

- [Install and run Raven](getting-started.md)
- [Choose your learning path](learn.md)
- [Read the full language introduction](introduction.md)
- [Model domains with functions, records, unions, and classes](lang/domain-modeling.md)
- [Look up precise language rules](lang/spec/index.md)
