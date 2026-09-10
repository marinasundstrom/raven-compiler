# Raven.CodeAnalysis.Tests

This project is the compiler coverage hub. Keep the default path fast and focused on syntax, binding, semantic model, diagnostics, symbol shape, operations shape, and project model behavior that must stay correct for Raven code to compile.

Runtime, reflection, generated IL, process, NuGet, MSBuild, and sample-project coverage is still useful, but it is secondary coverage. Keep it isolated from the baseline unless it is the only practical way to prove a compiler behavior.

## Test Tiers

| Tier | What belongs here | Command |
|---|---|---|
| Required CI | Bounded build plus critical compiler, Raven.Core, and language-server unit contracts | `scripts/test-ci.sh` |
| Baseline | Broad syntax and semantic coverage for local stabilization | `scripts/test-baseline.sh` |
| Feature suite | Focused area checks after changing a feature | `scripts/test-feature-suite.sh <suite>` |
| Runtime isolated | CodeGen, sample, reflection, emitted-assembly, and project-heavy integration tests | `scripts/test-runtime-isolated.sh` |
| Feature runtime | Runtime/emission overlap for one area | `scripts/test-feature-suite.sh <suite> --runtime` |
| Language-server integration | Opt-in project-backed language-server workspace, document-store, analyzer-lane, sample, and headless edit scenarios | `scripts/test-language-server-integration.sh` |
| Language-server perf | Opt-in language-server latency and interaction-budget checks | `scripts/test-language-server-perf.sh` |
| Samples | End-to-end sample project build after compiler/runtime changes | `FORCE_REBUILD=1 samples/build.sh` |

Main CI runs the required tier for pull requests targeting `main`, pushes to
`main`, and manual dispatches. It does not pack SDK
artifacts or run CodeGen/runtime, subprocess, CLI, NuGet/MSBuild project-system,
sample, language-server integration, or performance tests. Those suites are
valuable but too expensive or environment-sensitive to block every integrated
commit.

The required compiler contract selection lives in `scripts/test-ci.sh`. Keep it
small and intentional. A test belongs there only when it protects one of these
cross-cutting contracts:

- syntax-tree/parser integrity;
- core binding, overload, import, propagation, and conditional-access behavior;
- semantic caching and incremental document diagnostics;
- compiler diagnostic identity;
- completion behavior consumed by editors;
- Raven.Core public contracts;
- in-process language-server request and presentation behavior.

Feature-specific regressions should normally be added to their owning focused
suite, not automatically promoted to required CI. Promote one only when a
failure would invalidate the compiler/editor contract as a whole and the test
is deterministic, in-process, and fast.

All ad hoc `dotnet test` commands should use `/property:WarningLevel=0`.
Use `docs/testing/test-impact-map.md` to choose the smallest pre-change
baseline and post-change validation set for the compiler area being touched.

The full baseline can take around 20 minutes on a typical local machine. Treat it as a broad local stabilization gate, not a required CI job or the first tool for every edit: start with the smallest matching feature suite or test-class filter, then broaden to the baseline when the change affects shared compiler behavior. Part of the cleanup goal is to keep refactoring and reorganizing tests so selective runs become more accurate, faster, and easier to choose.

The full sample build is also a smoke gate, not a default inner-loop command: `FORCE_REBUILD=1 samples/build.sh` can take roughly 5-6 minutes because it recompiles Raven.Core/compiler dependencies and every sample, while `samples/run.sh` usually finishes in seconds once the sample DLLs are built. Prefer targeted sample filters when validating one language area, then run the full sample build/run pass for compiler/runtime stabilization.

Run suites because their owning behavior changed, not because they are nearby:

| Touched area | Usual validation |
|---|---|
| Parser, binder, semantic model, symbol display, diagnostics, operations | Focused `Raven.CodeAnalysis.Tests` class/filter or matching feature suite |
| Compiler driver, Raven project loading, MSBuild targets, target frameworks, references | Targeted project-loading/compiler-driver tests, then sample build only when compatibility changed |
| Emit, lowering, runtime execution, metadata shape, reflection | Focused runtime/CodeGen test or isolated runtime suite |
| Language-server handlers, document store, workspace manager, request scheduling, presentation formatting | Focused `Raven.LanguageServer.Tests` filter |
| Project-backed language-server workspace loading, analyzer lanes, document-sync scheduling, sample-backed LSP scenarios | Focused `Raven.LanguageServer.Integration.Tests` filter |
| Language-server latency budgets or metrics thresholds | `scripts/test-language-server-perf.sh` only |
| Console editor behavior | `test/Raven.Editor.Tests` only |

