# Raven Language Server

The Raven language server provides Language Server Protocol (LSP) support for `.rvn` files, with legacy `.rav` compatibility, so editors can surface diagnostics, completions, and inlay hints while you work. It is hosted inside the `Raven.LanguageServer` project and wraps the Raven compiler workspace to keep documents synchronized with the editor.

## Features
- **Text synchronization:** Opens, changes, saves, and closes documents through `TextDocumentSyncHandlerBase`, storing the latest text in the workspace.
- **Diagnostics:** Publishes Raven diagnostics for the current file after each change, keeping previous semantic results visible while newer snapshot diagnostics are pending when their ranges can be translated safely.
- **Completions:** Maps the compiler's completion items into LSP responses with
  snippet ranges for insertion. `#` triggers context-aware macro completion:
  expression positions offer freestanding and token-tree macros, while
  declaration attributes offer attached macros.
- **Hover symbol projection:** Hover on member-access segments resolves the member symbol for both identifier and access operators (for example `.Name` and `?.Name`), including carrier/conditional-access chains.
- **Hover capture annotations:** Hover on lambdas and nested `func` statements includes captured-symbol lists. Hover on captured locals/parameters marks them as captured variables.
- **Inlay hints:** The server provides inferred type annotation hints and invocation parameter-name hints. Both kinds include source edits where the hint can be applied directly.
- **Document outline:** Namespace-scoped functions, types, constants, and
  `let`/`var` declarations appear as peer members in the editor outline.
  Identifier-bearing declaration macro carriers such as
  `component! Greeting(...) { ... }` appear once under their authored name;
  identifier-less member macro invocations do not invent outline entries.
- **Framework references:** Loads reference assemblies from the latest installed .NET targeting pack so compilations can bind to standard library types without additional setup.

## Project layout
The server code lives in `src/Raven.LanguageServer` and boots from `Program.cs`, which wires up dependency injection, logging, and the LSP handlers. Key components include:
- `DocumentStore`: Tracks opened documents inside a `RavenWorkspace`, seeds framework references, and converts compiler diagnostics to LSP diagnostics.
- `RavenTextDocumentSyncHandler`: Handles open/change/save/close notifications and triggers diagnostics publication.
- `CompletionHandler`: Uses the compiler `CompletionService` to answer LSP completion requests.
- `PositionHelper`: Converts between LSP positions/ranges and Raven text spans.

## Project and file-application contexts

The language server uses evaluated `.rvnproj` membership when an opened source
file belongs to a Raven project. Cross-file lookup, diagnostics, navigation,
and other semantic features then use that project snapshot.

When an opened workspace folder contains one or more standard `.sln` or XML
`.slnx` files, the Raven projects listed by those solutions form the workspace
project group. Relative solution paths may point outside the opened folder.
Solution folders and non-Raven projects are ignored, while project-to-project
compiler references continue to come from evaluated MSBuild `ProjectReference`
items. If no solution contains a usable Raven project, the server recursively
discovers projects as before, so a solution file is never required. Creating,
changing, or deleting a `.sln` or `.slnx` file reloads the group while
preserving open document text.

Opening a file under the workspace also checks its ancestor directories for
projects omitted from the solution. The server loads these on demand and uses
their evaluated compile items to determine ownership, so sibling types and
project references remain available in nested samples. Explicitly excluded
files retain file-based application behavior.

A source file that is not included by an evaluated project is treated as the
root of its own file-based application. It receives an isolated ephemeral
project with the standard Raven prelude, framework references, `Raven.Core`,
and the standard `Raven.Macros` compiler plugin. Other loose files in the same
directory or workspace do not enter that compilation merely because they are
open. This matches `rvn run <file.rvn>` and prevents unrelated scripts from
contributing declarations or entry points to one another.

Closing a standalone root removes its ephemeral project. Future file-based
source directives will extend this project with explicitly resolved documents
rather than reverting to directory-wide implicit inclusion.

## Inlay hints

Raven currently emits two inlay hint categories:

- **Inferred type annotations:** locals, functions, and pattern declarations with omitted type annotations surface inferred `: T` and `-> T` hints. Applicable Raven fragments reported by token-tree macros participate as well, including inferred locals in declaration-shaped block fragments and collection-comprehension targets. For example, `let x = 42` inside a `component!` body displays as `let x: int = 42`. Assignment patterns that deconstruct into existing variables do not receive declaration hints. Applying the hint inserts the annotation into source.
- **Names:** positional invocation arguments surface their resolved parameter name before the argument. For example, `StackPanel(8.0)` displays as `StackPanel(spacing: 8.0)` when `8.0` binds to the `spacing` parameter. Positional and nominal deconstruction patterns also surface inferred element names, so `let (left, top) = point` can display as `let (x: left, y: top) = point` when the source tuple or `Deconstruct` shape provides `x` and `y`. Arguments or elements that already use named syntax do not receive duplicate hints. Applying the hint inserts the `name: ` prefix into source.

The VS Code extension exposes a master setting plus per-category settings:

