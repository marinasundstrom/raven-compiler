# Async Samples Matrix

These samples are intentionally split by feature combinations.
Keep them when the combination is distinct; merge only true duplicates.

## Simple first
- `async-await.rav` -> basic `async` + `await`
- `async-task-return.rav` -> async function returning `Task<T>`
- `async-valuetask.rav` -> async function returning `ValueTask` / `ValueTask<T>`

## Inference and generics
- `async-await-inference.rav` -> async lambda + `await` inference
- `async-generic-task-return.rav` -> generic async return (`Task<T>`)

## Result and pattern combinations
- `async-result-workflow-basic.rav` -> async workflow returning `Task<Result<T,E>>`
- `async-try-match-expression.rav` -> `try await ... match` composition

## Resource management and exceptions
- `async-file-io.rav` -> async file API usage
- `async-use-dispose-async-preferred.rav` -> async `use` prefers `DisposeAsync()` and falls back to `Dispose()`
- `async-try-catch.rav` -> `await` inside `try/catch`

## HTTP combinations
- `http-client.rav` -> `HttpClient` + exception handling
- `http-client-result.rav` -> `HttpClient` + `Result` via `try/catch`
- `http-client-result-extension.rav` -> extension method returning `Result`
- `http-client-result-propagation.rav` -> async propagation (`(try expression)?`) with HTTP
