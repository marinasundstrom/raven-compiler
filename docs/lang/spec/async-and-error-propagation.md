# Error propagation and carrier types

`Result` and `Option` let a function report failure or a missing value without
throwing an exception. Raven provides short forms for returning those cases
early so the main path stays easy to read.

## Try expressions

`try` captures exceptions as values. `(try expression)?` combines capture with carrier
propagation.

Together, these forms provide an interop boundary from exception-throwing APIs
to Raven's value-based error flow. `try` is appropriate when code needs to
inspect or transform the captured failure locally. `(try expression)?` is appropriate when
the enclosing carrier-returning operation cannot continue and should propagate
that failure immediately.

| Form | Result type | Success case | Failure case |
| --- | --- | --- | --- |
| `try expr` | `Result<T, Exception>` | `Ok(value)` or `Ok(())` | `Error(exception)` |
| `(try expr)?` | `T` inside an enclosing carrier-returning context | yields the success payload | propagates the captured error through the enclosing `Result`/`Option` |

`try expr` evaluates `expr` exactly once and converts the outcome to
`Result<T, Exception>`, where `T` is the operand type. If the operand has type
`unit`, the success case is `Ok(())`.

`(try expr)?` composes exception capture with ordinary propagation. It is valid
only when the enclosing
function or lambda returns a compatible `Result<_, _>` or `Option<_>`.

### Examples

```raven
import System.*

func describeNumber(text: string) -> string {
    return try Convert.ToInt32(text) match {
        Ok(let value) => "Parsed: $value"
        Error(FormatException ex) => "Invalid format: ${ex.Message}"
        Error(_) => "Unexpected failure"
    }
}

func parseRequiredInt(text: string) -> Result<int, Exception> {
    let value = (try Convert.ToInt32(text))?
    return Ok(value)
}

func saveText(path: string, text: string) -> string {
    return try System.IO.File.WriteAllText(path, text) match {
        Ok => "Saved"
        Error(UnauthorizedAccessException ex) => "Access denied: ${ex.Message}"
        Error(IOException ex) => "I/O error: ${ex.Message}"
        Error(_) => "Unexpected failure"
    }
}
```

`Convert.ToInt32` is used here because it remains a throwing .NET API. Raven's
standard framework projection for `int.Parse(string)` already returns
`Result<int, FormatException | OverflowException>`;
`try` is still the general mechanism for capturing exceptions from APIs without
such a projection. Setting `RavenFrameworkProjections` to `None` restores the
ordinary throwing CLR `int.Parse` member.

### Detailed rules

* The operand may be any expression that is valid in the current context.
* `try expr` does not accept `catch` or `finally` clauses; use statement-form
  `try` for structured exception handling.
* Nested `try` expressions are invalid and produce `RAV1906`.
* `(try expr)?` may be followed by `match`, like other propagated expressions.
* The former `try? expr` syntax is rejected with `RAV1925`.
* Each postfix `?` propagates one carrier layer. If `expr` already returns a
  `Result<T, E>`, `(try expr)?` preserves that inner result. To propagate both
  layers, write `((try expr)?)?` or bind the intermediate result and propagate it.
* `await` may appear inside `try expr` when the enclosing context is async.
* In pattern position, `Ok` is shorthand for `Ok(())` when the success payload
  is `unit`; `.Ok` remains available as target-typed shorthand.

## Result and Option carrier operators

`Result<T, E>` and `Option<T>` share the same carrier terminology throughout the
spec: `?` unwraps or propagates, and `?.` conditionally maps over the success
case.

| Form | Receiver | Result | Empty or error case |
| --- | --- | --- | --- |
| `expr?` | `Result<T, E>` | `T` | propagates `Error(error)` |
| `expr?` | `Option<T>` | `T` | propagates `None` |
| `expr?.Member` | `Result<T, E>` | `Result<U, E>` | preserves `Error(error)` |
| `expr?.Member` | `Option<T>` | `Option<U>` | preserves `None` |

### Propagation (`?`)

The postfix `?` operator unwraps a carrier value and propagates the non-success
case to the nearest enclosing carrier-returning function or lambda.

#### Examples

```raven
func loadAndParse(path: string) -> Result<int, Exception> {
    let text = ReadAllText(path)?
    let value = ParseInt(text)?
    return Ok(value)
}

func firstEven(values: int[]) -> Option<int> {
    let value = FindFirstEven(values)?
    return Some(value)
}
```

