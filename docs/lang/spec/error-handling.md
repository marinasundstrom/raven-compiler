# Exceptions and structured handling

Raven uses exceptions for unexpected failures and `Result<T, E>` or
`Option<T>` for failures and missing values that are part of an operation's
ordinary outcome.

```raven
func load(input: string) -> Result<Model, ContextError<ParseError>> {
    return parse(input).WithContext("Loading the model")
}
```

When an error value already describes the failure correctly, `WithContext`
adds the operation that was in progress and preserves the original error as
its cause. Use `MapError` when a boundary needs to translate one error model
into another. See [Error propagation and carrier
types](async-and-error-propagation.md) for `Result`, `Option`, `?`, `?.`, and
exception-capturing `try` expressions.

## Throwing an exception

`throw expression` stops the current path and propagates an exception outward.
The operand must derive from `System.Exception`; an incompatible operand reports
`RAV1020`.

```raven
func requireName(name: string?) -> string {
    return name ?? throw ArgumentException("Missing name")
}
```

Raven supports `throw` both as a statement and as an abrupt expression. The
expression form can appear in an `if` or `match` branch, a null-coalescing
operand, or another value position. It does not contribute a type because that
path never completes normally.

### Throw rules

* Statement-form `throw` is valid only in a statement context. Using it directly
  in an inline expression context reports `RAV1907`; use the expression form
  there.
* A `use` declaration in a scope is disposed before an exception leaves that
  scope.
* Reserve exceptions for exceptional conditions. Prefer a specific error type
  in `Result<T, E>` when callers are expected to handle the failure routinely.

## Handling exceptions with `try`

A `try` statement protects a block and handles exceptions with one or more
`catch` clauses. An optional `finally` clause runs whenever control leaves the
statement.

```raven
try {
    operation()
} catch FormatException ex {
    Console.WriteLine($"Bad input: {ex.Message}")
} finally {
    cleanup()
}
```

A catch clause may use an exception type pattern and an optional `when` guard:

```raven
try {
    operation()
} catch (Exception ex) when ex.StatusCode == 2 {
    Console.WriteLine($"Retriable failure: {ex.Message}")
}
```

Catch clauses are considered in source order. The first matching type pattern
whose guard succeeds handles the exception. A bare `catch` is equivalent to
`catch (System.Exception)`.

### Try-statement rules

* A `try` statement must contain at least one `catch` or a `finally`; omitting
  both reports `RAV1015`.
* A catch type must be `System.Exception` or a derived type. An incompatible
  type reports `RAV1016`.
* Parentheses may group the catch pattern.
* The supported runtime pattern is currently an exception type pattern such as
  `catch FormatException ex`. Richer non-type primary patterns are diagnosed
  until full catch-pattern semantics are available.
* `finally` runs whether the `try` block or a `catch` completes normally or
  leaves through an early control transfer.

## Capturing exceptions as values

Use `try expression` to capture a throwing API as `Result<T, Exception>`, or
`(try expression)?` to capture and immediately propagate the failure through an
enclosing carrier. The complete rules and diagnostics are in [Error propagation
and carrier types](async-and-error-propagation.md#try-expressions).
