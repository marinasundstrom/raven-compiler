# Generator delegation

This project demonstrates `yield from` as a statement and as a `unit` expression:

- Lazy synchronous delegation producing `42, 1, 2, 3`.
- Asynchronous delegation with awaited cleanup.
- Early `await for` exit disposing both iterator levels.
- A consumer token forwarded through `[EnumeratorCancellation]` to the inner source.

Build and run with the repository toolchain using:

```sh
scripts/build-project-samples.sh yield-from
scripts/run-project-samples.sh yield-from
```

With the current Raven SDK installed, run from this directory:

```sh
dotnet run --project YieldFrom.rvnproj --property WarningLevel=0
```

Each completed or interrupted source prints its disposal message. The early
exit and cancellation cases produce `42, 1` and do not print `delegation completed: ()`.
The cancellation case prints `async source disposed` before `cancelled`.