Do not run `Raven.LanguageServer.Tests` merely because a language feature changed. Run it when the change touches language-server code, editor-facing presentation, or a bug was observed only through the language-server path and needs an LSP-specific guard.

## Coverage Gaps

A skipped test is not covered by the baseline. Keep skipped tests visible and either restore them as fast syntax/semantic coverage, move them into isolated runtime coverage, or delete/replace them when they are stale.

Enabled runtime tests must execute their output assertions. Runtime helpers must propagate execution failures, including missing runtime members, rather than returning a sentinel that lets the test pass without checking behavior. `MatchExpressionCodeGenTests` follows this rule, including list middle-rest captures with empty and non-empty slices and inputs too short to match.

## Project Boundaries

Keep compiler and editor-adjacent coverage in the project that owns the behavior:

| Project | Owns |
|---|---|
| `test/Raven.CodeAnalysis.Tests` | Compiler API, syntax, binding, semantic model, diagnostics, symbols, operations, project loading, and reduced compiler regressions from samples |
| `test/Raven.LanguageServer.Tests` | Language-server request handling, document snapshots, diagnostics publication, hover/completion/inlay presentation, request cancellation, and VS Code-facing workspace behavior |
| `test/Raven.LanguageServer.Integration.Tests` | Opt-in project-backed language-server workspace, document-store, analyzer-lane, sample, and headless edit integration scenarios |
| `test/Raven.LanguageServer.Perf.Tests` | Opt-in language-server metrics, latency budgets, and performance instrumentation; do not include in baseline behavior gates |
| `test/Raven.Editor.Tests` | The `Raven.Editor` console editor only; do not use this project or namespace for language-server or VS Code integration coverage |

When a language-server failure exposes a compiler semantic bug, reduce it into `Raven.CodeAnalysis.Tests` first, then keep a narrow `Raven.LanguageServer.Tests` guard only for the language-server path that made the bug visible.

Nested async lambda capture coverage is active in
`AsyncFunctionExpressionStateMachineTests`, including direct invocation and
`Task.Run` of a stored delegate. Run it with
`dotnet test test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj --filter 'FullyQualifiedName~.AsyncFunctionExpressionStateMachineTests.' /property:WarningLevel=0`.

Async resource lifetime is covered by the active runtime class
`AsyncResourceLifetimeCodeGenTests`. It replaces the old skipped emit-only case
in the excluded `AsyncTryAwaitCodeGenTests` class, using a controlled incomplete
task to verify disposal before completion and reverse cleanup order for return,
exceptions, and cancellation. It also replaces the return-type-only disposal
check from `AsyncPropagateCodeGenTests`, verifying `?` carrier propagation and
`try? await` exception capture across suspension, including success payloads,
error payloads, skipped continuation code, and resource cleanup. Run this slice with
`dotnet test test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj --filter 'FullyQualifiedName~.AsyncResourceLifetimeCodeGenTests.' /property:WarningLevel=0`.

Expression-body runtime coverage is active in `ExpressionBodyCodeGenTests`: it
executes constructor and method side effects, local-function captures, and both
populated and empty option results from local functions, property getters, and
expression-bodied properties. Run this slice with
`dotnet test test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj --filter 'FullyQualifiedName~.ExpressionBodyCodeGenTests.' /property:WarningLevel=0`.

Member-binding runtime coverage is active in `MemberBindingCodeGenTests`. It
checks target-typed static fields, extension-property success and empty results,
runtime type identity for reference and value receivers, reflection predicates,
and generic error-mapping lambdas on success and failure. Run this slice with
`dotnet test test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj --filter 'FullyQualifiedName~.MemberBindingCodeGenTests.' /property:WarningLevel=0`.

By-reference alias runtime coverage is active in `ByRefCodeGenTests`. It checks
explicit and inferred alias locals, reads and writes through `&Type` and `ref`
parameters, and forwarded generic swaps of value and reference types. Run this
slice with
`dotnet test test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj --filter 'FullyQualifiedName~.ByRefCodeGenTests.' /property:WarningLevel=0`.

Current explicit gaps:

