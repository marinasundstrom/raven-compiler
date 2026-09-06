# WebAssembly direction

Raven has two independent WebAssembly goals:

1. Host `Raven.CodeAnalysis` in a WebAssembly environment so tools can parse,
   bind, inspect, and emit Raven programs without a server-side compiler.
2. Run Raven-produced programs in a WebAssembly environment.

Neither goal depends on the other. A playground may integrate both, but compiler
hosting and executable targeting have separate compatibility contracts and
tests.

For executable targeting, WebAssembly is an umbrella deployment story rather
than one host. Browser WebAssembly and WASI are sibling targets because they
expose different capabilities even when both use the .NET Mono runtime.

| Target | Status | Host contract |
| --- | --- | --- |
| `browser-wasm` | Experimental | Browser JavaScript, the DOM, and web APIs through .NET JavaScript interop. |
| `wasi-wasm` | Investigation | Capabilities supplied by a WASI runtime or component host; no DOM or implicit browser JavaScript. |

The first application slice is the framework-free [Browser WebAssembly
application](browser-wasm.md). It produces the normal .NET browser bundle: a
WebAssembly-hosted runtime plus managed Raven assemblies and static assets. It
is not yet a direct Raven-to-Wasm backend or a single native Wasm component.
Those are separate future compilation and AOT questions.

## Compiler hosting

The compiler must not assume that loaded runtime assemblies have physical file
locations or that `TRUSTED_PLATFORM_ASSEMBLIES` is populated. A WebAssembly host
supplies the framework metadata references that define its supported compilation
surface. References may be downloaded as static assets and materialized in the
host's virtual filesystem through `MetadataReference.CreateFromImage`.

Compiler metadata inspection in WebAssembly should use portable ECMA-335
readers instead of runtime assembly-loading APIs when only an assembly identity
or metadata is needed. Runtime reflection features that are unavailable in
WebAssembly should fall back to explicit metadata when doing so preserves
correct compiler behavior.

An initial browser-hosted probe has demonstrated:

- loading `Raven.CodeAnalysis` in .NET WebAssembly;
- parsing Raven source;
- binding a `System.Console.WriteLine` call against explicitly supplied
  framework metadata; and
- emitting a managed console assembly to an in-memory stream.

The reference bundle is a host concern. `src/Raven.Playground` embeds Raven.Core
and the .NET targeting-pack reference closure selected by MSBuild at build time
rather than guessing an installed targeting-pack patch directory in the
browser. It also uses the same generated standard prelude as `rvnc`, so ordinary
imports and Raven.Core cases have the same compiler-visible surface in both
hosts. The browser runtime's `System.Private.CoreLib` remains available for
execution. Reducing the reference closure is a future payload optimization;
correctness currently takes precedence over a hand-maintained reference
shortlist.

## Executable targeting

Raven currently emits managed CLI assemblies. The first WebAssembly executable
target is therefore the .NET WebAssembly runtime: Raven emits ordinary managed
assemblies, and .NET interprets or compiles their IL for browser or WASI
execution.

An independent browser-hosted probe has demonstrated that a console assembly
produced ahead of time by `rvnc` can be loaded and executed under .NET
WebAssembly without loading the Raven compiler.

`src/Raven.Playground` now combines the two independently owned paths in a
static Blazor WebAssembly application:

- Monaco hosts the source editor.
- Raven's existing TextMate grammar supplies the initial lexical highlighting.
- Monaco completion is supplied by a browser-hosted Raven `AdhocWorkspace` that
  advances the document through ordinary immutable solution snapshots and calls
  the public compiler completion API. Member completion is requested after a
  short pause in a dotted member prefix, avoiding uncancelable compiler work on
  every ordinary keystroke; `Ctrl+Space` requests global completion explicitly.
- **Compile** diagnoses and emits the workspace's current managed compilation,
  reusing semantic state already established by editor requests when possible.
- **Run** passes that assembly to a separate runner and captures its console
  output. Repeated Compile/Run commands on the same immutable compilation
  snapshot reuse the emitted assembly.

Compiler-backed hover and compiler-produced semantic highlighting remain later
editor-service layers; they are not part of the TextMate integration.

Build and publish the playground with:

```bash
dotnet publish src/Raven.Playground/Raven.Playground.csproj \
  -c Release \
  -o artifacts/playground
```

The deployable site is `artifacts/playground/wwwroot`. It can be served by any
static file host. The app uses a relative base path so it can run at a host root
or below a path such as `/raven/playground/`. The host must serve `.wasm` files
as `application/wasm`.

The official GitHub Pages workflow builds the documentation first, then runs
`scripts/build-playground-site.sh` to add the playground at
`_site/playground/`. Both surfaces are uploaded and deployed as one atomic
Pages artifact.

The playground's **Share** command encodes the current UTF-8 source as an
unpadded base64url value in the `source` query parameter, updates the current
URL, and copies it when the browser permits clipboard access. A valid shared
source takes precedence over the default Hello World example when the app
starts. Invalid or oversized values are ignored and the normal default example
is loaded.