#### Detailed rules

* For `Result<T, E>`, `Ok(value)` yields `value` and `Error(error)`
  immediately returns `Error(error)` from the enclosing context.
* For `Option<T>`, `Some(value)` yields `value` and `None` immediately returns
  `None` from the enclosing context.
* `expr?` is valid only when both the operand and the enclosing function or
  lambda return type implement compatible `IPropagatable` contracts.
* The operand is evaluated exactly once; the compiler may introduce temporaries
  to preserve that rule.
* `expr?` performs propagation only when `?` is not followed by a trailer. In
  postfix position, `expr?.Member`, `expr?(...)`, and `expr?[...]` are parsed as
  conditional-access forms instead. For a custom `IPropagatable` carrier,
  `expr?.Member` propagates the receiver and then accesses `Member` on its
  output. (`Result` and `Option` retain the lifted mapping behavior described
  below.)

If propagation relies on a user-defined implicit conversion on the error
channel, the compiler reports informational diagnostic `RAV1506`.

#### Custom carriers

Propagation is defined by the Raven.Core interface
`IPropagatable<TSelf, TOutput, TResidual>`, not by a required union shape or
type name:

```raven
public interface IPropagatable<TSelf, TOutput, TResidual> {
    func TryGetOutput(out output: TOutput) -> bool
    func TryGetResidual(out residual: TResidual) -> bool
    static func FromResidual(residual: TResidual) -> TSelf
}
```

`TryGetOutput` returns `true` and assigns the value produced by `expr?` when
evaluation can continue. When it returns `false`, the compiler calls
`TryGetResidual`; a conforming implementation must then return `true` and
assign the residual. The residual is implicitly converted to the enclosing
carrier's residual type and passed to that carrier's `FromResidual` method for
the early return. The operand is evaluated exactly once.

`TSelf` makes the static reconstruction member usable without associated types,
which Raven does not currently expose in interface declarations. It must be the
concrete implementing carrier. Different carrier types may interoperate when
their residual types are implicitly convertible. `Result<T, E>` implements the
contract with output `T` and residual `E`; `Option<T>` implements it with output
`T` and the unit residual `()`.

### Conditional member access (`?.`)

Carrier conditional access maps a member access over the success case of a
carrier without unwrapping the carrier itself.

#### Examples

```raven
record class User(Name: string, Item: Option<Item>)
record class Item(Name: string)

union LookupError {
    case MissingUser
    case MissingItem
}

func getUser() -> Result<User, LookupError> {
    return Error(.MissingUser)
}

func userNameLength() -> Result<int, LookupError> {
    let length = getUser()?.Name?.Length?
    return Ok(length)
}

func selectedItemName() -> Result<string, LookupError> {
    let maybeItem = getUser()?.Item?

    match maybeItem {
        Some(let item) => Ok(item.Name)
        None => Error(.MissingItem)
    }
}
```

#### Detailed rules

* For `Result<T, E>`, `expr?.Member` evaluates `Member` only when `expr` is
  `Ok(payload)` and returns `Result<U, E>`.
* For `Option<T>`, `expr?.Member` evaluates `Member` only when `expr` is
  `Some(payload)` and returns `Option<U>`.
* The member-access form is the only lifted carrier conditional-access form.
* `expr?[index]` and `expr?(args)` are not lifted for `Result` or `Option`.
* If indexing or invocation is required, unwrap first with `?`, pattern
  matching, or an explicit helper API.

For nullable/reference receivers, Raven also supports null-conditional member
assignment in statement position:

```raven
person?.Name = "Ada"
counter?.Value += 1
```

These forms evaluate the receiver once, execute the assignment only when the
receiver is non-null, and otherwise do nothing.

### End-to-end example

The following example shows `try`, propagation, and carrier conditional access
working together:

```raven
record class User(Name: string)

union LookupError {
    case Io(message: string)
    case InvalidUser
}

func readNameLength(path: string) -> Result<int, LookupError> {
    let text = try System.IO.File.ReadAllText(path) match {
        Ok(let content) => Ok(content)
        Error(let ex) => Error(.Io(ex.Message))
    }?

    let length = parseUser(text)?.Name?.Length?
    return Ok(length)
}

func parseUser(text: string) -> Result<User, LookupError> {
    if text == "" {
        return Error(.InvalidUser)
    }

    return Ok(User(text))
}
```