| Area | Gap | Preferred cleanup |
|---|---|---|
| Reference assembly diagnostics | file-scoped code and missing-main diagnostics require reference assemblies in some environments | Make the harness provide stable references or rewrite as compiler-only diagnostics |
| Language server coverage | request/hover/workspace integration tests can run for minutes under the baseline runner | Split fast request/mapper/semantic presentation tests from workspace integration tests, then guard the latter separately before restoring them to a default gate |
| Runtime CodeGen | stale runtime/reflection/emitted-shape classes are excluded from `scripts/test-runtime-isolated.sh`: `AsyncPropagateCodeGenTests`, `AsyncTryAwaitCodeGenTests`, `FunctionExpressionCodeGenTests`, `GenericInvocationCodeGenTests`, `AttachedMacroCodeGenTests`, `PdbSequencePointTests`, `PrimaryConstructorParameterCodeGenTests`, `ProjectFileNuGetReferenceTests`, `PropertyTests`, `RuntimeAsyncCodeGenTests`, `RuntimeSymbolResolverTests`, `TryExpressionCodeGenTests`, `TypeOfExpressionCodeGenTests`, `TypeResolutionPrecedenceTests`, `UnionCodeGenTests` | Reintroduce only focused runtime behavior checks; avoid emitted instruction/lowered shape assertions |
| Project/CLI runtime | `MsBuildSampleProjectCompilationTests` can trip the runtime hang guard through `rvn`/MSBuild sample compilation | Replace with a bounded CLI smoke test or rely on `FORCE_REBUILD=1 samples/build.sh` for sample coverage |

Positional/tuple pattern coverage is active in the runtime tier. The restored
`PositionalPatternCodeGenTests` execute tuple matches, immutable and mutable
declarations, and assignments to existing locals, and verify fallback behavior
for mismatched tuple lengths, element types, and non-tuple values. Run this slice
with:

```bash
dotnet test test/Raven.CodeAnalysis.Tests/Raven.CodeAnalysis.Tests.csproj --filter 'FullyQualifiedName~.PositionalPatternCodeGenTests.' /property:WarningLevel=0
```

## Area Runs

Use `scripts/test-feature-suite.sh --list` to see curated feature suites. Pick the smallest suite that matches the changed behavior:

| Changed area | Start with |
|---|---|
| Unions, union conversion, union nullability, C# union interop | `scripts/test-feature-suite.sh unions` |
| Parser, syntax nodes, trivia, recovery | syntax or parser test class filters, then `scripts/test-baseline.sh` |
| Function calls, overload resolution, optional/named/params args, callable `self` | `scripts/test-feature-suite.sh overload-resolution` |
| Functions, lambdas, async/await, async lowering | `scripts/test-feature-suite.sh functions-async` |
| Match, `is`, pattern variables, exhaustiveness | `scripts/test-feature-suite.sh patterns` |
| Extension declarations, extension lookup, metadata extension members | `scripts/test-feature-suite.sh extensions` |
| Partial types and members | `scripts/test-feature-suite.sh partials` |
| Macros and macro-expanded documents | `scripts/test-feature-suite.sh macros` |
| Imports, aliases, namespaces, escaped identifiers, multi-file lookup | `scripts/test-feature-suite.sh imports-and-namespaces` |
| Target frameworks, reference assembly paths, Raven project targeting | `scripts/test-feature-suite.sh framework-and-targeting` |

After a source change that can affect emitted IL, also run the matching `--runtime` suite or `scripts/test-runtime-isolated.sh`. After changes that affect project loading, Raven.Core, the compiler driver, or sample compatibility, run `FORCE_REBUILD=1 samples/build.sh`.

## Cleanup Rules

When a test fails, first classify it:

1. Compiler behavior is wrong: fix the compiler and keep or add focused coverage.
2. The test asserts stale syntax, symbol display, diagnostic wording, cache identity, lowered shape, or emitted instructions: rewrite it to assert stable language behavior or delete it if it no longer protects anything.
3. The test uses reflection, subprocesses, emitted assemblies, NuGet restore, MSBuild, or samples: move it to the isolated runtime path unless it is a small, crucial end-to-end guard.

Avoid baseline tests that can hang. If runtime/reflection coverage is required, keep it narrow, guarded by the isolated scripts, and separate from fast syntax/semantic coverage.

## Source Fixtures

Prefer inline Raven source when the syntax under test is small and the exact shape is part of the assertion. A local snippet usually makes simple `if`, `for`, `match`, lambda, or declaration tests easier to read than a named fixture.

Use shared Raven source fixtures selectively when the same small setup appears across several suites, especially reusable declarations such as `Option<T>`, `Result<T>`, record/property-pattern models, metadata-like helper extensions, or a canonical control-flow shape that syntax, semantic, operation, and diagnostic tests must intentionally keep aligned.

Do not turn complex samples into fixtures. When sample code reveals a gap, reduce it into a feature-owned test that keeps the relevant structure visible; leave broad sample compositions to the sample build/run smoke gates.

Fixtures should provide source text, not hide the assertion target. Keep the statement or expression being tested visible in the test unless the whole point is to verify one shared canonical source shape across multiple compiler layers.