- `raven.inlayHints.enabled`: enables or disables all Raven inlay hints.
- `raven.inlayHints.inferredTypes.enabled`: enables inferred type annotation hints when the master setting is enabled.
- `raven.inlayHints.names.enabled`: enables invocation parameter-name and deconstruction element-name hints when the master setting is enabled.

The command palette also exposes `Raven: Toggle Inlay Hints`, `Raven: Toggle Inferred Type Inlay Hints`, and `Raven: Toggle Name Inlay Hints`.

Inlay hints are editor presentation, not semantic truth. The compiler still owns
the inferred type and parameter-name answers, while the LSP layer owns request
scheduling and display stability. After an edit, the client debounces broad
inlay requests and the server may answer from cached or available presentation
state for unchanged ranges until the next successful semantic inlay query
replaces it. Small-document requests recompute immediately after committed
edits. Cached hints may be translated across edits that do not intersect the
hint range; edits inside a hint's source range drop that hint instead of
presenting stale text.

## Diagnostics publication

The language server follows the live semantic-model architecture described in
[Live semantic model](architecture/live-semantic-model.md): the compiler owns
semantic truth, while the LSP layer schedules, cancels, and presents versioned
results.

Diagnostics are versioned by editor document version. Every
`publishDiagnostics` notification should carry the version it was computed for
so the client can reject stale results after a later edit.

After ordinary edits, `RavenTextDocumentSyncHandler` publishes diagnostics from
explicit lanes:

- `Syntax`: syntax diagnostics from the current open document snapshot.
- `DocumentCompiler`: compiler diagnostics for the current document only.
- `ProjectCompiler`: compiler diagnostics for the project, filtered back to the current document.
- `ProjectWithAnalyzers`: project diagnostics including analyzer diagnostics, filtered back to the current document.

Background project analysis reserves semantic access without waiting before
running compiler diagnostics or analyzer callbacks. If a document is busy, the
run is incomplete and can be retried after the foreground request releases it.
Incomplete results do not replace the project analyzer cache.

Open, edit, and save use only the active editor lanes: `Syntax` immediately,
then `DocumentCompiler` as a follow-up. This gives the editor a fast replacement
diagnostic set for the new buffer while binder work completes in the background.
If the diagnostic values are unchanged but the document version changed, the
server still republishes them for the new version.

Syntax publication must not clear still-valid semantic diagnostics just because
the newer semantic snapshot is not ready. When possible, the dispatcher keeps
the last successfully computed diagnostics visible by translating their ranges
from the previous source text to the current source text. This applies to
compiler and analyzer diagnostics independently, so a syntax-only publish can
refresh parser errors without emptying the Problems list for semantic results
that are still being recomputed.

Translated diagnostics are a presentation bridge only. If an edit intersects a
previous diagnostic span, that diagnostic is not carried forward. For example,
editing `default!` to `default` intersects the `RAV0403` span for `<expr>!`, so
the nullable-suppression diagnostic is removed immediately instead of sticking
to unrelated text. Editing another area of the file, such as changing `42` to
`42 + 2`, should keep an unchanged `default!` diagnostic visible until the
document compiler lane publishes the fresh snapshot diagnostics.

Project-wide and analyzer-heavy lanes should not run as part of the active
editor feedback loop unless they are explicitly surfaced through a separate
background path. Slow semantic diagnostics should be fixed in the compiler and
binder pipeline, but they must not leave stale diagnostics visible for an edited
document.

Skipped, canceled, or failed analyzer/background diagnostic work should be
treated as not ready, not as an empty diagnostic result. Previously published
analyzer diagnostics remain visible until a successful analyzer lane for the
current snapshot replaces them.

## Prerequisites
- .NET 9 SDK on your `PATH`.
- Reference assemblies available from a .NET targeting pack (the server attempts to resolve the latest installed version automatically).

## Building
Restore dependencies and build the server project from the repository root:

```bash
dotnet restore src/Raven.LanguageServer/Raven.LanguageServer.csproj
dotnet build src/Raven.LanguageServer/Raven.LanguageServer.csproj
```

## Running
The server expects to be launched by an LSP client (for example, the Raven VS Code extension) and communicates over standard input/output. To run it manually for debugging:

```bash
dotnet run --project src/Raven.LanguageServer/Raven.LanguageServer.csproj --
```

The process will wait for LSP messages on stdin and emit responses to stdout.

## Logging
Raven language-service troubleshooting uses two different log sinks:

- **Server log:** [Program.cs](/Users/marina/Projects/Raven/src/Raven.LanguageServer/Program.cs) resolves the repository root and writes the language server's file log to `logs/raven-lsp.log`.
- **VS Code client log:** [extension.ts](/Users/marina/Projects/Raven/src/Raven.VSCode/src/extension.ts) writes lifecycle, request-start, request-complete, and request-failure lines such as `[lifecycle ...]` to the VS Code `Raven` output channel.

When investigating missing diagnostics, hover cancellations, or completion/definition issues, capture both:

