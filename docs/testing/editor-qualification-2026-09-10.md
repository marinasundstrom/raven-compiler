# Editor qualification — 2026-09-10

## Result and scope

The headless language-server integration and performance tiers pass: **345
unique integration cases and 18 performance cases**, with no remaining skips.
The integration result combines class-sized batches and focused retries after
fixes; it is not one uninterrupted whole-project run at the final revision.

This qualifies the tested workspace and handler paths on this machine. It does
not qualify VS Code client lifecycle, UI rendering, extension packaging, or all
supported platforms. No live VS Code failure was reproduced or client log
captured in this pass. Headless requests, assertions, and server instrumentation
were retained in local test results.

## Provenance

- Starting revision: `a7f6f7f5e`.
- Final implementation revision: `769ca3dc9`.
- Host: macOS, arm64.
- SDK: `11.0.100-preview.7.26381.103`.
- Integration and performance test target: `net10.0`.
- Language server build: both `net10.0` and `net11.0`, no errors or warnings.
- Repository compiler and support assemblies were used. The initial integration
  build rebuilt the .NET 10 compiler/support dependency chain, including macros.

## Completed slices

### Executable edit-recovery fixture (`72b472f69`)

The structural wrapper test starts with top-level statements, wraps them in
`func Main`, edits the body, and unwraps them again. Its temporary project
previously defaulted to a library, so the restored top-level source correctly
received “Only console applications may contain file-scope code.” The fixture
now requests executable output. All 12 headless edit-simulation cases pass.

### Watched macro consumer notifications (`769ca3dc9`)

A watched-file `Changed` event for a closed macro source updated the open
consumer's expansion from `1` to `2`, but `ReloadForWatchedFilesAsync` returned
an empty document list. `DidChangeWatchedFilesHandler` uses that list to publish
diagnostics, so the consumer was omitted from publication.

The workspace now includes projects whose versions changed while applying the
file events, including consumers whose macro references were replaced. The
previously skipped regression is enabled and checks both the current expansion
and the returned consumer URI. Repeating the notification without changing the
source produces no refresh work. The workspace and document-synchronization
batches pass all 149 cases after this fix.

## Coverage

| Integration class | Passing cases |
| --- | ---: |
| `LanguageServerSnapshotConsistencyTests` | 79 |
| `HeadlessEditSimulationTests` | 12 |
| `AnalyzerDiagnosticRecoveryTests` | 7 |
| `LanguageServerDiagnosticsTests` | 53 |
| `LanguageServerInlayHintTests` | 45 |
| `RavenTextDocumentSyncHandlerTests` | 85 |
| `WorkspaceManagerTests` | 64 |
| **Total** | **345** |

Coverage includes hover and completion after edits, malformed-code recovery,
cross-file changes, diagnostic/analyzer scheduling and recovery, inlay hints,
workspace reloads, macro consumers, and repository sample scenarios. The edit
matrix includes hello-world, conditional-compilation, top-level-members, and
repository-result-patterns. Other handler tests exercise ASP.NET and EF Core
samples.

All 18 cases in `Raven.LanguageServer.Perf.Tests` pass. One large-project body
edit measured a first hover of 590.7 ms and a repeat hover of 1.4 ms. These are
single-machine observations, not a platform-wide latency guarantee.

## Reproduction and evidence

Run an individual integration class or the performance tier with:

```bash
scripts/test-language-server-integration.sh --filter 'FullyQualifiedName~<class>'
scripts/test-language-server-perf.sh
```

Both scripts
set `WarningLevel=0` and a 300-second inactivity watchdog. After the initial
build, this pass used `--no-build --no-restore` for unchanged batches and
`--no-restore -p:BuildProjectReferences=false` for test-only rebuilds.

TRX files and per-class logs were saved locally under
`/tmp/raven-editor-qualification`. The initial failing wrapper diagnostic is
also recorded in `/tmp/raven-editor-wrapper-before.log`; the macro event's
empty refresh list is recorded in `/tmp/raven-editor-watched-before.log`.
These temporary paths are local evidence, not checked-in release artifacts.

The next qualification work is the sample build/run corpus, target-framework
matrix, and IL checks in [Release and bootstrap gates](release-and-bootstrap-gates.md).