## Standard carrier helper APIs (Raven.Core)

Raven ships `Option<T>`, `Result<T, E>`, and related extension helpers in
`Raven.Core`. These are library APIs (not syntax), but they are part of the
standard language experience and are expected by diagnostics, samples, and
tooling.

`Option<T>` and `Result<T, E>` are defined in `Raven.Core` as plain `union`
carriers, so they use Raven's default struct-union representation and follow the
conventional `.NET` union contract.

Raven.Core also provides `System.Text.Json` converters for `Option<T>`,
`Result<T, E>`, and ad-hoc `System.Union<...>` values. These converters prefer plain JSON
when the JSON shape can be recovered from the declared target type. Parenthesized
unions use value-union conversion because their members can be arbitrary types,
including primitives; case-declaration unions may opt into a tagged case format.
See
[Raven Core JSON serialization](../../compiler/json-serialization.md).

### `Option<T>` helpers

- State checks: `HasSome`, `HasNone`
- Mapping/composition: `Map`, `Then`, `Where`, `Filter`, `OrElse`
- Result bridge: `ThenResult`, `MapResult`, `OkOr(error)`,
  `OkOr(errorFactory)`
- Pattern/value helpers: `Match`, `Tap`, `TapNone`
- Unwrap helpers: `UnwrapOrElse`, `UnwrapOrDefault`, `UnwrapOrThrow`,
  `UnwrapOr(defaultValue)`
- Enumeration helpers: `ToEnumerable`, `GetEnumerator`
- Nested carrier helper: `Flatten` for `Option<Option<T>>`
- Nullable conversions:
  - `Option<T : class> <-> T?`
  - `Option<T : struct> <-> T?`

### `Result<T, E>` helpers

- State checks: `HasOk`, `HasError`
- Channel projection: `IsOk`, `IsError`
- Mapping/composition: `Map`, `Then`, `MapError`, `OrElse`
- Pattern/value helpers: `Match`, `Tap`, `TapError`
- Unwrap helpers: `UnwrapOrElse`, `UnwrapOrDefault`, `UnwrapOrThrow`,
  `UnwrapOr(defaultValue)`, `UnwrapError`
- Enumeration helpers: `ToEnumerable`, `GetEnumerator`

### Carrier LINQ extensions on `IEnumerable<T>` (`System.Linq`)

- Option-returning:
  - `FirstOrNone()`, `FirstOrNone(predicate)`
  - `LastOrNone()`, `LastOrNone(predicate)`
  - `SingleOrNone()`, `SingleOrNone(predicate)`
  - `ElementAtOrNone(index)`
- Result-returning with custom errors:
  - `FirstOrError(errorFactory)`, `FirstOrError(predicate, errorFactory)`
  - `LastOrError(errorFactory)`, `LastOrError(predicate, errorFactory)`
  - `SingleOrError(errorFactory)`, `SingleOrError(predicate, errorFactory)`
  - `SingleOrError(errorIfNone, errorIfMany)`
  - `SingleOrError(predicate, errorIfNone, errorIfMany)`
  - `ElementAtOrError(index, errorFactory)`
- Result-returning with captured exceptions:
  - `ToArrayOrException() -> Result<T[], Exception>`
  - `ToListOrException() -> Result<List<T>, Exception>`
  - `ToHashSetOrException() -> Result<HashSet<T>, Exception>`
  - `ToDictionaryOrException(keySelector) -> Result<Dictionary<TKey, T>, Exception>`
  - `ToDictionaryOrException(keySelector, elementSelector) -> Result<Dictionary<TKey, TValue>, Exception>`
- Result-returning with mapped errors:
  - `ToArrayOrError(errorFactory: Exception -> E) -> Result<T[], E>`
  - `ToListOrError(errorFactory: Exception -> E) -> Result<List<T>, E>`
  - `ToHashSetOrError(errorFactory: Exception -> E) -> Result<HashSet<T>, E>`
  - `ToDictionaryOrError(keySelector, errorFactory) -> Result<Dictionary<TKey, T>, E>`
  - `ToDictionaryOrError(keySelector, elementSelector, errorFactory) -> Result<Dictionary<TKey, TValue>, E>`