Playground examples live as individual `.rvn` files under
`src/Raven.Playground/wwwroot/examples/` and are registered in
`examples/index.json`. Each example should demonstrate one notable Raven feature
in a small real-world context. Fundamental syntax may appear as part of that
story, but examples should not read like isolated compiler test cases.

Run the end-to-end browser smoke test with:

```bash
scripts/test-playground-browser.sh
```

The test publishes a release build, serves only its static `wwwroot` output,
and uses headless Chromium to verify the initial Hello World source, share-link
round trips, Monaco startup, TextMate tokenization, semantic member completion
and insertion, compiler diagnostics, Raven.Core result construction,
synthesized record equality, emitted-assembly loading, and execution of every
registered example. Its first run installs the pinned Playwright Chromium
build.

The test runs Hello World in a cold compiler worker before other editor queries
can initialize declarations. The website workflow runs this suite against the
published Playground files before deployment, so a successful static build alone
cannot allow a broken browser compiler to be published.

Browser and WASI hosts expose different platform APIs. Target profiles should
describe those capabilities explicitly, and unavailable APIs should be handled
through normal target-framework reference surfaces and compiler diagnostics.
Direct emission of native WebAssembly, if pursued, is a separate backend and is
not implied by the managed WebAssembly target.

## WASI as a host story

WASI is principally a contract between a module and its host. Selecting the
`wasi-wasm` runtime identifier does not make the entire .NET API surface
portable. Files, sockets, clocks, randomness, environment variables, processes,
threads, and other platform services need an implementation from the host or a
clear unavailable-operation diagnostic.

A Raven WASI design therefore needs to settle:

- how a project selects a WASI world, runtime, and required capabilities;
- which .NET APIs are supported, adapted, or rejected for each host profile;
- how packages and Raven.Core avoid assumptions about an operating system,
  browser, or server;
- how resources, ownership, errors, cancellation, and asynchronous operations
  cross the host boundary;
- whether WIT and the WebAssembly Component Model are the canonical interface
  description, and how their types map to Raven types; and
- how conformance tests exercise the same component against representative
  hosts rather than treating one runtime as the specification.

Macros are a promising implementation layer for typed imports and exports.
They could consume or produce WIT and generate marshalling, adapters, and
capability declarations without exposing source-generator-specific APIs in
application code. That possibility does not remove the need to define the
semantic mapping and diagnostics first.

The upstream .NET WASI workload remains experimental, so Raven should initially
treat this as investigation work rather than offer a project template. The
first useful probe should be deliberately small: a console or component
application with explicit clock, file, or HTTP capabilities, followed by a
host-matrix test.

## Server-shaped applications inside a browser

A third deployment mode is worth tracking without conflating it with the normal
browser target: run a WASI application under a browser-provided WASI-like host.
Steve Sanderson's archived experimental .NET WASI SDK demonstrated that whole
ASP.NET Core applications could become standalone WASI modules and run under
standard or custom hosts. A browser host can translate the application's
server-style connection and platform operations into browser facilities, for
example from a worker or service worker.

This is not a reason to make ASP.NET Core the browser application model, but it
could enable useful self-contained experiences:

- interactive Web API and framework demos with no deployed server;
- local sandboxes, tutorials, or test fixtures that expose an HTTP-shaped API;
- offline applications that reuse server routing or middleware; and
- disposable development environments with a virtual file system and network.

The tradeoffs need measuring. An HTTP abstraction inside one browser tab can
add size and indirection compared with direct function calls; browser security,
storage, worker lifecycle, streaming, and networking constraints still apply;
and compatibility depends on the WASI host implementation. Treat this as a
host-adapter experiment after the basic `wasi-wasm` capability model exists,
not as part of the initial `raven-browser` template.

## References

- [.NET WebAssembly runtime targets](https://github.com/dotnet/runtime/blob/main/docs/workflow/wasm-documentation.md)
- [Experimental .NET WASI runtime](https://github.com/dotnet/runtime/blob/main/src/mono/wasi/README.md)
- [Archived experimental .NET WASI SDK](https://github.com/SteveSandersonMS/dotnet-wasi-sdk)
- [WebAssembly Component Model concepts](https://component-model.bytecodealliance.org/design/component-model-concepts.html)
- [WIT interfaces and worlds](https://component-model.bytecodealliance.org/design/wit.html)

## Next slices

1. Define browser and WASI target profiles, including supported runtime APIs,
   threading, filesystem, networking, and dynamic-loading behavior.
2. Add compiler-backed Monaco hover using the same workspace snapshot and
   semantic APIs as the language server.
3. Add compiler-produced semantic tokens without replacing the TextMate lexical
   fallback.
4. Reduce and cache the framework metadata payload without reintroducing an
   incomplete reference closure.
5. Prototype a small WIT-described Raven component against more than one WASI
   host before introducing a project template.
6. Evaluate a browser WASI host adapter with a server-shaped Web API demo after
   the underlying capability model is explicit.