1. `logs/raven-lsp.log` from the repository root.
2. The `Raven` output channel contents from VS Code.

The two logs answer different questions:

- `logs/raven-lsp.log` shows what the server process actually handled, including parser/diagnostics exceptions, code-action failures, and server startup/shutdown.
- The `Raven` output channel shows what the client requested and whether requests were canceled before the server answered.

## Headless edit probes

Use `tools/Raven.LanguageServer.Headless` to reproduce editor edit flows without
VS Code. Important recovery scenarios include wrapping existing top-level
statements in `func Main() { ... }`, either by typing the opening wrapper and
later adding the closing brace or by creating an empty block and pasting the
statements into it. These probes should compare the first semantic query after
the structural edit with a follow-up body edit inside the new function owner.

When a probe reports slow hover, inlay, or diagnostics, inspect the semantic
counters before changing LSP behavior. `symbolBinderFallbacks`,
`typeBoundFallbacks`, `boundBindFallbacks`, and `boundContextHits` indicate that
the compiler API had to bind or contextually rebind. If the compiler can answer
from sound available state, fix that path in `Raven.CodeAnalysis`; if it cannot,
the authoritative bind path should still produce the correct result.

Large full-document inlay requests are background presentation work and should
avoid cold expensive binding fallbacks. They may use cached or available
compiler state and return fewer hints. Small full-document, precise, and
visible-range inlay requests may use the authoritative binding path so the
editor can fill in missing hints without blocking broad scrolling requests.
Full-document inlay responses should prioritize labels and source-applicable
text edits over tooltip markdown. Focused range requests can include richer
tooltip content because they are tied to the user's current view or cursor.

Analyzer exceptions appear as warning-level workspace events even when they
fail immediately. `documentAnalyzer.failure` records the analyzer type, callback
phase, exception and stack trace, project, and document. Each analyzer emits at
most one detailed failure event per callback phase per document analysis run;
summary counters retain the total number of failures. The run summary uses
`outcome=completedWithFailures` when a callback or initialization failed. Healthy
analyzers continue to run. Cancellation remains cancellation and is not logged
as an analyzer failure.

Document and project analyzer results carry an explicit completion status.
Incomplete results are not stored in the analyzer caches or published to the
editor. The existing retry scheduler keeps the last valid analyzer diagnostics
visible while analysis is unavailable; a successful retry replaces them,
including clearing warnings that no longer apply. Successful sub-results can
still be reused when retrying a failed project analysis. The public workspace
`GetDiagnostics` API also caches only completed analyzer runs, so a transient
analyzer failure does not prevent recovery on the same project snapshot.

Analyzer diagnostics include `Raven.AnalyzerName` in their properties and are
presented with the `raven-analyzer` source. This origin metadata lets the editor
preserve external analyzer warnings during compiler-only refreshes without
relying on an analyzer-specific diagnostic ID prefix.

Project-wide analyzer execution includes compilation trees produced by source
generators, even when they have no authored workspace document. Each generated
tree uses the same analyzer driver and source suppression rules as authored
code. Compilation callbacks still run once per project, and a failure in a
generated tree prevents caching an incomplete project result.

Analyzers can call `context.ConfigureGeneratedCodeAnalysis(...)` during
initialization. `GeneratedCodeAnalysisFlags.None` skips callbacks on generated
trees and filters analyzer diagnostics located there; `Analyze` enables callbacks,
and `ReportDiagnostics` enables reporting. Combine the flags to enable both.
Compilation callbacks still execute, and their diagnostics follow the same
location-based reporting policy. Diagnostics without a source location are not
filtered by this policy. Unconfigured analyzers retain both flags by default.

This policy identifies source-generator output through compiler-owned provenance,
retained across text changes. It also recognizes file names ending in `.g.rvn`,
`.g.i.rvn`, `.generated.rvn`, or `.designer.rvn`, and names beginning with
`TemporaryGeneratedFile_` (case-insensitive). Leading comments containing
`<auto-generated` or `<autogenerated` also mark a tree as generated; these markers
are case-sensitive. Markers in code or comments after the first token do not count.
Header classification is cached per immutable tree and recalculated after edits,
so removing a header restores normal analysis unless another generated indicator
still applies.

`System.CodeDom.Compiler.GeneratedCodeAttribute` marks a source type or member as
generated for symbol, syntax-node, and operation callbacks. The classification
also applies to members nested under an attributed type and to analyzer
diagnostics whose source location is inside the generated declaration. A partial
symbol with multiple source declarations remains non-generated, even when one
part carries the attribute, while an individually attributed member remains
generated. Compiler diagnostics and source suppression directives retain their
existing behavior. Configuration is published only after analyzer initialization
succeeds.

Per-file `.editorconfig` entries can explicitly set `generated_code = true` or
`false`. These values override filename and header conventions and are carried in
the immutable compilation snapshot used by analyzer callbacks. A watched
`.editorconfig` change updates project options, invalidates cached analyzer
results, and republishes diagnostics for affected open documents without a full
project reload.
