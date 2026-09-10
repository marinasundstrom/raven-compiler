# Raven Changelog

Behavior-focused timeline covering **2025-09-12** to **2026-09-06**.

## Unreleased

### Fixed

- Standard `System.Union<...>` carriers now use native System.Text.Json union
  serialization when targeting .NET 11. The .NET 10 asset keeps its existing
  converter, JSON format, and case-selection behavior.

- .NET 11 closed class hierarchies now emit the framework `IsClosedTypeAttribute`,
  enabling C# exhaustive switches and opt-in System.Text.Json polymorphism.
  Raven recognizes C# closed hierarchies and rejects external direct derivation.

- Invalid inheritance from a static class reports its inheritance diagnostic
  without an unrelated missing-base-constructor error.

- Watched changes to macro source files now include open consuming documents in
  diagnostic refresh notifications after their macro references are updated.

- Classes whose implicit base call has no parameterless constructor now report
  a semantic error instead of crashing during emission. This includes explicit,
  primary, and synthesized default constructors.

- Referenced macro projects now prefer the selected compiler support assemblies
  over same-named package assets, preventing duplicate standard macro exports
  when repository and installed packages are both available.

- Semantic diagnostics now include parent binder scopes hidden by the top-level
  executable binder, restoring the error for file-scope code in libraries.

- Reading a by-reference parameter now loads the referenced value rather than
  its address. Writes and forwarded calls preserve the caller's storage, including
  generic swaps of value and reference types.

- Async error propagation with `use` now preserves suspension and disposes
  resources before returning a propagated failure. Both carrier `?` and
  `try? await` expose their early returns before async state-machine lowering.

- Async `use` resources are disposed before successful task completion, including
  explicit returns from exception handlers and resources acquired after an await.
  Hoisted resources now retain declaration order so cleanup runs in reverse order
  on completion, exceptions, and cancellation.
- Async delegate variables passed to `Task.Run` now select the payload-returning
  overload instead of unnecessarily nesting tasks. Delegate variance supports
  safe reference and nullable-return conversions, and equivalent generic
  signatures prefer the more specific declared parameter shape.
- List sequence-pattern captures now select the `IEnumerable<T>` constructor
  when materializing a slice, avoiding invalid captured lists when the capacity
  constructor appears first in metadata.
- Playground compilation works on single-threaded WebAssembly again: declaration
  access avoids unsupported semaphore waits while preserving cancellation checks.
- Incremental compilations no longer keep a chain of earlier compilations alive
  when reusing metadata and declaration state during editing. Unchanged reference
  metadata also remains reusable through parser recovery and full semantic rebuilds.
- Metadata method-parameter caching no longer prevents discarded reference
  assemblies and their metadata contexts from being garbage collected.
- Website publication now requires the Playground browser smoke suite, including
  a cold compile-and-run before any editor semantic queries.

### Breaking changes

- None recorded.

## 0.1.10 - 2026-09-06

### Breaking changes

- Compiler API consumers must account for `FromKeyword` in yield syntax factories
  and the new `IYieldOperation.IsDelegating` property.

### Added

- `yield from source` delegates synchronous or asynchronous sequences in statement
  and expression positions, with element conversions and async cancellation-token
  forwarding. Delegation expressions evaluate to `unit` after completion.

- Added Roslyn-shaped Fix All support with document, project, and solution
  scopes, stable code-action equivalence keys, and a reusable batch provider
  that merges non-overlapping text changes.
- All built-in code-fix providers now opt into Fix All where they offer a
  repeatable correction, and the language server exposes document-wide fixes
  through the standard `source.fixAll` action.

- `.editorconfig` supports per-file `generated_code = true` or `false` analyzer
  classification, including live updates in the language server.
- Generated-code analyzer policies now recognize `GeneratedCodeAttribute` on
  source types and members, including callbacks and location-based reporting.
- Generated-code analyzer policies recognize conventional generated file names and
  leading auto-generated comments, with classification refreshed after header edits.

- Analyzers can configure source-generated tree callbacks and diagnostic reporting
  independently with `ConfigureGeneratedCodeAnalysis` and `GeneratedCodeAnalysisFlags`.
  Unconfigured analyzers retain the existing analyze-and-report behavior.

### Fixed

- Nested macro names inside declared Raven fragments now resolve their macro symbols for hover, including `markup!` inside `component!`.

- Concurrent editor requests no longer deadlock while declaration binding expands
  macros in another document, restoring outlines, hovers, inlays, and diagnostics
  in macro-backed projects.

- Core generation defines its union metadata attributes even when the compiler
  host already has Raven.Core loaded, preserving case binding after a rebuild.

- Constant hovers show `const` declarations and identify them as constants,
  including namespace-level `extern const` values.

- Async enumeration awaits enumerator disposal on completion, exceptions, and
  early iterator disposal, following C# iterator cleanup semantics.

- Opening a source file in a nested project omitted from the workspace solution
  now loads its containing project on demand, restoring sibling type lookup
  while respecting evaluated compile exclusions.

- Namespace function accessibility validation now tolerates parser recovery
  tokens in parameter lists, preserving diagnostics after local-symbol queries
  in malformed async-lambda code.

- Background project analyzer diagnostics skip busy semantic models and retry
  without caching an incomplete result, preventing waits behind editor requests.
- Releasing asynchronous semantic access no longer corrupts the caller's lock
  depth, preventing subsequent diagnostics from blocking after editor queries.

- Read-only source-generator documents now show compiler and analyzer diagnostics
  from the current project compilation and refresh them after source edits.

- Project analyzer diagnostics now include source-generated syntax trees, honor
  suppression directives in those files, and retry failed generated-tree analysis.

- Public workspace diagnostics now retry failed analyzer runs instead of caching
  partial results, matching the recovery behavior of editor analysis.

- Incomplete document and project analyzer runs are no longer cached or published
  as successful empty results. The editor preserves previous analyzer warnings
  through failures and cancellation, then replaces them after a successful retry.
- Analyzer diagnostics carry their origin into the language server, so external
  analyzer warnings survive compiler-only diagnostic refreshes too.

- Analyzer failure logs identify the analyzer, callback phase, exception, and
  affected document, including initialization failures. Repeated failures are
  counted without flooding logs, and incomplete runs are labeled explicitly.

- Retrying failed or canceled analyzer initialization no longer duplicates
  callbacks or retains concurrency settings from the unsuccessful attempt.

- Go to Definition opens source-generator output as a read-only, live Raven
  document, including hover and navigation back to handwritten source, without
  requiring generated files on disk.

- Source-generator builds honor `EmitCompilerGeneratedFiles` and
  `CompilerGeneratedFilesOutputPath`, with opt-in output under `obj` by default.

- Document diagnostics resolve qualified types from generated source before any
  hover or symbol query, avoiding false missing-type errors in generator consumers.

- Document diagnostics now validate attributes against declarations replaced by
  attached macros, preventing property attributes such as `[Required]` from
  being incorrectly rejected as class attributes after `#[Observable]`.

- Macros invoked directly in a type member list now participate in signature
  help, hover, definition, inlay hints, semantic coloring, and expansion
  previews, including Raven fragments inside their token-tree bodies.

- Hover, inlay hints, and semantic coloring no longer fail on argument-list
  macros such as `subscribe!(...)`. Hover resolves the macro name and ordinary
  symbols inside its arguments and callback.

## 0.1.9 - 2026-09-05

### Breaking changes

- None.

### Added

- Added the default `RAV9038` warning for ignored calls returning `Task` or
  `ValueTask`, including their generic forms. Await, return, store, or explicitly
  discard the task to make its handling clear.

### Fixed

- Empty partial-union diagnostics now consistently point to the first declaration
  in compilation source order, regardless of which file is queried first.
- Bare type names in expression statements now report a compiler error instead
  of the unused-result discard warning (`RAV9034`).

- Ordinary code completion now stays quiet inside comments and string or
  character text, including an unfinished opening quote. Completion remains
  available inside interpolated expressions and after member-access operators.
- Completion on an indented blank line now offers visible locals and statement
  keywords after a completed member expression, while preserving member
  completion when continuing an unfinished access on the next line.
- Completion now uses the identifier prefix before the caret when editing in
  the middle of a word, and preserves existing method argument lists instead
  of inserting duplicate parentheses.
- Accepting a completion now preserves leading indentation and comments by
  replacing only the identifier text.
- VS Code Outline now omits ordinary `const`, `let`, and `var` declarations;
  external constants declared with `extern const` remain visible.
- Hovering a property or field name in an object initializer now shows the
  assigned member instead of returning no hover or only its inferred type.
- Project-backed workspace loading now reuses MSBuild evaluation snapshots
  across reference discovery, recursive graph loading, and nested Raven macro
  builds. Imported project files and source changes invalidate the snapshot,
  while evaluation counters and elapsed time are available for performance
  verification.
- File-backed macro references now reuse the loaded plugin assembly and export
  discovery across consumers when the plugin and dependency content is
  unchanged. Macro instances remain consumer-local, changed binaries load in a
  new collectible context, and failed loads remain retryable.
- Language-server macro refreshes now reuse persistent shadow assemblies from
  unchanged compiler inputs instead of recompiling and only detecting the
  cache hit after emission.
- `RAV9037` now identifies the exact `Option` or `Result` state in which a
  partial extraction can throw, together with the cases that should be handled.
- Hover presentation now treats types with primary constructors like every
  other type: a type hover shows the compact type identity, while a constructor
  call hover shows the parameter list without constructor accessibility.
- Repository sample build and run matrices now propagate the target-specific
  `Raven.Core` assembly together with the repository compiler host. Nested
  Raven project references no longer fall back to the host framework's core
  assembly when their `LanguageTargets` override is intentionally removed.
- `RAV0501` now names each inaccessible constituent type in a public
  constructed contract instead of blaming the whole constructed type. This
  applies across return types, parameters, properties, events, indexers, base
  types, generic constraints, and macro signatures. For example,
  `Result<ItemId, ItemIdError>` identifies `ItemIdError` when that error union
  is the inaccessible component.

## 0.1.8 - 2026-09-04

### Breaking changes

- None.

### Added

- Added a code action that generates throwing stubs for missing interface
  methods, properties, and indexers.
- Added the default `UnsafeUnwrapAnalyzer` (`RAV9037`) warning for partial
  `Option` and `Result` extraction through `Unwrap`, `UnwrapOrThrow`,
  `UnwrapError`, and `Expect`.

### Fixed

- Closed generic overrides now compare their emitted CLR return shape as well
  as Raven nullability. An unconstrained `T?` instantiated with a value type
  correctly overrides through the underlying value-type ABI, while a true
  `Nullable<T>` return remains distinct. This restores the `Raven.Core`
  `Option` and `Result` JSON converters on `net10.0`.
- The greenhouse Native AOT sample now talks to SCD4x sensors through a small
  direct `System.Device.Gpio` I2C driver instead of the reflection-heavy
  `Iot.Device.Bindings` and UnitsNet dependency chain. Its isolated package
  cache is explicitly restored before compiler validation, and Native AOT
  publishing is warning-free without suppressing trim diagnostics.
- Stable release preparation now consumes the standard `Unreleased` breaking-
  change scaffold before opening the versioned section, avoiding duplicate
  headings in generated changelog entries.
- Full and incremental parser recovery now preserve closing braces around
  incomplete argument and parameter lists, escaped text in interpolated strings,
  and stray union-body braces. Malformed union payload lists no longer crash
  semantic binding, and declaration-unstable edits use a logged full semantic
  rebind so undo cannot retain stale overrides or lose top-level declarations.
- Parser recovery now retains unexpected tokens and their boundary whitespace in
  the syntax tree. Malformed edits no longer shift later source positions, so
  diagnostics and language-service responses remain aligned with unchanged code
  and recover correctly after undo.
- Portable PDB emission now inserts the missing debug-information rows for
  abstract and interface methods before assembly-reference rewriting. This keeps
  later sequence points and local scopes attached to their actual methods, so
  breakpoints reliably bind and stop in multi-file applications.
- Match exhaustiveness once again treats inferred payload bindings as complete
  when matching imported cases inside their generic union, without regressing
  nested-union coverage.
- Inconsistent accessibility is now rejected with `RAV0501` across primary
  constructors and promoted properties, delegates, inherited types, namespace
  functions, union payloads, constructed signature types, and protected APIs
  that expose internal types.
- Standalone file-based applications opened in the language server now activate
  the standard `Raven.Macros` compiler plugin as well as referencing its metadata.
  Their editor compilation includes `Raven.Core` and the generated Prelude,
  matching one-shot script compilation.
- Language-server project loading now isolates nested NuGet restores from the
  MSBuild instance registered in the server process, preventing SDK assembly
  mismatches when a workspace-level `global.json` selects a different .NET SDK.
- Match exhaustiveness now recognizes qualified parameterless union cases nested
  inside constructed generic union cases, including across source and metadata.
- Union declarations now report an error when a body-form union has no cases or
  a parenthesized union has fewer than two variant types.
- Source-declared `ToString` overrides on records and unions now retain their
  bodies instead of being replaced by synthesized structural formatting, and
  overrides with incompatible return nullability report `RAV0307`.
- Parser recovery during live editing no longer loops at end-of-file for incomplete
  enum declarations, throws when an incrementally reparsed type is removed before a
  newline, or places stray separator tokens directly in typed member lists. This
  keeps completion, diagnostics, semantic tokens, and document symbols responsive
  while declarations are being typed.

## 0.1.7 - 2026-09-02

### Breaking changes

- None.

### Added

- Language-server workspaces now use standard `.sln` and XML `.slnx` files to
  group Raven projects, including projects referenced by relative paths outside
  the opened folder, while retaining recursive project discovery when no usable
  solution grouping exists.

### Fixed

- Language-server workspaces now reload evaluated Raven projects when imported
  MSBuild `.props` or `.targets` files change, keeping project options and
  package properties in sync with `dotnet build`.
- Hover signatures for properties, fields, and events with unresolved declared
  types now preserve the authored type annotation instead of displaying the
  compiler's internal error type.
- Hovering an unresolved name in a type position no longer presents the
  compiler's internal error-type symbol as Quick Info; the unresolved-name
  diagnostic remains the user-facing explanation.
- Type arguments are now resolved independently when their enclosing generic
  type is unresolved, so semantic features still recognize known types in
  annotations such as `MissingGeneric<KnownType>`.
- The target-framework release gate now stops when a representative project
  fails to build instead of continuing with stale binaries.

## 0.1.6 - 2026-08-29

### Breaking changes

- None.

### Fixed

- Native PE files included in MSBuild reference sets no longer crash compilation
  as invalid managed metadata. This restores Raven ASP.NET Core template builds
  on Windows, where the framework reference set includes native hosting modules.
- Release installation verification now fails immediately when a Windows native
  command fails, preventing compiler or template failures from being reported as
  a successful workflow run.

## 0.1.5 - 2026-08-28

### Breaking changes

- None.

### Added

- Added namespace-scope `[class: ...]` declarations for applying attributes to
  synthesized `NamespaceMembers` classes. A blank line distinguishes the scope
  declaration from an attribute attached to the following member, and the form
  works consistently in root, file-scoped, block-scoped, and nested namespaces.
- Extended the MyServiceBus RabbitMQ showcase with the consumer-method API from
  `Sundstrom.MyServiceBus.RabbitMq` 0.1.0-preview.5. An attributed Raven
  namespace-level request function demonstrates message, consume-context, and
  cancellation-token binding, a `Task<TResponse>` result, and automatic
  assembly scanning.

### Fixed

- Attributes on namespace-level functions and constants now emit only on their
  declared method, return value, parameter, or field target instead of leaking
  onto the synthesized `NamespaceMembers` type.

## 0.1.4 - 2026-08-28

### Breaking changes

- None.

### Added

- Added a MyServiceBus and RabbitMQ showcase with dependency-injected Raven
  consumers, tuple deconstruction of message records, publish/subscribe,
  request/response, async shutdown, and a Docker broker helper.

### Fixed

- Language-server project loading now consumes the current NuGet assets file
  before using the direct-package cache and falls back to restore when a package
  declares dependencies. Transitive compile references are therefore available
  to diagnostics, hover, and other semantic features instead of producing false
  missing-import errors and expensive fallback binding.
- Async methods now support one or more `await` expressions in `catch` and
  `finally` blocks. Classic lowering emits verifiable generated state machines,
  while .NET 11 runtime async preserves the same return and exception behavior
  without synthesizing a state-machine type.

## 0.1.3 - 2026-08-26

### Breaking changes

- None.

### Added

- Added an experimental declarative MAUI component sample. Its `component!`
  and `maui!` macros generate ordinary `ContentView`, `BindableProperty`, CLR
  property, event, and native child-collection APIs; support XAML-style text
  conversion and embedded Raven expressions; project collection-comprehension
  controls; and provide XML/Raven editor regions without a separate UI runtime.

### Fixed

- Explicitly typed field initializers now apply method-group-to-delegate
  conversion instead of emitting the delegate's default value.
- Nested type lookup now respects the authored generic arity consistently in
  type syntax, qualified expressions, aliases, attributes, and semantic-model
  queries, for both source and referenced metadata types, including
  expression-shaped construction through a generic metadata container. An
  omitted type-argument list remains open for Raven's generic constructor
  inference, while an explicit list selects its exact arity.
- Fixed member lookup through base classes for object initializers, `with`
  expressions, inherited content properties, and static members accessed
  through derived types. This restores ordinary .NET inheritance behavior for
  framework controls and Raven-authored class hierarchies.

## 0.1.2 - 2026-08-26

### Breaking changes

- SDK-supplied implicit imports are now enabled by default. Existing projects
  with names that collide with the imported `System` namespaces or standard
  `Result`/`Option` cases can receive new ambiguity diagnostics. Set
  `ImplicitImports` to `disable` or remove individual `Import` items to retain
  the previous scope.

- Aligned generated project preludes with the .NET SDK model. `Raven.Sdk` now
  contributes its defaults as removable MSBuild `Import` items and
  `ImplicitImports` controls SDK-supplied imports.
- Added `Raven.Sdk.Web`, a lockstep Raven project SDK that composes
  `Microsoft.NET.Sdk.Web`, supplies ASP.NET Core and `Microsoft.Extensions`
  implicit imports, and preserves MVC application-part generation without a C#
  CodeDOM provider.
- Added an isolated Pico W nanoFramework DHT22 and SH1106 sample that reads GP2
  every two seconds and continuously refreshes the displayed temperature without
  involving Wi-Fi, HTTP, or LED status behavior.

## 0.1.1 - 2026-08-25

- Fixed browser Playground compilation of Raven unions, including the built-in
  macro forms sample, by emitting Raven-owned metadata without unsupported
  WebAssembly reflection APIs.

## 0.1.0 - 2026-08-24

- Fixed stable release preparation so version updates preserve historical
  architecture decision records instead of rewriting their original context.
- Fixed Raven project output closure so compiler-discovered runtime dependencies
  are copied even when their compile-time references are not marked copy-local.
- Fixed built-in bitwise operators and compound assignments through pointer
  members so they emit valid IL and read and write the pointed-to field, while
  preserving numeric promotion for smaller integral operands.
- Fixed standalone compiler dependency setup so `Raven.CodeAnalysis` remains
  available when the optional standard macro library has not been built or
  installed alongside the compiler.
- Fixed semantic analysis of unresolved fluent local-initializer chains so
  diagnostics terminate instead of repeatedly retrying the same failed local
  type-inference paths.
- Extended the Pico W nanoFramework Wi-Fi sample with manual and
  `nanoFramework.Iot.Device.Ssd13xx` SH1106 OLED paths that display a retrieved
  public IP address, including the complete I2C/graphics deployment closure.
- Fixed RavenDoc source-link generation in linked worktrees and other
  hermetic environments, including relative or missing source directories.
- Fixed clean-checkout compiler builds so syntax, bound-tree, symbol, and
  diagnostic sources generated during the build are included in the same
  compilation instead of requiring a second warm build.
- Simplified iterator control flow to `yield value` and bare `return`. The
  C#-style `yield return value` and `yield break` forms and their syntax and
  operation API nodes have been removed; old source receives direct migration
  diagnostics.
- Fixed sync and async iterators so every yielded expression is checked against
  the declared element type. Invalid implicit narrowing is now diagnosed, and
  an available explicit conversion is surfaced to editor tooling instead of
  being silently applied during emission.
- Added a manifest-driven Release IL gate for representative standalone and
  project samples, with target, assembly, coverage, verifier, and runtime
  evidence recorded for the bootstrap freeze.
- Fixed async state-machine lowering so expressions nested inside comparison,
  range, guarded, union-member, and deconstruction patterns remap instance and
  captured receivers through their synthesized fields.
- Fixed ILVerify reference resolution for cross-target compilation by supplying
  the selected target runtime's implementation closure, including framework
  internals such as `System.Private.Xml.Linq`, instead of the compiler host's
  runtime assemblies.
- Fixed repository dependency discovery so Release compiler invocations select
  Release Raven.CodeAnalysis and Raven.Macros fallbacks instead of silently
  mixing in Debug assemblies.
- Fixed constrained generic-math operators to emit calls to their static
  interface contracts instead of invalid primitive numeric IL over type
  parameters.
- Added an aggregate project-sample runtime gate that executes every ordinary
  executable with repository artifacts, enforces timeouts, records auditable
  reports, and requires reviewed build-only reasons for non-runnable workloads.
- Fixed verifiable emission for reference-constrained generic values used by
  nullable declaration patterns, null returns, and default values. These paths
  now use CLR-verifiable typed locals, boxed null tests, and `initobj` defaults.
- Fixed synthesized `Equals(object)` for generic record classes so casts and
  locals use the constructed self type rather than the open generic definition.
  Generic record equality now executes correctly and emits verifiable IL.
- Fixed repeated generic substitution when nested union cases were re-anchored
  under a constructed carrier. Generic cases such as `Ok<T[]>` now remain
  `Ok<T[]>` instead of becoming nested arrays, and `try` expressions returning
  generic `Result` carriers emit verifiable conversion metadata.
- The qualified pre-bootstrap foundation will ship as the stable `0.1.0`
  release. Release preparation now transitions the current preview line to
  `0.1.0`, while bootstrap stage tags remain independent identities. Release
  and bootstrap gates require both build and runtime evidence for standalone
  and executable project samples, with reviewed reasons for non-runnable cases.
- Fixed static interface implementation conformance and emission. Missing
  static contracts are now diagnosed, and valid implementations receive the
  method-override metadata required for generic static dispatch and verifiable
  `IPropagatable` implementations.
- Fixed static interface contracts without bodies so they emit as abstract
  static virtual methods rather than invalid empty method bodies. This makes
  generic static contracts such as `IPropagatable.FromResidual` verifiable IL.
- Added Raven-authored `json!` and `xml!` standard-library macros. They produce
  strongly typed `JsonObject` and `XElement` expressions, use Raven's
  `$identifier` and `${expression}` splice forms, surface caller-scope Raven
  fragments, validate completed literals with `JsonDocument` and `XDocument`,
  and project their envelopes to JSON/XML editor services. A new data-literal
  sample demonstrates platform interop and escaping.
- Fixed emitted framework implementation references to use the target runtime's
  identities rather than the compiler host's identities. This keeps assemblies
  targeting .NET 10 loadable when Raven itself runs on .NET 11, including APIs
  such as framework collections and LINQ to XML that use forwarded framework
  types. Union contracts and marker attributes are likewise resolved from the
  target `Raven.Core` rather than the host runtime.
- Fixed metadata loading for assemblies whose emitted custom-attribute scopes
  refer back to the containing assembly, preventing recursive assembly-symbol
  construction when consuming Raven-authored union metadata.
- Raven project builds now copy explicit macro runtime dependencies even when
  the same assembly also appears among compiler references. The project-sample
  harness defaults to the repository targets and .NET 11 compiler host, with an
  explicit switch for testing the separately installed SDK.
- Unused-value analysis now counts caller variables referenced by ordinary
  Raven fragments inside macro bodies.
- Fixed generic-name lookahead so a less-than comparison before a block cannot
  consume a greater-than operator from a nested statement as a type-argument
  terminator.
- Fixed incremental editor binding so top-level function signatures wait for
  deferred source imports, preserving union parameter types, union patterns,
  and pattern payload locals during diagnostics and hover queries.
- Fixed TextMate fallback colorization for attribute targets such as
  `assembly`, lexical keywords such as `typeof`, and `not` in `is not`
  patterns.
- Raven project publishing now reuses project-reference outputs already known
  to MSBuild instead of also publishing the compiler's intermediate runtime
  copy of the same assembly.
- Added session-isolated repository development launchers for a Raven terminal,
  an installed VS Code extension using repository toolchain paths, and a built
  repository extension running in an isolated Extension Development Host.

## 0.1.0-preview.14 - 2026-08-23

- Compact macro declarations can now opt into explicit carrier combinations and
  token-body requirements with `MacroCarrier`. The standard `timer!` macro uses
  this to add `timer! message { ... }` while retaining `timer! { ... }`; message
  templates preserve normal Raven interpolation and reserve literal `{time}`
  for the elapsed duration.
- The VS Code extension now reports its toolchain provenance at activation and
  through **Raven: Show Toolchain Information**, distinguishing repository and
  installed extension hosts, the exact language-server source, the installed
  SDK, and each workspace's `global.json`-selected project SDK.
- Language-server handlers now bind explicitly to `MediatR.Unit`, keeping VS
  Code extension packaging compatible with the .NET 11 SDK.
- Playground metaprogramming showcases now separate typed parenthesized calls,
  declaration-shaped forms, and the standard attached, token-body, quote, and
  statement-shaped macro forms into focused runnable examples.
- The standard `timer!` macro now publishes its complete body as an ordinary
  Raven block fragment, restoring symbol hover and the other compiler-backed
  editor features inside its braces.
- Macros can now use `ExpressionSyntax<T>` as a semantic boundary contract in
  addition to the existing expression syntax-category contract. Typed inputs
  retain the authored immutable expression and its bound type, reject
  incompatible arguments before execution, and compose with nested macro
  invocations. Typed outputs bind the ordinary expansion and reject results
  that are not implicitly convertible to `T`. Class-authored providers expose
  the equivalent output promise through `ExpressionResultType`; the checked-in
  Blazor Markup sample now promises `RenderFragment`, while Query remains
  untyped until its source- and selector-dependent result can be inferred.
  Macro hover presents a declared result as `T` rather than the infrastructure
  facade `ExpressionSyntax<T>`, and infers the bound expansion type when no
  result contract is declared.
- The compiler no longer records or copies assemblies from the installed .NET
  shared framework as application-local runtime dependencies. Rebuilding after
  removing a runtime-dependent macro now clears the stale dependency manifest.
- Language-server builds and VS Code extension packages now carry the matching
  `Raven.Macros` assembly. Project loading can therefore activate standard and
  referenced macro providers in isolated editor hosts, restoring macro DSL
  completion, classifications, and embedded-language projections.
- `quote!` now implements expression parsing, splice recognition, diagnostics,
  and syntax rendering wholly in the Raven-authored `Raven.Macros` library.
  Macro authors can parse equal-width body projections while retaining source
  positions, and `RavenQuoter` now exposes node source overrides that preserve
  surrounding trivia. The transitional compiler-hosted quote implementation
  has been removed, and `compile!` reuses the Raven implementation.
- `compile!` now validates its delegate type argument and constructs its
  runtime-compilation expression in the Raven-authored `Raven.Macros` library.
  The macro design now records contextual expected-delegate and explicitly
  typed-lambda inference as future, non-heuristic directions.
- `Error` now performs its union inspection, `System.IError` base-list rewrite,
  duplicate-message diagnostics, and generated `Message` and `Cause` property
  construction wholly in the Raven-authored `Raven.Macros` library. The final
  compiler-hosted C# implementation for the Error macro family has been
  removed.
- `ErrorMessage` now validates its string expression and containing `#[Error]`
  union wholly in the Raven-authored `Raven.Macros` library, demonstrating an
  attached macro that reports diagnostics and intentionally returns an empty
  expansion.
- `sha256Digest!` now implements constant inspection, canonical formatting,
  hashing, and syntax construction wholly in the Raven-authored
  `Raven.Macros` library through the public `MacroArgument` constant contract.
- Macro authors can now read source-relative text files through public
  dependency-tracked context APIs and construct safely escaped Raven string
  literals with `MacroSyntax.StringLiteral`. `embedFileContent!` now implements
  its expansion wholly in the Raven-authored `Raven.Macros` library instead of
  delegating to a compiler-hosted C# helper.
- Freestanding macros now preserve their source envelope through explicit
  parenthesized, expression-header, token-tree, and declaration carrier syntax
  nodes. Class-authored macros can select accepted carrier kinds and whether a
  trailing token body is forbidden, optional, or required. Expression-header
  carriers accept one ordinary Raven expression in expression or statement
  position, including keyword aliases such as `match! value { ... }`, without
  reclassifying postfix `!` followed by an operator or member access.
  Declaration carriers now preserve a reusable Raven-shaped header containing
  declared type parameters, parameters, either a base list or return type,
  standard `where` constraints, an optional `permits` clause, and an optional
  token body. Generic arguments on the macro name remain distinct from type
  parameters introduced after the carried declaration name.
- Member completion in a macro block fragment now resolves assignment targets
  through fragment-local semantic state instead of querying the outer semantic
  model with detached syntax. Typing `.` on the right side of an assignment
  therefore shows receiver members on the first request instead of falling back
  to statement keywords until completion is reopened.
- Inferred-type inlays now include variables introduced by match patterns inside
  macro fragments, including visible-range requests that start within a
  declaration macro body. Committed document edits also request a fresh inlay
  pass so newly added argument-name hints do not remain hidden behind translated
  cached hints.

## 0.1.0-preview.12 - 2026-08-22

- Added declaration-level capability clauses for function-shaped token-tree
  macros. Uniform clauses such as `completion by CompleteDsl` and
  `fragments by DslSyntax.GetFragments` make generated adapters implement the
  existing optional macro service interfaces and forward to ordinary namespace
  or qualified static functions. Existing class-authored macros and provider
  APIs remain unchanged. A new `macro-capabilities` sample demonstrates
  expansion plus keyword, token-kind, highlighting, fragment, symbol,
  completion, and embedded-language services without introducing a service
  class.
- Added compiler-owned, position-preserving embedded-language projections for
  token-tree macros. `IMacroEmbeddedLanguageProvider` identifies the target
  language and supplies projected body text; Raven validates identical length
  and line breaks, caches the result, and contains optional-provider failures.
  The checked-in Markup macro now exposes an `html` projection that retains the
  markup envelope while masking embedded Raven expressions. The VS Code
  extension mounts it as a virtual HTML document, asks VS Code's existing HTML
  provider for standard element, attribute, and closing-tag completions plus
  HTML hover, maps ranges back to Raven source, and merges them after Raven's
  semantic tooling. Nested `markup!` inside a reported macro block uses the same
  position-based projection lookup, while reported embedded Raven fragments
  retain ownership of completion and hover inside their spans.
- Added compiler-owned completion contributions for token-tree macro DSLs.
  `IMacroCompletionProvider` reports editor-neutral items with body-relative
  replacement spans and optional Raven symbols; the compiler maps, orders, and
  failure-isolates them before the language server presents them. The checked-in
  Markup macro now completes incomplete Blazor component tags and properties,
  while embedded Raven expressions retain native completion and standard HTML
  catalog completion comes from VS Code's virtual-document integration.
  Completion requests now propagate cancellation into macro
  providers, and the language server triggers completion on `<` and spaces.
- Macro block fragments now honor their reported body-relative spans instead of
  reparsing the complete token-tree body. Structured declaration DSLs can expose
  multiple independent Raven blocks and receive native hover, completion,
  classifications, inferred-type inlays, and semantic binding in each region.
- Macro-fragment semantic queries now prefer exact compiler symbol mappings,
  including target-typed arguments and inferred pattern declarations, instead
  of falling back to an enclosing invocation. Fragment results are authoritative
  to language services, so detached syntax is not rebound through the outer
  tree, and target-typed lambda declarations are stable across incremental cache
  histories. The Proto.Actor DSL also projects its generated `IContext` into
  fragment scope so `context.Respond(...)` participates in native binding,
  hover, and completion.
- Semantic tokens for macro documents now wait for the current compiler semantic
  snapshot instead of caching a syntax-only result when completion, inlays, or
  other work temporarily owns semantic access. Macro-provided keywords and
  fragment classifications therefore remain stable while editing, and token
  caches also track cross-file project snapshot changes.
- Added a framework-free browser application type to `rvn init` and
  `Raven.Templates` as `browser`/`raven-browser`. It composes Raven with
  `Microsoft.NET.Sdk.WebAssembly`, publishes a normal static browser bundle,
  demonstrates a typed JavaScript method call, a JavaScript-to-Raven delegate
  callback, and a named Raven method discovered through `getAssemblyExports`
  via a built-in Raven `[JSImport]`/`[JSExport]` source generator, and includes
  a runnable sample plus a documented macro-based direction for evolving typed
  Raven imports and exports. Mutable value-type receiver calls now preserve
  mutations required by the low-level WebAssembly marshalling API without
  misinterpreting preloaded conditional-access values as managed addresses. The
  WebAssembly direction now tracks browser and WASI as
  sibling host stories, including future WIT mappings and browser-hosted WASI
  experiments for server-shaped applications.
- Macro-fragment editor queries no longer publish speculative nested expansion
  diagnostics or register their synthetic expansion trees as authored document
  state. Declaration-shaped component macros therefore remain valid while
  inlays, completion, and classifications inspect a nested expression macro,
  including after blank-line edits. Declaration carriers are also covered as
  members at compilation-unit and file-scoped-namespace scope, distinct from
  Raven's supported top-level statement macro form.
- Fixed VS Code language-server resolution so a packaged extension does not
  silently use an older automatically discovered SDK server, which could
  invalidate macro expansion diagnostics after otherwise harmless edits.
- Statement- and expression-form `if`, including pattern conditionals, now
  accept an optional contextual `then` keyword. It clearly separates the
  condition from an unbraced branch and permits statement bodies on the same
  line without reserving `then` as an identifier elsewhere. The keyword is
  primarily intended for expression-form conditionals; statement support keeps
  the syntax symmetric. Macro token streams also preserve custom keyword kinds
  for `then` and other non-reserved Raven keywords, so existing token-tree DSL
  clauses remain compatible.
- `Option<T>` parameters may now use `.None` as their default argument. Raven
  emits a dedicated parameter-metadata marker and reconstructs the active
  `None` case for omitted arguments, including calls across assembly references,
  without treating the union carrier's CLR default state as `None`.
- The Greenhouse Monitor sample can now read live CO2, temperature, and
  humidity telemetry from an SCD40/SCD41 sensor connected to a Raspberry Pi
  over I2C, while retaining simulated telemetry as its default mock source.
- The public Web API showcase now demonstrates passing a Raven `func` lambda
  directly as an ASP.NET Core route handler. The component-macro showcase now
  mixes an ordinary Raven statement with its nested `markup!` expression, and
  the static documentation highlighter presents the known `component!` and
  `markup!` showcase aliases as contextual keywords.
- Inferred local type inlays now work inside Raven macro fragments, including
  declaration-shaped block fragments such as `component! Greeting(...)` where
  `let x = 42` is presented as `let x: int = 42`.
- Unary `+`, `-`, and `~` now apply .NET-style integral promotion to `sbyte`,
  `byte`, `short`, `ushort`, and `char` operands, producing an `int` result.
- The release procedure and installation-workflow dispatch form now state that
  public installation verification must wait until the published packages are
  restorable from a fresh NuGet.org-only cache, because its project and template
  checks restore `Raven.Sdk` and `Raven.Templates`. Merely seeing the version in
  NuGet's flat-container index is not treated as sufficient propagation. The
  documented post-publication checklist also separates immutable release
  publication, package propagation, installation verification, and the
  `main`-only GitHub Pages deployment.
- The website workflow now fetches release tags before deriving provenance, so
  a post-release `main` deployment advances to the next unreleased preview line
  instead of labeling the already published version as unreleased.

## 0.1.0-preview.11 - 2026-08-20

- Identifier-bearing declaration macro carriers now appear once under their
  authored name in the VS Code outline, while identifier-less member macro
  invocations remain omitted and generated implementation members stay hidden.
- Published documentation, Playground, RavenDoc references, and experimental
  component-macro sites now show one shared version and source commit in their
  footers. Untagged builds are identified as unreleased, while an exact release
  tag marks the matching immutable build as released.
- The parser now represents declaration-shaped freestanding macro carriers such
  as `component! Foo(x: int) { ... }` with a dedicated syntax node, preserving
  modifiers, the declared name, declaration parameters, and the lossless body
  at compilation-unit, namespace, and type-member scope. A macro parameter of
  type `FreestandingMacroDeclarationSyntax` selects this carrier, while an
  independent `IMacroTokenStream` parameter projects its body. Declaration
  carriers now participate in descriptor resolution, expansion-category
  validation, namespace/type declaration discovery, expanded documents, and
  source-authored macro lowering. The HTML/Blazor showcase uses the
  `component` alias of `FunctionComponent` to compose an ordinary Raven body
  with a nested `markup!` macro. The showcase names the provider `MarkupMacro`,
  retains `Html!` as a compatibility alias, and presents the sample as a
  macro-authored DSL over ordinary Blazor infrastructure.
- Declaration-shaped macro bodies can now be exposed as ordinary Raven block
  fragments, with typed header parameters projected into the fragment as
  parameter symbols that preserve navigation back to the header. Compiler and
  language-server queries now carry those fragments through nested macros for
  completion, hover, go-to-definition, and semantic coloring. Resolved macro
  aliases such as `component!` and `markup!` are contextually presented as
  contextual keywords after macro resolution, while canonical macro names
  retain the distinct macro classification. The browser Playground now
  requests the same compiler-owned semantic classifications and overlays them
  in Monaco, so visible aliases receive the same keyword color there as in
  language-server clients.
- Lambda diagnostics now reject missing expression bodies, report incompatible
  explicit and tail returns at the returned expression, and publish actionable
  source ranges through the language server.
- Language-server project reloads now preserve the last successfully loaded
  solution when a transient project edit or restore failure prevents the
  replacement project from opening, and retry on the next relevant file event.
- Enum-member signatures and hover text now show the member's underlying
  constant value, while enum-valued parameter defaults retain target-typed
  displays such as `= .Rising`.
- The language server now reloads projects when an external `dotnet restore` or
  `dotnet build` updates `obj/project.assets.json`, allowing newly added package
  references to recover after a failed or racing editor-side project reload.
- Editing an unused import now invalidates the stale grey diagnostic on that
  import line only, while unused-import diagnostics on untouched lines remain
  visible until refreshed analyzer results arrive.
- Small-document inlay hints now recompute immediately after committed edits,
  preventing stale inferred labels from lingering while typing.
- Bare locals, parameters, properties, and non-constant fields are no longer
  accepted as implicit runtime value patterns; use `== value` for an explicit
  comparison or a binding keyword to capture the matched value.
- Inferred type inlays now include collection-comprehension targets authored in
  Raven macro fragments reported through the current fragment-provider API.
- Extern constants now retain `extern` in symbol signatures and hover text, and
  namespace-scoped constants and `let`/`var` declarations now appear alongside
  other members in the editor outline.
- Macro terminology now follows procedural-macro conventions: application kind
  is `Freestanding` or `Attached`. Freestanding macros use function-like syntax
  at any grammar position permitted by their declared result; attached macros
  occupy attribute-like positions on existing declarations. The public
  freestanding support types are now `FreestandingMacroContext` and
  `FreestandingMacroExpansionResult`.
- Macros now have one stable method-shaped declaration ABI: a hidden nominal
  definition type owns generic parameters, its designated `Expand` method owns
  any number of caller-supplied or compiler-injected parameters, and binding,
  execution, signature help, hover, and referenced-project metadata consume
  that same canonical signature. Compiled providers use the erased
  `IMacroExecutor` boundary, while the existing `macro` authoring syntax is
  unchanged. Legacy category-specific providers are normalized to an executor
  at registration, so compiler expansion now has one dispatch path; the
  standard `query!` provider uses that boundary directly. Authors may also use
  an ordinary `IMacroDefinition` class whose unconstrained designated `Expand`
  method supplies the canonical signature; Raven lowers generic and
  non-generic definition classes to direct erased entry points without
  reflective expansion. Source edits to a definition's generic or `Expand`
  signature rebuild its registered descriptor so unchanged consumer documents
  immediately receive updated binding and editor information.
- Raven project compilation avoids a redundant SDK evaluation when the target
  framework and configuration are already supplied, skips local macro
  activation assemblies for pure macro libraries, avoids duplicate framework
  references, and reuses documentation symbol collection. In the standard
  macro library workload, a measured net11 compiler invocation fell from about
  140 seconds to 11 seconds.
- Named `func` declarations now accept `_` discard parameters. Discarded
  parameters preserve their argument slots and types without introducing a
  usable body name or producing the unused-parameter diagnostic, which allows
  overrides and interface implementations to explicitly ignore contract
  parameters.

## 0.1.0-preview.10 - 2026-08-16

- Concurrent pattern-loop diagnostics and semantic queries no longer
  recursively hash binder-owned local symbols.
- Lock statements now preserve the runtime scope for
  `System.Threading.Monitor`, preventing .NET 11 executables from looking for
  the non-forwarded type in `System.Runtime`.
- Postfix `?` propagation is now extensible through Raven.Core's
  `IPropagatable<TSelf, TOutput, TResidual>` contract. Custom classes, structs,
  records, and unions can define output extraction, residual extraction, and
  enclosing-carrier reconstruction; `Result` and `Option` implement the same
  protocol. Custom-carrier `expr?.Member` syntax propagates the receiver before
  accessing the output member.
- Union symbols now expose their complete alternative-type set as `Variants`.
  The former `CaseTypes` API has been removed so `case` consistently names the
  body-form declaration syntax, while variants may be declared by either
  parenthesized types or `case` declarations.
- Raven SDK builds now recompile when the selected compiler toolchain path
  changes, even when a newly installed SDK's files have older timestamps than
  existing project output. This prevents stale union assemblies from retaining
  .NET 11 `System.Runtime` contracts after a project is retargeted to .NET 10.
- Raven scripts and the `rvn repl` now load the standard generated prelude, so
  common namespaces and extensions such as LINQ are available without explicit
  imports, matching regular Raven projects.
- Compiler diagnostics emitted during `dotnet build` and `dotnet run` now stay
  on a single MSBuild-compatible line, so the .NET CLI forwards the actual Raven
  errors and warnings instead of showing only the final `RAVENBUILD` failure.
- Result-returning application entry points now resolve constructed `Ok` and
  `Error` cases consistently for source and referenced `Raven.Core.Result`
  carriers. `Error` writes its payload to standard error and returns exit code
  `1`; `Result<(), E>.Ok` returns exit code `0`.

## 0.1.0-preview.9 - 2026-08-14

- Added a guarded release procedure that derives one monotonically increasing
  `0.1.0-preview.N` version, validates all current documentation and package
  references, requires the tagged commit to have passed the full Main CI gate,
  records package provenance, and rejects duplicate immutable NuGet versions.
  Every push to `main` now runs the complete ordered build, baseline,
  runtime/emission, and language-server suites. Distribution reuses that result
  and runs only release-specific packaging and smoke checks.

- Removed a stale union semantic test for the reflection-era friendly-type-name
  helper and aligned conversion diagnostics coverage with the explicit-cast
  hint emitted alongside an incompatible assignment. Unused-variable analyzer
  registration coverage now also includes macro declarations, and Raven.Core
  `Result.ToString()` coverage matches the reflection-free union display
  contract.

## 0.1.0-preview.8.1 - 2026-08-14

- Added a Pico W nanoFramework Wi-Fi/HTTP LED sample whose deployment wrapper
  securely prompts for compile-time SSID and password constants, builds the
  direct device-only networking calls, and packages the complete compact-assembly
  closure for wire-protocol or UF2 deployment. Its managed networking package
  snapshot matches the current `PICO_RP2040_W` preview firmware's native
  checksums. Wire-protocol deployment now rejects the legacy
  `RP_PICO_W_RP2040` firmware, which loads the Wi-Fi assembly but exposes no
  wireless adapter, before prompting for credentials or rebuilding the image.
  Hardware guidance now records the Pico W family's single-band 2.4 GHz
  requirement and the observed status-4 IP-address timeout on a non-compatible
  network, followed by immediate success on a 2.4 GHz network. The LED remains
  off during the blocking network operation so steady high now indicates only
  a successful HTTP response. Its network workflow now returns an exhaustive
  union whose failure cases preserve network details, HTTP status, or safe
  exception details instead of encoding outcomes as integer constants. Its VS
  Code launch task now prompts for masked Wi-Fi credentials and stages the full
  compact assembly closure expected by the nanoFramework debugger. Its build
  and deploy scripts accept `--repo-compiler` to rebuild and select the compiler
  from the current checkout instead of reusing the compiler bundled with the
  installed SDK.

- Synthesized union `ToString()` now builds the carrier and case representation
  directly from the statically known union shape. It no longer emits runtime
  `System.Type` inspection or reflection-based display-name helpers. Closed
  generic type arguments are intentionally omitted from the display (for
  example, `Result.Ok(42)`), while strings and characters remain quoted through
  ordinary runtime type tests. Restricted targets such as `netnano1.0` can also
  disable structural `ToString()` synthesis entirely and inherit the runtime
  default without affecting Raven.Core builds.

- Direct numeric conversions to or from metadata enums now normalize through
  the enum's declared underlying type before Reflection.Emit runtime-type
  resolution. Target-only enums therefore support conversions such as
  `(int)WifiNetworkHelper.Status` without requiring the target assembly to be
  loadable in the compiler host.

- Explicit external-constant overrides now take precedence over project
  `RavenConstant` defaults regardless of command-line argument order. This
  prevents SDK project defaults from replacing values supplied by deployment
  tooling, such as the Wi-Fi SSID and password embedded in a device image.

- External metadata method calls no longer require the target assembly to be
  loadable in the compiler host. When Reflection.Emit cannot materialize a
  target `MethodInfo`, emission now preserves the compiler method symbol and
  replaces its temporary IL operand with a target `MemberRef` before writing
  the PE. This first metadata-token path allows nanoFramework Wi-Fi and HTTP
  APIs to be called directly from Raven without a C# bridge.

- Runtime async now follows the target runtime capability, matching Roslyn's
  .NET 11 model. It remains enabled by default for `net11.0`, falls back to
  classic state-machine lowering for `net10.0`, and can be disabled in SDK
  projects with `<UseRuntimeAsync>false</UseRuntimeAsync>`. Capability
  detection requires the complete `AsyncHelpers` contract because .NET 10
  exposes an earlier experimental form without the entry-point handlers.
  General awaiters now lower through the runtime suspension helpers instead of
  synchronously calling `GetResult`; this includes `Task.Yield()`.

- Fixed .NET 11 compiler hosts leaking host-only runtime APIs into net10.0
  output. Net10 unions now bind their `IUnion` compatibility contract from
  Raven.Core instead of `System.Runtime`, and async entry-point bridges use the
  target-compatible awaiter pattern when `AsyncHelpers` is absent from the
  selected target framework.

- Included both XML and Markdown symbol documentation for Raven.Core and
  Raven.Macros in NuGet packages and standalone SDK archives. Raven tooling
  prefers Markdown and falls back to XML, while C# and other .NET consumers
  continue to receive standard XML documentation. Constructed and substituted
  metadata symbols now preserve their definitions' documentation, and generic
  method documentation IDs follow the standard .NET arity convention.
  Documentation caches now reload when an assembly or sidecar is replaced at
  the same output path, so editor sessions do not retain an older fallback.

- Fixed clean Raven.Core bootstrap builds so `RavenEmitCoreTypesOnly` cannot be
  overwritten by a previously emitted Raven.Core reference. Multi-targeted
  compiler builds no longer load the net10.0 core library as a reference while
  rebuilding that same library. Distributed compiler hosts now also prefer
  their version-matched local CodeAnalysis and Macros assemblies over any
  development outputs that happen to be reachable from their filesystem path,
  and accept those host-targeted libraries when compiling for a newer .NET
  target framework.

- Standardized the distributable Raven toolchain on .NET 11. The standalone
  SDK, NuGet Project SDK, analyzers, language server, and VS Code extension now
  carry .NET 11-hosted tooling, while Raven library packages retain their
  target-specific assets so the toolchain can continue to build net10.0 and
  net11.0 applications. Emission now also retargets reflection-derived assembly
  identities to the selected target framework's reference assemblies.

- Migrated repository sample projects to the NuGet-resolved `Raven.Sdk` and
  centralized their SDK version in `global.json`, so samples use the same
  project-system contract as distributed Raven applications without requiring
  a globally installed Raven SDK.

- Aligned Raven templates with the .NET CLI's lowercase `--framework` option
  and made net11.0 the default for console, class-library, and Web projects in
  the .NET 11 distribution. Package and installation validation now builds Web
  projects and verifies nanoFramework `.pe`, `.pdbx`, and `.bin` artifacts;
  package validation also starts the Web app and checks its HTTP response.

- Fixed macro token queries to share the semantic model's fragment-region
  snapshot instead of invoking fragment providers again for the same syntax.

- Fixed Raven project-reference loading so command-line builds consume the
  referenced project's MSBuild-built output as metadata, matching the normal
  .NET build model, while workspaces retain source project references for live
  analysis and navigation. Generated Raven class libraries can therefore be
  consumed without merging their source into the application's assembly or
  hiding its `Main` entry point. Dependent workspace compilations and diagnostic
  caches are now invalidated when a referenced project changes. Package
  validation also runs and publishes a Raven consumer and exercises the same
  generated library from C#.

- Added the NuGet-resolved `Raven.Sdk` MSBuild Project SDK on top of
  `Microsoft.NET.Sdk`. Generated projects now pin the matching Raven SDK and
  build through ordinary `dotnet restore`, `build`, `run`, and `publish`
  commands without machine-specific compiler paths. The SDK adds lockstep
  implicit `Raven.Core` and `Raven.Macros` packages, and failed builds retain
  Raven diagnostics without printing the complete compiler reference command.

- Added a lockstep `Raven.Templates` NuGet template package for normal
  `dotnet new` usage. Console, class-library, ASP.NET Core, and .NET
  nanoFramework templates now share their canonical project content with the
  corresponding `rvn init` scaffolds; package validation installs and renders
  all variants in an isolated .NET CLI home, builds each result, and executes
  the console application.

- Fixed project compilation on Windows when the intermediate output path ends
  in a directory separator, and diagnose executable file-scope code in every
  source file when more than one file contributes top-level statements.
  `rvn init` now creates `src/Main.rvn` with an explicit `func Main` for console
  projects and a declaration-only source file for class libraries.

- Fixed installed `rvn build`, `rvn run`, and `rvn clean` commands so canonical
  `Raven.Sdk` projects own their MSBuild target selection instead of receiving
  a command-line `LanguageTargets` override. This preserves the .NET SDK's
  reference-assembly configuration across incremental `dotnet run` builds and
  avoids missing `refint` outputs. `rvn init` also emits a standard TFM.

- Fixed SDK installers to accept checksum manifest paths both with and without
  a leading `./`, and normalized future release manifests to use bare asset
  names. Added a separate manual post-publication workflow that exercises the
  public SDK installers across all release targets and installs the public VSIX
  into clean VS Code instances on Windows, Linux, and macOS.

- Bundled the VS Code extension host and its runtime dependencies into one
  production JavaScript entry point. VSIX packages now include repository and
  MIT license metadata, exclude development dependencies and intermediate
  outputs, preserve the source manifest version during release packaging, and
  resolve with no production npm audit findings.

- Fixed clean-checkout SDK packaging by bootstrapping the Release compiler host
  before Raven-authored Release projects are built, and using that
  host-compatible compiler while cross-publishing target-architecture SDKs.

- Added lockstep NuGet packaging for `Raven.Core`, `Raven.Macros`,
  `Raven.CodeAnalysis`, and `Raven.Analyzers`, including package metadata,
  symbols, isolated local-feed validation, GitHub release assets, and manual
  matching-tag NuGet.org publication through Trusted Publishing. Distribution
  runs and both publication operations require explicit workflow choices.
  Package validation executes a distributed macro and loads a distributed
  analyzer through the standard NuGet analyzer asset convention.

- Separated documentation-site validation from publication. Relevant pull
  requests continue to build the complete website, while pushes to `main` no
  longer deploy it; GitHub Pages publication requires a manually dispatched
  workflow with an explicit `publish_site` choice.

- Added the opt-in `PreferLoopOverWhileTrueAnalyzer` (`RAV9036`) and a code fix
  that replaces unconditional `while true` statements with `loop`.

- Added explicit Debug and Release compiler optimization policies. Release
  project builds now run an ordered pipeline of specialized bound-tree
  rewriters, beginning with conservative pattern algebra, Boolean-expression
  and comparison simplification and literal branch pruning. Release omits
  debug-only IL padding except for stable visible sequence-point anchors, so
  portable PDBs remain valid for symbol consumers. Debug remains the unchanged
  default path, shared semantic lowering stays configuration-neutral, and the
  evaluated MSBuild `Optimize` property can override the configuration default.

- Reduced default analyzer noise by keeping only correctness and safety checks
  enabled automatically. Built-in analyzers are grouped by diagnostic kind,
  with nonessential members opt-in through individual names or
  `category:<kind>` in `RavenEnabledAnalyzers`; `all` enables the full optional
  set.

- Added lightweight compiler-backed hover signatures to the browser
  Playground for resolved identifiers and members.

- Added indeterminate progress bars for Playground startup and compiler
  operations so disabled controls are not the only indication of work.

- Added diagnostic start locations to the Playground problems list.

- Made header-brand destinations consistent across DocFX, RavenDoc, the
  Playground, and the component-template showcase. Standalone applications
  keep their own root while combined-site builds explicitly point at the
  parent Raven documentation root.

- Diagnosed unresolved types in both tuple-style and field-style union case
  payloads. Hover signatures now preserve the authored unresolved type name,
  including qualified names, and reserve `<Error>` for declarations without
  usable source type syntax.

- Preserved portable PDB symbols while normalizing or retargeting emitted
  assembly references. Final assemblies now retain a matching CodeView identity
  and executable source sequence points, allowing debuggers such as the .NET
  nanoFramework VS Code adapter to bind Raven breakpoints to the selected line.

- Fixed SDK builds so conditionally evaluated `RavenConstant` items are
  forwarded to the compiler process instead of being used only for incremental
  invalidation. Updated the nanoFramework deployment guidance with the
  published RP2040 firmware package names and the tested `nanoff` 2.5.162
  macOS UF2 and device-discovery workarounds.

- Added expression forms of the macro contribution constructs `expand`,
  `replace`, and `introduce`. They can now be used directly in `match` arms and
  other macro expression positions while retaining their standalone statement
  forms; macro lowering, validation, semantic classification, and control-flow
  analysis recognize both shapes. Unused-local and unused-parameter analysis
  now treats macro declarations as callable owners and observes contribution
  operands in either form.

- Added expression forms of `break`, `continue`, `yield`, `yield return`, and
  `yield break`, including labeled `break` and `continue` targets. These forms
  can be used directly in `match`, `if`, null-coalescing, and other expression
  positions, with abrupt branches integrated into type joins and control-flow
  reachability analysis.

- Made member-access completion receiver-aware: value and literal receivers now
  show only referencable instance members, excluding nested types and generated
  backing/accessor symbols, while type receivers retain static members and now
  include accessible nested types.

- Moved Playground completion, compilation, and emitted-program execution into
  a persistent .NET 11 Web Worker so compiler work no longer stalls Monaco's UI
  thread. Raven's semantic access gate now avoids unsupported blocking waits in
  single-threaded WebAssembly runtimes while retaining synchronization when
  multithreading is available.

- Made Playground completion appear immediately after member-access dots and
  automatically after a short, meaningful Raven identifier prefix, while
  keeping compiler requests bounded and out of comments and strings and
  preserving manual Ctrl+Space completion. Standalone `dotnet run` now stages
  documentation-owned snippets before static web assets are prepared, avoiding
  an empty-manifest startup crash.

- Added stable Playground links for public examples, documentation-owned
  snippets, and inline shared source. Only bundled example and snippet IDs can
  opt into automatic execution; invalid links now fall back to Hello World
  with an inline explanation, and documentation snippets remain separate from
  the Playground's visible example picker.

- Made `dotnet build` package `netnano1.0` executables into compact `.pe`,
  `.pdbx`, and complete deployable `.bin` artifacts while staging the compact
  dependency closure for the official nanoFramework VS Code debugger. Added
  checked-in build, launch, and attach configurations, plus a getting-started
  guide covering firmware selection and direct `nanoff` UF2 and wire-protocol
  deployment without sample utility scripts.

- Fixed pattern matching over type parameters constrained to sealed hierarchies
  so permitted subtype patterns bind correctly and covering every permitted
  leaf is exhaustive. Parenthesized union carriers now also extract their active
  member before evaluating a typed property pattern and its whole-value
  designation, while matching the carrier type itself remains valid.

- Updated active .NET 11 samples to target the Preview 7 SDK and matching
  Microsoft ASP.NET Core and OpenAPI packages. The vehicle-costs sample retains
  its compatible EF Core/Npgsql Preview 6 pair pending a Preview 7 provider,
  and the runtime-async sample now selects the Preview 7 SDK explicitly.

- Fixed fresh builds of referenced Raven compiler-plugin projects so nested
  macro compilation inherits the compiler's `Raven.CodeAnalysis` and
  `Raven.Macros` support references. Removed tracked VehicleCosts bisect
  fixtures that were being discovered as samples and leaking into the parent
  project's default source glob.

- Added a distributable Raven `netnano1.0` MSBuild target profile. Standard
  `Microsoft.NET.Sdk` Raven projects now need only select `netnano1.0`, add
  normal device package references, and declare application settings; Raven's
  build assets supply the nanoFramework target identity, core library, metadata
  processor, and reduced-runtime compiler defaults. Direct `rvnc` project
  compilation also recognizes the target and its canonical NuGet assets. Both
  the Blinky and temperature/union/GPIO probes now use concise `.rvnproj` files
  and normal PackageReference restore.

- Added the first SDK-style `netnano1.0` Raven project path. Alternative-runtime
  projects can suppress host framework references and select their target core
  library through MSBuild properties; the compiler and language server preserve
  that evaluated reference surface. The Pico-family Blinky sample now restores
  ordinary nanoFramework `PackageReference` items, builds through its
  `.rvnproj`, and packages the result as an `NFMRK2` image.

- Made synthesized record and union formatting Native AOT-safe by marking
  Raven-generated structured-display types through Raven.Core instead of
  discovering helper methods with reflection. Explicit `--raven-core`
  references now replace project-evaluated references with the same assembly
  identity, preventing stale or configuration-mismatched Raven.Core assemblies
  from controlling emission. Added a Linux x64 CI smoke test that publishes and
  runs the greenhouse sample and rejects trim-analysis or AOT-analysis warnings.

- Added a reproducible Native AOT path to the greenhouse-monitor sample, with
  host RID detection, native publish-and-run validation, RID-specific artifacts,
  and a documented `linux-arm64` workflow for Linux-based Raspberry Pi devices.

- Added typed `extern const` build inputs with required/default declaration
  forms, invariant type validation, `CompilationOptions` support, repeatable
  `rvnc` and `rvn build --constant NAME=VALUE` providers, `.rvnproj`
  `RavenConstant` items, and command-line-over-project-over-source precedence.
  Successful overrides of source initializers now produce a value-redacted
  informational diagnostic so build-time substitution remains visible.
  The nanoFramework Blinky sample now uses this facility for `LedPin` instead
  of generating Raven source.

- Added a minimal nanoFramework Raspberry Pi Pico-family Blinky sample pinned
  to mutually matching 2.0 preview core and GPIO assembly versions, with a
  generic Raven pin-selection probe, board profiles, reproducible
  Raven-to-`NFMRK2` build and deployment images, dry-run deployment commands,
  an explicit RP2350 firmware-support boundary, and a successful Pico WH
  hardware deployment against the matching nanoCLR 2.0 preview runtime.

- Added a reproducible nanoFramework temperature-monitor MVP sample that
  restores a pinned target reference closure, compiles union- and GPIO-based
  Raven source, and packages it as an `NFMRK2` image.

- Made synthesized union value formatting emit against reduced managed runtime
  surfaces that omit `Type.IsPrimitive` and `string.Replace(string, string)`,
  while preserving full escaping when the target provides the replace API.

- Added standalone `rvnc` options for explicit alternative-runtime reference
  closures: `--no-framework-references` suppresses the host .NET targeting pack,
  while `--target-core-library` adds and selects the target core-library
  identity used during emission.

- Added `EmitOptions.TargetCoreLibraryIdentity`, allowing compiler hosts to
  retarget emitted host core type references to a supplied runtime core-library
  identity without loading target assemblies for execution.

- Stopped the compiler driver from copying the host `System.Private.CoreLib`
  into Raven runtime-dependency closures. This prevents the host core library
  from overriding the target runtime's core library during Native AOT publish.

- Union carriers can now implement interfaces with a conventional base list,
  such as `union Failure: IError`. The compiler exposes the interfaces through
  the symbol API, validates required members, rejects class inheritance, and
  emits CLR interface implementation metadata.

- Attached type macros can now replace a union carrier's interface shape and
  introduce members into the carrier, enabling reusable derived capabilities
  such as error contracts.

- Added the reusable `#[Error]` macro to `Raven.Macros`. It makes a union
  implement `System.IError` and supplies a case-aware `Message` property unless
  the union already declares one. Case-level `#[ErrorMessage(...)]` can now
  provide a Raven string expression with payload interpolation; unannotated
  cases retain the default case-aware message.

- Macro declarations can now bind a union case through an `on` parameter typed
  as `CaseDeclarationSyntax`. Case-level attached-macro replacements are
  contextualized inside union declarations instead of being left detached.

- Union members can match cases from their own declaring union without an
  import or carrier qualifier, including `self is MissingValue` and match arms
  such as `InvalidValue(let value)`.

- Promoted the LINQ-style `query!` DSL from the invocable-macro sample into
  `Raven.Macros`, preserving its expression-fragment completion, hover,
  diagnostics, and `Where`/`Select` expansion behavior.

- Added a Playground example that composes the standard `query!`, `quote!`,
  `#[Error]`, and `#[ErrorMessage]` macros in a runnable WebAssembly program.

- Query macro bodies now classify their DSL keywords and project ordinary
  Raven semantic highlighting through source, predicate, and selector fragments.

- Macro fragment locals with authored declaration spans now project local
  symbols onto their declaring DSL tokens, enabling ordinary hover information
  on declarations such as the `value` in `from value in items`.

- Hovering an invocable or attached macro name now includes documentation from
  the macro implementation alongside its macro kind, targets, arguments, and
  expansion command guidance.

- Hardened macro-reference activation so provider-construction failures report
  the provider type and underlying error through `RAVM001`, remain stable across
  repeated diagnostics, and do not disable macros from healthy references.

- Completed boxing and unboxing conversion classification between value types
  and interfaces they implement. This makes struct union carriers usable
  through their declared interfaces from Raven source, not only in emitted CLR
  metadata.

- Preserved inferred union-case type arguments when reporting a failed
  case-to-carrier conversion, rather than falling back to an open generic
  carrier such as `Result<T, E>`.

- Fixed union-case symbol display so type-use formats omit constructor payloads
  while declaration formats show the inherited case signature without repeating
  the containing union's generic parameters.

- Fixed incremental parsing of edited type members so method signature changes
  retain type-member context instead of being misclassified as file-scoped
  function statements. Category-changing edits still fall back to a full parse.

- Removed the alternate `#Name` invocable-macro syntax. Invocable macros now
  use the single `Name!` carrier for argument lists, raw token bodies, or both;
  `#` remains available for directives and attached macro attributes.

- Generalized invocable macro expansion results around a single `SyntaxNode`,
  with typed expression and statement projections and factories. This removes
  expression-only casts from the execution boundary and prepares centralized
  grammar-position validation without adding multi-node complexity to the MVP.
  A bare raw-body invocation used as a whole statement now requires statement
  syntax, while a parenthesized invocation remains an expression; call-style
  invocations retain normal expression-statement behavior, and category
  mismatches produce a compiler diagnostic instead of reaching the binder.

- Extended the normalized invocable-macro result boundary with explicit,
  immutable member-list output. Empty member lists remain distinguishable from
  no expansion, single-node and list outputs are mutually exclusive, and a
  list returned through an expression or statement carrier is diagnosed rather
  than silently ignored.

- Added a dedicated recoverable `Name!` invocation carrier in type-member
  position while preserving the existing statement envelope at file and
  namespace scope, where semantic macro resolution must decide whether an
  invocation supplies statements or declarations. Qualified macro names and
  raw token bodies now share one parser path, and bare postfix `value!` remains
  nullable suppression.

- Unified expression and type-member `Name!` carriers behind the same
  invocable-macro context surface. Macro authors now use `Syntax`, `Name`,
  `ExclamationToken`, `ArgumentList`, and `TokenTree` consistently instead of
  depending on an expression-only syntax type.

- Added semantic-model expansion for invocable macros in type-member position.
  Expanded documents now splice generated members in order, support explicit
  empty-list removal and single-member results, and diagnose incompatible
  expression or statement output while preserving recoverable source.

- Extended member-producing invocable macros through compiler declaration
  binding and emission. Type-body, namespace, and file-scope invocations can
  now contribute real members; ambiguous file/namespace carriers preserve
  statement behavior until the semantic expansion result selects declarations.

- Completed compact member-producing macro declarations. A
  `SyntaxList<TMember>` return annotation now selects namespace- and type-member
  positions, `expand` normalizes the list into the invocable result, and the
  generated provider preserves those targets through the class-authored ABI.
  Class providers can declare the same positions with `InvocationTargets`.

- Documented the execution, output, input, and tooling boundary between macros
  and source generators: macros transform an explicit authored site inside the
  compiler, while generators add project-wide generated documents through the
  workspace or build host.

- Added normalized macro application metadata that separates attached versus
  invocable application from grammar targets. Macro syntax parameters now expose
  the supplier-oriented `SyntaxInput`, `Context`, and `TokenBody` roles, preserve
  source-backed expression arguments, and rely on ordinary call-site binding to
  infer and validate expanded values.

- Reusable compiler-plugin projects can now place a bare
  `[assembly: RavenCompilerPlugin]` marker and compact `macro` declarations in
  the same source file. Partitioning retains the marker in the emitted plugin
  while lowering only the declarations into provider adapters. Bare-marker
  discovery exports public declarations and provider classes; non-public
  macros remain local to their defining project.

- Simplified Raven-authored macro declarations to `macro Name(...)`, removing
  the redundant `func` keyword. The compiler API now exposes
  `MacroDeclarationSyntax`, `IMacroDeclarationSymbol`, and `SymbolKind.Macro`,
  while semantic tokens classify macro names distinctly for editor clients.

- Simplified Raven-authored macro control flow: `expand` now supplies the final
  expansion and returns from the current execution path, while `replace`,
  `introduce`, editor metadata, and reported diagnostics accumulate until an
  explicit expansion or body fall-through. All macro contexts now expose
  ordinary diagnostic-reporting APIs, and attached macro declarations can request
  a compiler-supplied `AttachedMacroContext` without adding an invocation
  argument.

- Preserved caller-authored syntax reused by invocable macro expansions as
  ordinary visible syntax instead of debugger-hidden generated plumbing.
  Semantic queries now project bound nodes, types, symbols, and contextual
  lambda parameters through that expansion even when the macro has not already
  been bound by another query.

- Added deterministic `MacroContext.CreateUniqueName` allocation for generated
  bindings. It avoids identifiers authored in the invocation document and
  names previously allocated by the same macro context, without conflating
  textual collision avoidance with future call-site/definition-site hygiene.

- Added diagnostic-first `MacroContext.RequireSyntax<TSyntax>` validation for
  macro transformations. Category mismatches now accumulate a source-located
  error and return safely instead of requiring casts or exceptions, including
  exact authored-origin locations for parsed DSL fragments.

- Added cursor-based Raven fragment parsing to macro token streams. Macro
  authors can parse one expression, statement, type, or pattern from the
  current token, receive its recovered syntax and body-relative span, and
  continue reading the outer DSL without precomputing an end span.

- Added invocation-scoped type and symbol inspection helpers for Raven
  expressions parsed by token-tree macros.

- Added cursor-based single-member parsing without weakening exact-whole-region
  member validation.

- Added a parse-result source-provenance overload for mapping generated macro
  syntax back to its authored fragment.

- Added concise structural and factory-form syntax inspection helpers for
  macro authors.

- Macro syntax parse results can now be reported directly through
  `MacroContext.ReportDiagnostics(result)`.

- Added `MacroContext.CreateUniqueIdentifier` for constructing fresh generated
  identifier syntax without repeating name allocation and factory calls.

- Aligned documentation and VS Code grammar highlighting for macro
  declarations, contribution keywords, attributes, target clauses, and
  invocable `Name!` invocations, and made constructor access modifiers and
  union cases with or without payloads consistent.

- Added a standalone Blazor WebAssembly host and GitHub Pages publishing path
  for the experimental component-template showcase, sharing the same Raven
  components, source presentation, styles, and interactions as the Server host.
- Fixed portable-PDB correction when a normalized method maps to an omitted
  trailing `MethodDebugInformation` row, preventing Release compiler bootstrap
  failures in assemblies whose final methods have no debug information.
- Fixed the experimental component-template WebAssembly host startup by
  including the .NET runtime import map and the Raven component assembly's
  explicit runtime dependencies in its published page.

- Added `samples/projects/macro-dsl`, a minimal Raven-authored token DSL that
  demonstrates macro-local keywords, embedded Raven expression regions,
  native fragment diagnostics, expansion, and debugger source provenance in a
  standalone plugin-and-application project.

- Fixed portable-PDB method ownership after assembly-reference normalization.
  Sequence points and local scopes are reconciled against the final PE method
  rows, async and iterator bodies remain attached to their generated
  `MoveNext` methods, and overlapping visible spans within a method are
  suppressed. This restores stable stepping for ordinary, top-level, match,
  async, iterator, and macro-authored executable code.

- Added compiler-owned debugging provenance for executable macro fragments.
  Parsed expressions and statements retain authored `.rvn` locations,
  string-built expansions can provide span mappings, and portable PDBs hide
  unmapped generated plumbing. The VS Code Raven debugger can now build and
  launch a separate .NET startup project, enabling F5 debugging of the Blazor
  template sample through its web host and launch profile.

- Fixed expanded macro documents so generated declarations retain line breaks
  between adjacent members and receive context-aware indentation in compiler
  expanded-source projections, including the VS Code expanded-syntax view.

- Added compiler-owned script submission chains and a dedicated submission
  binder. Variables, functions, and types from earlier submissions now
  participate in semantic binding without being inserted into the current
  top-level lexical scope.

- Script submissions now persist top-level variable values in typed execution
  slots. Later submissions can read and update those values without rerunning
  earlier submission bodies; the storage code path is inactive for ordinary
  files and projects.

- Script submission functions and user-defined types now flow through emitted
  metadata references, allowing later submissions to call and instantiate them
  through normal CLR member references. Submission compilation state and its
  code-generation bridge are isolated behind dedicated internal components.

- Added Raven-owned `RavenScript`, `Script`, `ScriptOptions`, and `ScriptState`
  hosting APIs. Script states execute only the newest submission, retain
  variables and emitted declarations across continuations, and own a
  collectible assembly-loading session that can be released explicitly.

- Script states now expose the typed value of a submission's trailing
  expression through `HasReturnValue` and `ReturnValue`, enabling `eval` and
  interactive hosts without rewriting source text.

- Added `rvn eval <code>` and an MVP `rvn repl`. The REPL retains variables,
  functions, and types across submissions, uses compiler-owned completeness for
  multiline input, and supports `:load`, `:reset`, `:references`, `:help`, and
  `:quit`.

- Centralized top-level function classification and added a semantic-model
  submission declaration projection. Submission functions now bind against
  prior submission state, while PE code generation resolves repeated metadata
  type names against their containing assembly rather than an unrelated cached
  assembly. Normal compilation keeps its existing file-local function policy.

- Added `SyntaxTree.GetSubmissionCompleteness()` for script and interactive
  source. Hosts can distinguish complete submissions, trailing incomplete
  constructs that need more input, and complete submissions with syntax errors.

- Fixed **Raven: Run Active File/Project** in the VS Code extension so an active
  project-backed source runs its owning `.rvnproj`, compiling all project source
  files, while standalone sources continue to run as isolated file applications.

- Fixed nullable generic substitution so Raven's unified nullable symbol is
  preserved independently from its CLR ABI projection. Constructed type
  and method symbols now retain whether the original signature projects to its
  underlying type or `Nullable<T>`, preventing invalid calls such as
  `EventCallback<int>.InvokeAsync` while keeping compiler API nullability intact.
- Metadata symbol loading now falls back safely when an optional dependency is
  absent from a metadata load context instead of crashing type classification.
- Compiler API consumers can query a nullable type's CLR representation through
  `GetNullableAbiProjection()`. The total API distinguishes non-nullable types,
  annotated underlying-type projections, and `System.Nullable<T>` projections
  without exposing Raven's internal nullable symbol implementation.
- Added the off-by-default `EnableIsNotNullNarrowing` compatibility option.
  A direct `value is not null` condition narrows a local or parameter only in
  its true branch, and semantic type information and hover show the contextual
  non-null type there. Raven's canonical `if let` and type-pattern bindings are
  unchanged.

- Raven MSBuild projects now implicitly include `.rvn` source files through the
  standard `Compile` item contract. Set `EnableDefaultCompileItems` to `false`
  and add explicit `Compile` items when a project needs a curated source list.

- Removed the legacy `RavenCompile` MSBuild item. Raven projects now use the
  same `Compile` item contract as SDK-style C# projects, and checked-in Raven
  projects and test fixtures have been migrated accordingly.

- Removed the legacy `Raven.MSBuild.targets` host-project bridge and its
  `RavenProjectFile`/`RavenOutputDir` contract. Raven projects now build as
  ordinary `.rvnproj` projects and participate through `ProjectReference`.

- Removed support for the legacy `.ravenproj` project format from Raven's CLI,
  compiler driver, documentation tool, workspace routing, and VS Code tooling.
  `.rvnproj` is now the sole Raven project-file extension.

- Removed the legacy Raven and composite workspace project-system services.
  `RavenWorkspace`, `rvnc`, and compiler-plugin builds now use the MSBuild
  project-system service directly.

- Migrated project-system, package/framework-reference, generated-source,
  analyzer-option, compiler-output, and file-watcher fixtures to SDK-style
  `.rvnproj` files, and removed obsolete custom-solution persistence coverage.

- Removed the custom `ProjectFile` and `SolutionFile` XML serializers and the
  obsolete file-path solution persistence APIs. Project loading and saving now
  remain entirely on the MSBuild-backed `.rvnproj` path.

- Renamed the Raven.Core sources from legacy `.rav` to `.rvn` and removed its
  explicit `Compile` list. Raven.Core now uses the same implicit source-item
  behavior as other SDK-style Raven projects.

- Documented the remaining MSBuild parity boundary: Raven still needs a
  distributable MSBuild SDK with conventional props/targets, stronger
  design-time and reference-assembly support, and less custom dependency flow.

- Raven's MSBuild compile target now preserves the active `Configuration` and
  inner-build `TargetFramework` when `rvnc` evaluates a project, including its
  project references and compiler plugins. Multi-targeted Release builds no
  longer fall back to Debug or the first declared target framework.

- Raven project evaluation now ignores the SDK-injected
  `Microsoft.NETCore.App` framework item. The workspace already supplies the
  target framework reference assemblies, so package-only projects no longer
  trigger a redundant restore.

- MSBuild-backed `.rvnproj` workspace evaluation now applies `.editorconfig`
  diagnostic severity and suppression settings to compilation options.

- Fixed Raven MSBuild incrementality so unchanged projects skip `CoreCompile`.
  Logical package and framework-reference names are no longer treated as input
  file paths; their resolved assemblies remain tracked through `ReferencePath`.

- `dotnet clean` now removes Raven-owned generated sources, intermediate and
  copied documentation trees, dependency manifests, and runtime dependency
  artifacts from the active build configuration and target framework.

- Added optional target types to macro expression fragments through
  `CreateExpressionFragmentRegion`. Macro DSLs can now give authored inline
  lambdas normal parameter inference and hover without exposing private DSL
  trees. Optional provider failures remain isolated, and visible-value lookup
  now handles detached recovered fragment syntax without crashing hover.

- Fixed `TextSpan` value equality metadata so Raven code can use `==` and `!=`
  without introducing an unsupported nullable conversion during emission.

- Extended the HTML-to-Blazor macro prototype so component parameters of type
  `EventCallback` or `EventCallback<T>` accept either callback references or
  inline Raven lambdas. The macro now target-types the authored expression as
  `Action` or `Action<T>` and emits the matching `EventCallback.Factory.Create`
  wrapper, removing that boilerplate from component code.

- Extended macro token symbol projection to contextual DSL references. The
  HTML-to-Blazor sample now resolves component attributes to their ordinary
  component property symbols, enabling standard hover and go-to-definition
  without exposing the macro's private parser representation. Token lookup
  follows reported Raven fragments into nested macro invocations, explicit
  DSL token associations take precedence over containing fragments, and macro
  invocation hints no longer occupy unresolved text inside token-tree bodies.

- Updated the HTML-to-Blazor sample so component parameters and matching DSL
  attributes follow .NET PascalCase naming conventions.

- Extended the HTML-to-Blazor sample with an ordinary Razor `StatusBadge`
  component from a referenced Blazor project. The HTML macro now also accepts
  qualified component tags and maps their terminal type token and parameters
  to ordinary compiler symbols for editor tooling. The badge's co-located
  `.razor.css` demonstrates that components instantiated from Raven templates
  retain Blazor's normal CSS-isolation pipeline.

- Added evaluated `MacroOption` project items as a general immutable build-to-
  macro configuration channel. The HTML-to-Blazor sample now uses it with the
  Static Web Assets SDK to rewrite, bundle, publish, and apply isolated
  `Counter.rvn.css` without generating Razor files or implementing a CSS parser.

- Fixed workspace loading for compiler-plugin project references so the built
  plugin assembly is available as both a macro provider and an ordinary
  metadata reference. Macro libraries can now expose runtime helper types used
  by their expansions without producing false editor diagnostics.

- Fixed macro-fragment semantic queries over collection comprehensions and
  nested macro expansions so detached fragment syntax no longer crashes while
  creating synthesized locals, lambdas, or parameters. Hover now resolves the
  outer comprehension source, condition, and iteration-local members. Nested
  token-tree macro fragments inherit the lexical scope at their invocation, so
  HTML expressions inside a comprehension resolve the item, its members, and
  caller callbacks without HTML-specific language-server logic. Hover uses
  authored names for compiler-synthesized comprehension locals and avoids
  presenting a nested macro's generated lambda as the authored DSL symbol.

- Added optional ordinary-symbol targets to macro token metadata. Outer DSL
  tokens now reuse normal VS Code hover and go-to-definition, with the HTML
  sample resolving component tags such as `<Greeting>` without exposing its
  private parser structure or adding HTML-specific language-server code.

- Added compiler-owned semantic lookup and normal VS Code hover and
  go-to-definition presentation
  for ordinary Raven expression and statement fragments inside token-tree
  macro DSLs. Hover resolves caller locals, macro-introduced typed locals, and
  their members through the same span metadata used by completion. Optional
  declaration spans let DSL-introduced locals navigate back to their authored
  token, without an HTML- or query-specific editor integration.

- Added `fragment` and `token` contributions to token-tree `macro`
  declarations, so Raven-authored DSL macros can publish ordinary Raven
  expression, statement, type, pattern, or member regions, typed locals, and
  token kind/classification metadata without separate provider classes.
  Dedicated providers remain supported when tooling must be independent from
  full expansion.

- Extended the isolated HTML-to-Blazor macro sample with expression-based
  conditional content, canonical prefix match rendering, filtered
  list-comprehension rendering, native Blazor keys, and interactive
  Match and Todo showcases whose model changes re-evaluate the rendered
  expressions. The Match showcase now renders and destructures payload-bearing
  union cases instead of matching integer phases.

- Fixed collection-comprehension iteration locals captured by nested lambdas
  so each generated delegate snapshots the current iteration value instead of
  reading an uninitialized shared closure field.

- Added end-to-end DSL-tooling acceptance coverage that compiles the checked-in
  HTML macro and verifies cached token/fragment snapshots, cursor routing, and
  authored diagnostics. The HTML/Blazor showcase now highlights its Raven code
  and relies on inferred `unit` return types for effect-only functions.

- Added `MacroInputSnapshot` as the compiler-owned combined view of a
  token-tree invocation's classified tokens and embedded Raven regions, with
  deterministic source ordering and most-specific cursor lookup. The language
  server consumes this combined view for macro semantic highlighting.

- Added compiler-owned macro token snapshots with provider raw kinds, authored
  spans, stable standard or provider-defined kind names, keyword overlays, and
  optional lightweight token classifications.
  Invalid or failing optional metadata is normalized per token rather than
  discarding otherwise valid snapshot data.
  Token providers remain independent of Raven's global `SyntaxKind` values.
  Token and embedded-fragment tooling results are cached by the owning semantic
  model for the immutable invocation snapshot.
  The language server now projects available macro token classifications into
  semantic highlighting without loading or invoking providers itself.
  The HTML-to-Blazor prototype supplies lightweight body-token classifications
  in addition to its embedded Raven expression regions.

- Added an optional token-tree macro fragment-region provider and compiler API
  for surfacing body-relative expression, statement, type, pattern, and member
  spans as authored source regions without exposing a macro's private DSL tree.
  The HTML-to-Blazor sample now publishes its embedded Raven expression spans
  through that contract. Ordinary Raven completion now routes through reported
  regions using the invocation's caller scope, including local-symbol and
  member-access completion, without requiring a DSL-specific completion API.
  The Raven-authored LINQ-like query sample now proves the same contract for
  its source, predicate, and projection fragments. Fragment regions can also
  carry compiler-owned locals, with a focused sequence-element helper allowing
  the query DSL's range variable to receive typed member completion in its
  predicate and projection without exposing generated lambdas. Schema-backed
  DSLs can provide an explicitly typed local directly.

- Added exact-one member-declaration parsing for standalone syntax construction
  and token-tree macro bodies. Empty, multiple, import-only, and global-
  statement inputs are rejected instead of silently selecting a declaration.

- Added source-backed type, pattern, and compilation-unit parsing to token-tree
  macro contexts, with diagnostic-bearing results mapped to authored body
  locations, plus matching `SyntaxFactory` entry points for standalone text.
  Recovered macro fragment syntax now also retains its authored source
  position instead of being rooted at position zero.

- Fixed empty collection expressions targeting `ImmutableArray<T>` crashing
  emission when the selected zero-argument static factory is metadata-marked
  as an extension method.

- Fixed bare type patterns such as `value is BaseType` being bound as value
  constants and emitting invalid equality calls.

- Fixed `rvnc` runtime dependency copying for `Microsoft.NETCore.App.Ref` and
  `Microsoft.AspNetCore.App.Ref` inputs so executable projects copy runtime
  assemblies instead of unloadable reference assemblies.

- Restricted authored union bodies to cases, computed properties, indexers,
  ordinary methods, and computed static properties. Storage properties and
  other member kinds now report diagnostics, keeping union representation and
  construction compiler-controlled in line with the C# union model.

- Changed the body-declared union case ABI to emit non-generic cases inside the
  union and generic-union cases inside an explicitly annotated non-generic
  companion. Raven merges the carrier and companion during metadata import, so
  case lookup, `import Union.*`, preludes, matching, and semantic ownership stay
  unchanged while each CLR case carries only the generic parameters it uses.
  The carrier retains the C# union constructor, `Value`, and `TryGetValue`
  surface, and C# can construct and match cases as `Result.Ok<T>` and
  `Result.Error<E>`.

- Raven.Core nullable conversions and JSON naming-policy handling now use
  explicit typed pattern bindings, and `Option` construction is qualified to
  remain unambiguous against .NET 11 framework metadata.
- Union cases now support positional unnamed payloads such as
  `case Some(T)` and `case Pair(int, string)`. They project public `Value` or
  `Item1`/`Item2` properties and stable generated constructor parameter names
  for .NET interop, while normal Raven symbol display keeps the names omitted.
  Named and unnamed payload forms cannot be mixed within one case. Raven.Core's
  `Option.Some`, `Result.Ok`, and `Result.Error` now use the positional form.
- Stabilized pre-nested union case construction and emission. Imported
  free-standing cases retain contextual carrier conversion in constructor
  arguments, explicit case-typed expressions emit the case value itself,
  nullable value payloads emit valid `Nullable<T>` values, and carrier display
  preserves the carrier's complete generic type arguments.
- Canonicalized framework collection contracts loaded through runtime and
  facade assemblies so Raven Core extensions bind consistently in mixed
  reference environments such as the browser playground.
- Fixed generic method inference so method-group adaptation cannot widen a type
  argument already inferred from an ordinary argument.
- Fixed bound-tree traversal through assignment expressions so async capture
  analysis preserves locals used across suspension, including await-for
  enumerators.
- Fixed closure emission for target-typed lambdas inside Result-propagating
  namespace functions when replayed binding omitted an outer parameter from the
  recorded capture set.
- **Breaking:** Raven no longer performs nullable-specific control-flow
  refinement. A value with static type `T?` remains nullable after direct null
  checks and must be explicitly pattern-bound, converted, conditionally
  accessed, or suppressed before dereference. `TypeInfo` no longer exposes a
  separate null-flow result, `CompilationOptions.EnableNullFlowAnalysis` has
  been removed, and `RAV0402` now reports a static nullable-value access.
  Nullable metadata remains part of source/PE boundary projection; in
  particular, `MaybeNull` return contracts produce statically nullable results.
- Parenthesized expressions now pass their contextual target type to the
  enclosed value, keeping target-bound union cases stable after cold queries.
- Cold contextual semantic queries now seed preceding lexical declarations
  before binding their statement. Lambda parameters resolve against their
  contextual delegate instead of falling through to a later shadowing local.
- Parenthesized simple lambdas such as `(value => value)` are now parsed as
  parenthesized expressions, while `(value) => value` remains the distinct
  parenthesized-parameter lambda form.
- Target typing now reaches only the value-producing branches of nested `if`
  expressions and the trailing value of block expressions. Generic union cases
  in nested match arms retain their constructed carrier type without restoring
  ambient target-type leakage into unrelated descendants.
- Missing closing parentheses in function and method parameter lists now
  recover at return annotations, constraint clauses, and body boundaries.
  Later declarations remain independently parseable and bindable during edits.
- Binder target-type scopes are now attached to the expression they target
  instead of flowing ambiently into every descendant. Composite operands keep
  their own semantics, while genuinely target-dependent forms such as enum
  member bindings receive their context explicitly.
- Constructed nested generic members now canonicalize substituted self-types
  back to their current constructed receiver. Source and PE conversion
  operators consequently preserve both outer and inner type arguments in their
  containing and return types.
- Binary-expression result targets no longer flow into their operands. A
  Boolean context such as a catch filter therefore preserves the method return
  target of a nested `return` expression instead of coercing its value to
  Boolean before operator binding.
- Catch filters now participate in public reachability analysis as required
  expressions. Nested `return` expressions make the catch body unreachable
  without re-entering method binding during diagnostic production.
- Public control-flow analysis now recognizes abrupt expressions in required
  conditions, loop inputs, lock receivers, and match scrutinees. A nested
  `return` makes dependent branches and following statements unreachable in
  either semantic-query order.
- Postfix null suppression now has an explicit bound-tree identity and produces
  a non-nullable expression type. Inferred locals no longer
  retain the nullable operand type or report a false possible-null dereference.
- Reachability analysis now recognizes nested `return` expressions inside
  eagerly evaluated invocation arguments, unary operands, receivers, indexers,
  assignments, and object construction. Following statements are marked
  unreachable, and public control-flow results include the nested return node.
- User-defined conversion classification now selects an exact source or target
  operator independently of declaration and semantic-query order instead of
  returning the first applicable member. Competing candidates are compared on
  both standard-conversion legs, while exact implicit operators retain a fast
  path for reference-heavy builds.
- Raven-emitted generic interfaces now preserve `in` and `out` variance in
  their CLR generic-parameter metadata. Reloaded PE symbols consequently apply
  the same covariant and contravariant interface conversions as source symbols.
- Imported type-resolution scopes are deduplicated by compiler scope identity
  instead of recursively comparing complete symbol graphs. This removes
  pathological symbol-comparison work from reference-heavy Raven builds while
  retaining each distinct source and metadata scope.
- Constructed generic properties, indexers, and events now project accessors
  back to the constructed member, and substituted parameters are owned by their
  constructed method rather than directly by the containing type. Raven source
  and emitted PE symbols now agree across the complete member graph.
- Metadata `[MaybeNull]` output contracts now project call results as statically
  nullable Raven types when the underlying type can represent null.
- Generic inference and constructed signatures now preserve type parameters
  nested through nullable arrays, tuples, and constructed generic arguments.
  Raven tuple projections compare consistently with emitted
  `System.ValueTuple` signatures, and incomplete available-state inference
  falls back to authoritative binding instead of dropping a generic overload.
  Mixed suffixes such as `T?[]`, `T[]?`, and `T?[]?` also retain their written
  nesting order in the syntax tree.
- Raven-emitted generic type and method parameters now preserve `notnull` when
  reloaded from metadata. Constraint round-trip coverage also locks `class`,
  `struct`, `new()`, base-class, and interface constraints across source and PE
  symbols.
- Nested constructed generic types now substitute outer type arguments inside
  their own parameter constraints for both source and metadata symbols.
  Constructor binding validates the final constructed type, so dependent
  constraints cannot be bypassed by using a generic nested type as a call
  target.
- Nullable match exhaustiveness now preserves the complete non-null pattern
  domain instead of reducing it to one opaque underlying-type case. Booleans,
  enums, and sealed hierarchies can be covered case by case alongside `null`;
  open hierarchies still require a base-type or `_` fallback. Missing-case code
  fixes now generate typed sealed-hierarchy bindings from nullable scrutinees.
- Nullability documentation now defines one coherent Raven policy: unified
  nullable symbols for reference and value types, strict flow-based proof of
  safe access, pattern-first handling, `Option<T>` for domain absence, and
  direct null checks as supported compatibility forms. Compiler and analyzer
  configuration are documented as policy controls rather than alternate type
  systems.
- Generic inference for `ref`, `out`, and `in` arguments now consumes the
  referenced element type instead of leaking compiler-only address/ref wrapper
  symbols into constructed method type arguments. Exact by-reference kind and
  addressability checks still run during overload applicability.
- Semantic queries after an event- or property-accessor body edit now recover
  the accessor's method binder directly, preserving accessor parameters and
  edited local types without forcing complete source declaration binding.
- Generic calls rejected by type-parameter constraints now retain the inferred,
  constructed method as their semantic candidate. Nullable generic parameters
  are projected consistently through Raven's unified nullable symbol model for
  both source and metadata methods.
- Control-flow analysis now lets an abrupt `finally` replace pending loop
  transfers before publishing exits. A `continue` in `finally` therefore
  suppresses an enclosed `break` instead of making a non-completing loop appear
  to have a reachable endpoint.
- Lazy source member declaration now preserves every constructed containing-type
  layer when rebuilding nested receivers. Rejected generic overload candidates
  consequently expose the same projected constraints and containing types as
  their metadata equivalents.
- Public type information for constructor invocations now reports the
  constructed containing type instead of the constructor's `unit` return type,
  including every construction layer of nested generic types.
- Constructing an error type through the public named-type API now preserves the
  same recovery symbol instead of throwing `NotSupportedException`, keeping
  semantic tooling total while source is incomplete. Error types also identify
  themselves as closed type definitions through the ordinary symbol contract.
- Local functions declared in nested statement and expression blocks no longer
  leak into enclosing scopes through their emitted container. Lexical function
  overload sets are restored on block exit, and semantic lookup admits local
  functions only through an enclosing block.
- An incomplete generic constraint clause such as `where T:` now reports a
  localized missing-constraint diagnostic while retaining the declaration and
  later sibling declarations for semantic queries during edits.
- Constant-false catch filters likewise exclude their unreachable catch-body
  mutations from the null-state joined after a `try` statement.
- Cold public symbol queries now apply a method group's contextual delegate
  conversion instead of returning an unresolved candidate set. Imported
  generic and non-generic namespace-function overloads therefore select the
  same method before and after diagnostics are collected.
- Lexical function scopes now retain complete overload sets instead of replacing
  an earlier overload with the last declaration of the same name. Higher-order
  generic calls therefore see generic and non-generic namespace-function
  overloads consistently, independent of declaration and semantic-query order.
- Semantic queries over object-initializer syntax no longer throw during edit
  recovery. Attached initializers report their containing construction type,
  while temporarily detached recovery nodes produce an error result.
- Conditional access now uses an expression-local non-null receiver conversion,
  preventing false nullable-input diagnostics when invoking source extension
  methods through `?.` without refining the original storage.
- Reuse unchanged local macro partitions even when they contain authored
  diagnostics, remapping those diagnostics after consumer-only workspace edits
  instead of recompiling the macro partition.

- Overload resolution now prefers an exact non-generic method over a generic
  candidate whose inferred construction produces the same parameter sequence,
  independent of declaration order. The same rule selects methods when a method
  group is converted to a delegate.
- Raven.Core's union JSON converters now preserve nullable payloads through
  reflection by carrying the declared case/member type separately from the
  possibly null value. Their Raven signatures also match the nullable .NET
  contracts used by `JsonSerializer` and reflection invocation.
- By-reference overload applicability now follows CLR signature identity for
  nullability: nullable reference annotations do not make an otherwise matching
  `ref`, `out`, or `in` argument inapplicable, while nullable value types remain
  distinct.
- Generic method inference now widens repeated type-parameter bounds for a
  base/derived argument pair instead of depending on argument order. Partial
  explicit type arguments on namespace functions also remain open through
  invocation binding, so trailing fixed arguments and leading inferred
  arguments are combined by the shared overload resolver.
- Invocation and property-assignment inputs now honor .NET nullability
  contracts. Non-nullable and `DisallowNull` inputs reject null or nullable
  values, `AllowNull` accepts them without changing the declared/read type, and
  PE property attributes are available through the public symbol model.
- Source named types now expose the same local CLI `MetadataName` as PE named
  types, including generic arity but excluding namespace and containing-type
  paths. `ToFullyQualifiedMetadataName` owns complete identities, and emission
  now uses it explicitly for top-level types while retaining nested builders.
- PE by-reference parameters now retain their nullable element annotation,
  using write-state nullability for `out` parameters even when reflection
  exposes the annotation on the root by-ref node.
- Constrained generic overloads now participate in fast semantic-query ranking
  only when their constraints are satisfied. A generic identity conversion can
  beat an `object` fallback, while a rejected generic candidate leaves the
  fallback selectable, independent of declaration and diagnostic query order.
- Fast semantic-model queries now apply generic method constraints before
  publishing an invocation target. Dependent constraints such as
  `TDerived: TBase` work for explicit and inferred calls through emitted Raven
  libraries; invalid calls expose a failed candidate instead of a selected
  method.
- Name-based PE member lookup now resolves nested generic types by their
  Raven-facing name (for example, `Inner` as well as the CLI name `Inner`1`).
  Constructed nested generic types and their methods retain equal source/PE
  identity across emit and reload, including inherited containing-type
  substitutions.
- `ConstructedMethodSymbol` object equality and hashing now use its constructed
  signature identity instead of proxying to the open method definition.
  Reflexive and independently reconstructed methods behave consistently in
  ordinary sets as well as with `SymbolEqualityComparer`.
- Source and PE symbols now retain stable identity across an emit/reload generic
  boundary. PE modules expose their scope name and containing assembly when
  reflection omits a module file name, and source generic method
  `MetadataName` follows the CLI name rather than documentation-ID
  double-backtick notation.
- Definite-assignment analysis for `out` parameters now joins the actual exits
  from `loop` statements. Assigning before every reachable `break` satisfies
  the contract, while any unassigned break path still reports `RAV0269`.
- Removed the unused placeholder local table and throwing declaration path from
  `LocalScopeBinder`. Lexical locals remain owned by `BlockBinder`, leaving the
  local-scope binder as the forwarding semantic boundary it actually provides.
- `IForLoopOperation` now exposes source `for let` patterns and no longer
  reports the binder's synthetic iteration temporary as the loop local.
  `while let` and pattern-based `for` loops are covered as first-class public
  operation shapes.
- PE method and parameter symbols now project nullable flow attributes from
  referenced assemblies, including return-level `MaybeNull` and parameter-level
  `NotNullWhen` constructor values. The metadata decoder is shared across
  method, return, and parameter attributes.
- Higher-order generic calls now preserve constraint failures discovered while
  constructing a method-group argument. Passing a constrained generic function
  with incompatible inferred type arguments reports the constraint violation
  instead of silently accepting the invocation.
- Symbol queries for method-group arguments now use the enclosing invocation's
  contextual binding before falling back to the visible open declaration. This
  keeps inferred method symbols stable across cold queries, diagnostic queries,
  and workspace edits.
- Constraint clauses that name undeclared type parameters now report `RAV0360`
  for functions, methods, function expressions, and macro declarations instead of
  being silently ignored.
- Duplicate lexical bindings in the same scope now report `RAV0167` as binding
  errors. `RAV0168` remains a warning for intentional or accidental shadowing
  across nested scopes.
- Unsupported or programmatically constructed literal syntax now produces an
  invalid-expression diagnostic and bound error expression instead of throwing
  from semantic binding.
- Incomplete interpolated strings remain bindable as strings during edits, and
  unknown constructed content recovers through an invalid-expression
  diagnostic instead of an exception.
- Removed the obsolete `TypeUnionSymbol`, `ITypeUnionSymbol`, and
  `TypeKind.TypeUnion` compiler API and all associated conversion, inference,
  exhaustiveness, emission, analyzer, and language-service behavior. Raven's
  union feature remains independent; union syntax no longer falls back to a
  synthesized or common-base type when its required union definition is absent.
- `GetTypeInfo` now reports contextual conversions consistently for return,
  assignment, and argument expressions. In particular, explicit return values
  converted to a standard-union carrier retain their natural type while
  exposing the carrier as `ConvertedType`, independent of expression shape,
  available-state path, and query order.
- Bound-tree walkers now use generated expression and statement dispatch and
  accept every bound-node family without throwing on non-block statements.
  This prevents analysis and lowering walkers from silently skipping newly
  introduced bound expressions.
- Pattern binding now recovers from unsupported synthetic pattern kinds with a
  normal invalid-term diagnostic and error pattern instead of throwing. This
  keeps semantic APIs total for edited or programmatically constructed trees.
- Constant evaluation now folds logical negation of Boolean constants, so
  control-flow and missing-return analysis recognize loops such as
  `while !false` as non-terminating unless a reachable break exists. Boolean
  conjunction, disjunction, and same-type constant equality are folded as well.
- Control-flow analysis now recognizes parenthesized, converted, and
  all-abrupt `if` and `match` expressions as non-completing, including when
  such an expression is nested in a local initializer.
- Nullable type symbols now project the underlying type's declared members for
  both reference and value types. Public `GetMembers` therefore agrees with
  `LookupType` and `IsMemberDefined` instead of exposing only base-type members.
- Nullable symbol APIs now distinguish structural inspection from total
  normalization: `TryGetNullableUnderlyingType` exposes nullable wrapping,
  `GetNonNullableType` replaces the ambiguous `GetPlainType`/`StripNullable`
  helpers, and `GetNullableType` provides an immutable, idempotent nullable-type
  transform. The resulting type symbol is the semantic nullability result for
  both reference and value types; there is no parallel nullable-annotation API.
- Lowering macro-introduced methods now preserves the binder-owned source method
  when generated syntax is contextualized into an authored semantic model.
  Emission no longer re-queries a detached declaration through a semantic model
  that owns a different syntax tree.
- Metadata loading now preserves non-null owner and type contracts for PE field,
  property, event, and method symbols, using the compiler error type for
  unreadable referenced signatures. Non-constant PE fields now return no
  constant instead of invoking an invalid reflection operation, and projected
  tuple fields no longer inherit from PE symbols with absent reflection state.
  Syntax trees always expose source text; detached syntax nodes and tokens
  explicitly expose nullable parents and source behavior, while default tokens,
  node-or-token values, syntax lists, and separated lists are safe to inspect.
  Child-list and reflected-property projections now materialize stable cached
  views, detached nodes are rejected as declaration-table keys, and nested PE
  types preserve their declaring type, namespace, and module ownership.
  Separated-list tokens retain their actual parent and source position, while
  token replacement can safely descend into structured recovery trivia.
  Constructed generic methods preserve substituted array metadata, and
  synthesized entry points use the compilation's canonical `string[]` type.
  Detached symbols now expose nullable assembly and module containment in line
  with the public compiler API while retaining stable names for presentation.
  PE array loading preserves pointer and by-reference element symbol kinds
  instead of projecting every wrapper element as a named type.
- Public compiler-model queries no longer throw for array, tuple,
  and module namespace symbols. `SourceText` now provides cached line
  collections with line-break spans, validated copy operations, and cancellable
  full or span-based writes without creating substring values.
- Tuple symbols now report tuple identity consistently across source,
  metadata, constructed, and aliased symbols. `UnderlyingTupleType` is present
  only for tuple projections and is explicitly nullable for ordinary named
  types.
- Semantic-model queries now reject foreign and detached syntax consistently,
  including symbol, type, operation, capture, flow-analysis, function-parameter,
  and macro-expansion queries. Semantic-model acquisition distinguishes a null
  tree from a non-null tree that is not part of the compilation, and reversed
  statement regions no longer produce a successful control-flow analysis.
- Source fields model an absent or null constant value without violating their
  symbol contract. Failed type-resolution results without a detailed issue now
  produce the ordinary fallback diagnostic instead of throwing, and parser
  recovery coverage now checks every incomplete prefix of a macro declaration with
  a match body.
- Async lambda return-target and iterator signature recognition no longer
  assumes that every named type exposes an original definition or a complete
  namespace-parent chain, preventing malformed or custom symbols from causing
  binding failures.
- Nullable metadata now uses the .NET transform-flag convention for nested
  generic arguments, arrays, generic value types, and by-reference positions.
  Raven also imports both uniform and positional nullable annotations without
  changing overload applicability based only on reference-type annotations.
  Emitted source types now carry the conventional non-null nullable context, so
  unannotated reference signatures round-trip as non-null through both .NET
  reflection and Raven metadata import without redundant position attributes.
  Synthesized nullable context and constraint attributes are emitted from raw
  metadata blobs, avoiding unsupported constructor introspection when compiling
  in the WebAssembly Playground.
- Expression blocks now project `return` and `throw` items as abrupt expression
  statements; bare expression-form `return` carries implicit `unit`. `break`
  and `continue` remain statement-only and consistently report `RAV1902` or
  `RAV1903` from expression blocks, including macro bodies and blocks nested in
  loops.
- Control-flow analysis now models unconditional `loop` statements, reachable
  `break` exits, literal-true `while` loops, `unsafe` blocks, and `finally`
  execution consistently. Exhaustive match statements now make their endpoint
  unreachable when every arm returns or throws; missing coverage, guards, and
  completing arms remain reachable.
- `out`-parameter definite assignment now joins exhaustive match arms and
  respects proven non-terminating loops. Raven locals continue to require an
  initializer at declaration; the unused `RAV0165` use-before-assignment
  descriptor has been removed instead of advertising an inactive rule.
  Missing-return, unreachable-code, and `let ... else` diagnostics no longer
  disappear behind blanket exception suppression when a body contains an
  independent binding error.
- Function declarations now have explicit isolation coverage proving that body
  errors retain the declared signature, stay confined to the broken
  declaration, and do not prevent valid sibling resolution after workspace
  edits. `if` expression binding no longer changes scope by silently falling
  back to the enclosing binder when branch-binder construction fails.
- Target-typed expression binding now keys semantic cache entries by target type
  for every expression form, including wrappers such as parenthesized
  expressions, instead of relying on a syntax-kind allowlist. Semantic results
  are stable regardless of which target-type context binds the syntax first.
- Open generic method groups now infer and construct candidates from target
  delegate parameter types. Their constructed signatures participate in outer
  generic inference, delegate conversion, and semantic symbol publication, so
  calls such as `Apply(21, Identity)` resolve both methods consistently.
- Nullable parameter types now retain their underlying type during namespace
  function signature declaration. Distinct overloads such as `string?` and
  `object?` are no longer misdiagnosed as duplicates, while null-literal calls
  consistently select the more specific reference overload.
- Ambiguous invocations now publish `CandidateReason.Ambiguous` and the complete
  candidate set through `SemanticModel.GetSymbolInfo`. Opportunistic invocation
  lookup falls back to authoritative binding when it cannot select a method.
- Failed invocation binding now retains every considered method candidate, so
  `GetSymbolInfo` exposes useful candidates with
  `CandidateReason.OverloadResolutionFailure`.
- Incremental match exhaustiveness now has add-and-restore coverage for source
  enums, Raven union cases, and sealed-hierarchy permitted subtypes, including
  agreement between diagnostics and `GetMatchExhaustiveness` across snapshots.
- Incomplete constructor declarations now recover with a missing block and a
  targeted `RAV1028` diagnostic instead of throwing or silently accepting a
  bodyless `init`. Recovery preserves following type members, and parser
  mutation coverage now includes contemporary constructor and macro syntax.
  Type-only declaration patterns now use their optional designation directly
  instead of manufacturing a designation containing `None` tokens.
- Incremental syntax updates now retain fragment parser diagnostics, discard
  stale diagnostics from replaced syntax, and shift unaffected diagnostic spans
  after edits. Green-node replacement also preserves unchanged sibling
  identities instead of rebuilding the entire tree. Incremental parser tests
  compare exact syntax shape and diagnostics with an authoritative full parse,
  including incomplete macro declarations and repair edits. Recovery-sensitive
  edits now fall back to an authoritative full-document parse, record and log a
  reason-coded event for stabilization, and support a strict test mode that
  throws rather than hiding an unexpected fallback.
- Raven's nullability and control-flow code actions are usable through the
  language server again: structured diagnostic arguments now survive the LSP
  round trip, null-identity guidance is registered by default, nullable-to-Option
  rewrites generate canonical `let` bindings, and if/else-to-match refactorings
  preserve exact fallback semantics with `_` instead of guessing complementary
  cases from names. The VS Code lifecycle log now records code-action requests
  and returned action counts for future editor-side diagnosis.
- Match exhaustiveness quick fixes now add all missing arms in one
  deduplicated action for match statements and expressions, including matches
  authored inside macro declarations. Generated patterns follow the scrutinee:
  typed bindings for sealed classes and parenthesized unions, positional
  bindings for sealed records, target-typed cases for case-declared unions and
  enums, and literal patterns for finite literal cases.
- Macro bodies now participate in primary semantic diagnostics like
  ordinary function bodies. Invalid local macros report against authored source
  immediately instead of waiting for the projected macro assembly, and project
  diagnostics no longer replace those errors with generated source positions.
  A broken macro declaration no longer prevents valid sibling macros from compiling
  and expanding, and attached macro targets are modeled as implicit parameters
  for normal lookup and semantic tooling. Incremental recovery now preserves
  incomplete macro bodies, while language-service analysis retains the complete
  authored macro compilation even when emission filters out a broken macro.
  Document-scoped compiler diagnostics now unify the consumer and macro
  projections of an authored file, so VS Code publishes macro-body diagnostics
  during edits. Invocations still resolve to a recognized local macro
  declaration when its implementation is broken, avoiding misleading
  unresolved-macro cascades, while hover remains available throughout macro
  parameters and bodies.
- RavenDoc now omits redundant `public` modifiers, hides compiler-emitted
  extension grouping types and implementation-only accessors, preserves
  protected accessor contracts, and renders operator signatures without a
  duplicated `func` keyword.
- RavenDoc now groups case-declared union cases under their declaring union
  using logical Raven names and signatures, while keeping parenthesized
  member-type unions distinct and suppressing separate emitted case-type pages.
- Published Raven.Core API pages are now generated from the Raven project
  rather than its metadata assembly, restoring GitHub source links with line
  anchors. Workspace project loading also honors `RavenEmitCoreTypesOnly` by
  disabling framework projections while Raven.Core itself is analyzed.
- Abstract syntax API families now carry generator-owned closed-hierarchy
  metadata. Raven imports permitted subtype lists from referenced assemblies,
  so matches over the `SyntaxNode` root, structured trivia, expressions,
  statements, patterns, names, types, members, and other intermediate syntax
  categories receive ordinary exhaustiveness diagnostics and missing-case
  feedback, including recovery-only syntax nodes.

- Unit-returning callables now report `RAV9034` when their final expression
  produces a non-unit value, including effectful invocations. This prevents a
  discarded value from looking like a valid tail result; `_ = expression`
  remains the explicit intentional-discard form. The analyzer diagnostic can
  be disabled through standard `.editorconfig` severity configuration.
  Consumed block-expression and value-returning lambda tails remain valid
  implicit results and are not reported.
- Consolidated full returned-value handling into `RAV9034`, removing the
  overlapping `RAV9029` diagnostic. Value-forming outer expressions such as
  `2 + Compute()` are now reported even when a nested call may have effects,
  while full mode extends the same diagnostic to bare calls and member access.
- Added a user-facing built-in analyzer reference with default severities and
  `.editorconfig` override guidance to the main documentation navigation.
- Added a Playground sample demonstrating Raven's support for functional
  programming patterns within its pragmatic, general-purpose model: immutable
  transformations, value-producing `match` and `if` expressions, block
  expressions, final-expression returns, and an effectful output shell around
  a pure calculation core.
- RavenDoc now accepts repeatable `--value name=value` inputs and substitutes
  explicit `{{name}}` placeholders in Markdown, enabling publishing workflows
  to inject paths, versions, commit identifiers, and version stamps while
  leaving unresolved placeholders visible.
- RavenDoc now separates its reusable page template and static assets from
  symbol extraction, renders compact API headings, editor-like signatures with
  generic constraints, distinct namespace/member icons, structured
  documentation sections, and a responsive page outline.
- Updated tests, samples, and language-facing documentation to use canonical
  `let` lexical bindings while retaining `val` for immutable properties and
  compatibility coverage. Compiler API, analyzer, source-generator, and macro
  examples now prefer Raven where the API is being consumed from Raven, and
  contributor guidance defines a gradual Raven-first infrastructure and
  bootstrap boundary.
- Added `Raven.Macros.Sha256Digest!` with the imported `sha256Digest!` alias.
  It hashes literal values during compilation and expands to a lowercase
  hexadecimal string, avoiding runtime hashing and naming collisions with
  .NET's `SHA256` type.
- Added `Raven.Macros.EmbedFileContent!` with the imported
  `embedFileContent!` alias. It resolves paths relative to the invoking source
  file, embeds UTF-8 text as a string literal, diagnoses missing files, and
  invalidates cached expansions when observed files change or disappear.
- Macro declarations now appear in VS Code's document outline with a
  distinct operator symbol, including local functions nested in their bodies.
- The documentation build now publishes independent RavenDoc API sites for
  `Raven.Core` and `Raven.Macros` alongside the DocFX language site and browser
  Playground. Library documentation is prominent in the main navigation,
  compiler APIs are supporting tooling reference, and macro pages link to the
  compiler syntax-tree guide.
- RavenDoc now renders namespace functions and `macro` declarations on
  namespace pages. Namespace-function pages preserve the Raven-facing shape
  while identifying the emitted CLR container for consumers in other .NET
  languages.
- Moved the standard `quote` and `compile` declarations into the Raven-authored
  `Raven.Macros` compiler-plugin assembly. Its aliases require
  `import Raven.Macros.*`, while canonical qualified names remain available.
- Raven compiler-plugin projects marked with `RavenCompilerPlugin` can now emit
  reusable `macro` declarations, and compiler/MSBuild runtime dependency
  propagation follows actual emitted assembly references instead of scanning
  source for specific macro names.
- Fixed a language-server hover regression that could recursively materialize
  metadata symbols while resolving qualified names. Metadata types now use a
  cached simple-name index, qualified namespace segments resolve from available
  semantic state, and cold consumer-local hovers recover correctly in files
  partitioned for local macro declarations.
- VS Code and the language server now treat a source file outside evaluated
  `.rvnproj` items as an isolated file-based application. Loose files no longer
  leak declarations into one another or nearby projects, standalone snapshots
  receive the standard prelude, and Run Active File/Project invokes `rvn run`
  in an interactive terminal.
- Added first-class file-based applications through `rvn run <file.rvn>` and
  the `rvn <file.rvn>` shorthand. Arguments after `--` reach `Main`, process
  exit codes are preserved, and one-shot compilation artifacts are isolated
  from the source tree and cleaned after execution. A first-line
  `#!/usr/bin/env rvn` shebang is preserved as trivia, enabling executable
  Raven files on Unix-like systems.
- Macro completion items now carry `IMacroSymbol` and map to VS Code's distinct
  snippet icon instead of appearing as ordinary classes or untyped text.
- Added compiler symbols for loaded and intrinsic macros. `nameof` now accepts
  macro names and preserves the resolved alias or canonical spelling, while
  `typeof` reports a dedicated diagnostic when its operand resolves to a macro.
- Added namespace-qualified macro invocation and import-scoped macro aliases.
  Compiler-provided `Raven.Macros.Quote` and `Raven.Macros.Compile` expose the
  `quote` and `compile` aliases through `import Raven.Macros.*`; local names can
  shadow imported aliases, while qualified invocation remains an escape hatch.
  Argument-style bang invocations such as `twice!(21)` no longer require an
  empty token-tree body, and invocable macro samples now use this preferred
  form.
- Fixed expanded-document commands for projects containing authored
  `macro` declarations. `rvn dev syntax --syntax-view expanded` and
  `rvn dev macros` now resolve the workspace document's projected compilation
  tree, expand every sibling macro invocation, and preserve line breaks after
  multiline token-tree invocations.
- Fixed language-server hover inside authored `macro` declarations.
  Their signature semantic model now provides method-like body scopes, so
  parameter declarations, locals, references, member access, and invocations
  resolve without consulting the lowered macro implementation tree.
- Made authored `macro` declarations first-class incremental executable
  owners. Signature and body edits now invalidate their semantic state without
  discarding unrelated state, and language-service queries recover through
  malformed intermediate edits instead of reusing stale parameters or locals.
- Prevented authored macro parameters and attached-target names from
  colliding with compiler-generated adapter locals during local macro lowering.
- Updated the Playground metaprogramming sample to use native macro declarations
  with real `ExpressionSyntax` and `IMacroTokenStream` parameters, and made
  local macro partition emission work entirely in memory under WebAssembly.
- Added type-directed `ExpressionSyntax` parameters to macro declarations. They project
  authored invocation arguments as `ExpressionSyntax` and can be mixed with
  ordinary typed values, while semantic symbols and runtime parameter
  descriptors expose the shared `MacroParameterRole`.
- Added native token-stream inputs to macro declarations. An
  `body: IMacroTokenStream` parameter selects token-tree invocation syntax and
  binds the raw body, while the remaining parameters continue
  to use the typed caller-supplied argument model.
- Added a compiler-normalized macro definition descriptor shared by provider
  registration, synthesized symbols, validation, completion, signature help,
  and hover. Legacy class-authored macro contracts are adapted once at
  registration instead of being interpreted independently by each consumer.
- Made `macro` declarations executable as same-compilation argument-style
  and attached macros. Attached declarations mark one ordinary typed parameter,
  such as `on property: PropertyDeclarationSyntax`, and ordinary synchronous bodies can
  conditionally combine `expand`, `replace`, and `introduce` contribution
  statements. The compiler lowers them to isolated provider adapters and typed
  parameter objects while preserving `IMacroDeclarationSymbol` as their semantic
  identity.
- Removed the separate macro target-clause syntax. Attached target bindings now
  participate in the shared parameter-role model as `AttachedTarget`, with
  diagnostics for duplicate targets, defaults, unsupported syntax types, and
  malformed declarations that remain queryable by language services.
- Macro syntax return annotations now project to invocable grammar targets.
  `ExpressionSyntax`, `StatementSyntax`, their union, and category-untyped
  `SyntaxNode` expose normalized invocation flags; unsupported semantic return
  types and attached/invocable contradictions produce declaration diagnostics.
- Fixed declaration skeleton binding for union-type signatures so compiler API
  symbols retain their `System.Union<...>` return shape instead of falling back
  to the supplied default type.
- Made the Playground own and serve its theme stylesheet directly so local
  development and standalone subpath deployments use the same asset URL.
- Reduced the Playground header to a compact Raven mark beside its title so the
  complete desktop workspace fits without page-level scrolling on typical
  laptop viewports.
- Replaced the Playground's native example selector with a themed, searchable
  picker that organizes samples into a Basics section and feature-focused
  groups.
- Added expression-form pattern binding with
  `if let pattern = value { ... } else { ... }`. It uses the same pattern and
  capture semantics as the existing statement form, scopes captures to the
  successful branch, and computes its result from the two branch values.
- Added compiler-integrated conditional compilation with `#if`, `#elif`,
  `#else`, and `#endif`. Conditions support defined symbols, `true`/`false`,
  parentheses, and Raven `not`/`and`/`or` operators (with
  `!`/`&&`/`||` aliases). Symbols flow from MSBuild `DefineConstants` or the
  `rvnc --define` option, inactive source remains lossless disabled trivia, and
  the VS Code extension highlights directives and dims inactive code.
- Added a Roslyn-inspired syntax tree visualizer to the VS Code extension. The
  Explorer view presents compiler-produced nodes, tokens, trivia, property
  roles, spans, raw kinds, missing elements, and diagnostics; supports authored
  and fully macro-expanded trees; and opens the complete expanded source when
  switching to the expanded tree.
  `rvn dev syntax` now exposes the structured JSON and source-override contract
  used by the view.
- Namespace-level types, delegates, unions, and extension declarations now
  default to `internal`; `public` explicitly exports them from the assembly and
  is no longer diagnosed as redundant in that position. Type members,
  including nested types, default to `public`, while Raven.Core now marks its
  exported declarations explicitly. The former `MembersPublicByDefault`
  compilation/project option and its CLI switches have been removed.
- `use` bindings no longer report `RAV9027` merely because their bound value is
  not read; establishing the disposal lifetime counts as the declaration's
  intended use.
- Added raw-body token-tree expression macros with `name!{ ... }` syntax,
  lossless DSL body capture, body-relative diagnostics, and helpers for parsing
  the complete body or selected embedded spans as Raven expressions. Macro
  bodies bypass ordinary Raven tokenization, while expansion continues through
  normal semantic binding and emit.
- Added the alternate `name! { ... }` spelling for token-tree expression
  macros. It has a dedicated syntax node while sharing macro binding,
  expansion, completion, and language-service behavior with `name!{ ... }`.
- Added `SyntaxToken.RawKind`, macro-local token reclassification through
  `WithRawKind`, and detached custom-token construction without changing
  ordinary Raven `SyntaxKind` classification or lexing.
- Added replaceable macro token streams that emit `SyntaxToken`, including a
  default Raven-lexer-backed stream, macro-local keyword/reserved-word overlays,
  and compiler-discovered custom stream providers for DSL-specific lexers.
- Added typed token-tree macro inputs through
  `ITokenTreeMacro<TParameters>`, allowing validated positional or
  named arguments before an unrestricted raw DSL body.
- Added compiler-owned typed macro parameter descriptors and named-argument
  completion for attached, argument-style, and token-tree macros.
- Added context-aware macro-name completion for incomplete invocations.
  Typing `#` in an expression offers only invocable and token-tree macros;
  typing it in a declaration offers only attached macros and inserts the
  complete `#[Macro]` attribute form.
- Added a Raven-authored `guard!{ unless <expression> }` sample as the
  token-tree macro MVP, demonstrating macro-local keywords, embedded Raven
  expression parsing, direct lowering, and end-to-end execution.
- Extended the token-tree macro sample with
  `choose!{ test ... then ... otherwise ... }`, demonstrating multiple
  macro-local clauses, independently parsed Raven fragments, body-mapped
  missing-clause diagnostics, and direct lowering to an `if` expression.
- Added a minimal LINQ-like `#query` macro sample with one `from`, optional
  `where`, and one `select` clause, directly lowering caller-scoped Raven
  fragments to ordinary `Where`/`Select` calls and authored range-variable
  lambdas.
- Added diagnostic-bearing embedded Raven expression parsing for token-tree
  macros. `ParseExpressionResult` returns recovered syntax plus immutable
  native parser diagnostics mapped to the authored invocation, while the
  existing `ParseExpression` convenience API remains available.
- Added complete-body and selected-span Raven statement parsing for token-tree
  macros. `ParseStatement` returns recovered `StatementSyntax`, while
  `ParseStatementResult` also retains native authored-source diagnostics and
  rejects trailing input.
- Added compiler-owned macro signature help for typed attached, invocable,
  and token-tree invocations. The semantic model now exposes normalized macro
  parameters and the active argument, and the language server presents that
  result including token-tree body shape.
- Added the initial `macro` declaration boundary at compilation-unit and
  namespace-member scope. It uses a dedicated
  `MacroDeclarationSyntax`, treats `macro` contextually, and exposes a
  distinct `IMacroDeclarationSymbol` with macro-owned parameters, generic
  parameters, constraints, and call-site return type. Macro declarations do not
  implement `IMethodSymbol`, enter ordinary runtime method binding, or support
  `async`/`await`; semantic activation and lowering remain future work.
- Added the compiler-owned expression-only `quote!{ ... }` intrinsic. It
  preserves tokens and trivia, rejects malformed or trailing input at authored
  locations, expands to fully qualified `SyntaxFactory` construction, and
  participates in macro-name completion without a plugin reference.
- Added `#(expression)` holes inside expression quotes. Holes are discovered
  through the macro token stream without changing Raven lexing, accept ordinary
  Raven expressions that bind as `ExpressionSyntax`, preserve surrounding
  quote trivia, and retain native diagnostics at authored locations.
- Added `compile<TDelegate>! { expression }`, which applies the `quote!` syntax
  and hole model, compiles the resulting Raven expression at runtime, and
  returns a strongly typed delegate. Compiler and SDK builds now add
  `Raven.CodeAnalysis` and runtime compiler dependencies on demand for the
  intrinsic while respecting an explicit project reference.
- Migrated the Raven-authored sample `#add` procedural macro to construct its
  expansion with `#quote` and argument-expression holes, validating quote while
  compiling a macro plugin and loading that plugin in a consuming project.
- Centralized compiler-provided macro registration in the compiler's default
  macro environment and added `MacroReference.CreateFromImage`, allowing emitted
  Raven macro plugins to be activated directly from memory as a foundation for
  same-project macros and the Playground.
- Added an explicit compile-time-only macro source partition to `Compilation`.
  `AddMacroSyntaxTrees` compiles Raven macro declarations in memory before
  consumer binding, reports partition diagnostics through the consumer
  compilation, includes local macros in completion, and excludes plugin
  implementation types from runtime emit.
- Added automatic direct-declaration discovery for same-project macros. Macro
  interface implementations move into the compile-time partition through
  compiler, Workspace, and SDK compilation paths, while retaining
  semantic-model access and requiring neither a `RavenMacro` item nor an
  explicit compiler-contract project reference.
- Retired the transitional consumer-authored `RavenMacro` project item.
  Reusable compiler-plugin providers now use ordinary marked project,
  assembly, or package references; project loaders report migration guidance
  when the removed item is encountered.
- Removed the transitional `IRavenMacroPlugin` aggregation contract and
  `[LocalMacroPlugin]` source marker. Macros are now registered directly as
  `IMacroDefinition` implementations.
- Made macro category classification compiler-owned. Definitions implement
  exactly one category-specific macro interface, and `MacroFacts` derives
  `MacroKind` without a macro-overridable discriminator property.
- Moved macro target applicability to `IAttachedDeclarationMacro`, removing
  redundant `MacroTarget.None` implementations from invocable and
  token-tree macros while retaining normalized queries through `MacroFacts`.
- Added focused sample projects for custom macro token streams and quote-based
  macro expansion.
- Applied nominal-type macro replacements to base/interface binding, allowing
  an attached macro to add a real interface contract alongside generated
  members.
- Migrated the remaining C# macro sample providers to Raven-authored macro
  projects.
- Renamed project sample files around their entry point, program, primary type,
  or related type group instead of using `main.rvn` universally.
- Changed `MacroReference` to expose a cached immutable `Macros` snapshot so
  compiler and tooling queries reuse the same definition instances.
- Kept collectible macro assembly contexts alive for the lifetime of their
  cached macro snapshots, preventing referenced helper assemblies from failing
  to load when collection occurs before expansion.
- Made VS Code language-server builds on extension activation opt-in. The
  extension now starts an existing workspace or packaged server immediately by
  default instead of blocking activation on a full compiler dependency build.
- Added declaration-granular same-project macros through `[LocalMacro]`.
  Marked top-level declarations are compiled and activated separately while
  ordinary declarations in the same source remain runtime code, enabling macros
  to be declared and consumed in one Playground buffer.
- Reused emitted same-project macro partition artifacts across consumer-only
  incremental edits, while invalidating them for macro or reference changes and
  remapping cached partition diagnostics to the current syntax-tree projection.
- Added `RAVM003` for local macro implementations that depend on consumer
  declarations, identifying the compile-time activation cycle at the authored
  reference in both dedicated and mixed-source macro layouts.
- Added position-aware semantic-model lookup for mixed local-macro documents.
  `Compilation.GetSemanticModel(tree, position)` and
  `Document.GetSemanticModelAsync(position)` now route macro declaration
  positions to the current macro projection while preserving the consumer model
  for ordinary source positions and existing positionless calls.
- Routed language-server hover and completion through the position-aware
  semantic projection, enabling ordinary Raven symbol information and member
  completion inside same-buffer local macro implementations.
- Routed language-server definition, references, and rename through the
  position-aware semantic projection. Reference search now scans both
  compiler-owned projections of a mixed document while returning edits and
  locations against the original authored source.
- Made workspace analyzer execution projection-aware for mixed local-macro
  documents. Syntax-node, symbol, operation, and syntax-tree actions now see
  both ordinary consumer code and Raven macro implementation code with the
  semantic model that owns each projection.
- Added `InvocableMacroExpansionResult` factory methods for expression
  results, forwarded parser diagnostics, macro-authored diagnostics, and
  combined diagnostic results. The built-in `#quote` macro and Playground
  local-macro example now use the factory path.
- Added matching `MacroExpansionResult` factories for attached declaration
  replacement, introduced members, peer declarations, and diagnostic-only
  results. Mutable result properties remain available for compatibility.
- Stabilized attached property replacement binding so declaration-pass
  accessor skeletons are completed once and later binds reuse the registered
  accessor symbols. Replacement properties now expose the same getter and
  setter identities through both the property and containing type.
- Improved typed macro failure diagnostics by unwrapping reflection invocation
  failures. Attached and invocable macro authors now see their underlying
  exception message at the authored macro name instead of a generic reflection
  wrapper message.
- Made attached and invocable macro expansion cancellation-aware. Direct
  and reflection-wrapped cancellation now propagates to the compiler caller,
  does not produce `RAVM020`, and does not cache a failed expansion, allowing a
  later uncanceled request to retry normally.
- Added the provider-owned `[assembly: RavenCompilerPlugin]` marker for
  reusable Raven macro projects. Consumers can now use an ordinary
  `ProjectReference`; the workspace builds and activates marked providers as
  compiler plugins without adding them as runtime project references or
  scanning unmarked dependencies.
- Added deterministic macro export manifests through repeatable
  `[assembly: RavenCompilerPlugin(typeof(MacroType))]` markers. File, assembly,
  and in-memory macro references now select direct macro definitions, retain
  bare-marker fallback discovery, and report invalid manifests as `RAVM001`.
  Same-project macro partitions discover direct definitions without an
  assembly export marker.
- Added provider-marked C# compiler-plugin project references. Raven projects
  can now consume a C# macro provider through an ordinary `ProjectReference`;
  the project system builds and activates marked providers without adding them
  to the consumer's runtime reference graph or scanning unmarked dependencies.
- Added compiler-owned discovery of marked portable assembly references.
  Direct DLL and resolved package references now join the same active macro
  registry as explicit and same-project macros after a metadata-only marker
  check; unmarked assemblies are never activated or searched for macro types.
- Added split NuGet package support for compiler plugins. Consumer binding now
  retains a package's `ref/<tfm>` assembly while a marked `lib/<tfm>`
  implementation is activated separately as a macro reference. Macro helper
  assemblies shipped beside the implementation are resolved without requiring
  an application `.deps.json`; runtime assets supplied by transitive NuGet
  packages are carried as private identity-checked macro dependency probes
  rather than consumer metadata references.
- Deferred assembly custom-attribute emission until source type builders and
  members exist, allowing assembly attributes such as macro manifests to carry
  `typeof` values that refer to types declared in the same Raven assembly.
- Added runnable Playground examples for constructing syntax with `#quote` and
  for defining local attached, argument-style expression, and token-tree
  expression macros.
- Prevented incomplete recovered source symbols from aborting external
  documentation emission when no stable documentation member ID can be built.
- Changed `Compilation.AddReferences` to append metadata references, matching
  its Roslyn-style additive contract instead of replacing existing references.
- Clarified that Raven-authored enums are closed by default for declared-member
  match exhaustiveness, with no source modifier, and locked complete enum
  matches with focused semantic coverage.
- Documented the typestate pattern with phantom marker types, a state-erased
  base class, and state-specific extensions, and added a runnable Playground
  connection-lifecycle example.
- Changed `RavenQuoter` to emit Raven `SyntaxFactory` construction code by
  default, with explicit C# output available through `RavenQuoterOptions`.
- Added a Raven-themed DocFX site with a dedicated carousel of language samples,
  compact documentation navigation, VS Code-style Raven syntax highlighting,
  and shared light/dark design tokens used by RavenDoc and the Playground.
- Added compiler-owned Markdown classification for Raven documentation
  comments and dedicated language-server semantic tokens for tags, headings,
  links, inline code, and fenced code. Tag-like text in code remains literal.
- Refreshed the `markdown-docs` library/consumer sample to demonstrate default
  dual output, Raven-native role aliases, links, headings, inline code, and
  fenced Raven examples without redundant project configuration.
- Added the format-neutral `RavenDocumentation` compiler API. Markdown and XML
  inputs now normalize into Raven-owned section and association roles before
  XML projection, with a small compatibility alias set including `@parameter`,
  `@result`, and `@throws`.
- Raven library projects now emit both Raven Markdown documentation sidecars
  and compatible .NET XML documentation by default. Raven consumes Markdown
  first and falls back to XML for libraries without Raven documentation;
  Markdown remains the default source comment format and XML authoring remains
  explicit.
- `Raven.Core` now emits and ships `Raven.Core.xml`, with documentation for its
  public types, carrier members, LINQ helpers, JSON converters, and framework
  projection adapters. Projected framework methods forward their adapter
  documentation so hover can present the Raven-facing `Option`/`Result`
  behavior.
- XML documentation emission now handles source-field metadata names safely,
  and MSBuild-relative default documentation paths resolve without duplicating
  the intermediate output directory.

- Updated the ASP.NET Core samples for .NET 11 Preview 6 union request,
  response, streaming, JSON persistence, and OpenAPI `anyOf` support.

- Restrict .NET 11 runtime-async method metadata to Task-like methods so async
  iterators remain valid CLR types.

- Allow multiline fluent expressions in typed `let ... else` declarations,
  including unparenthesized awaited invocations.

- Added Roslyn-shaped source generator APIs, dedicated workspace generator
  references, and `.rvnproj` `<Analyzer>` / `<SourceGenerator>` assembly items
  that run extensions in normal project builds.

- Reorganized spans, stack allocation, ref structs, ref safety, and unsafe
  interop into a dedicated systems-programming documentation section so these
  specialized features no longer dominate the core language path.

- Added first-class `Span<T>` and `ReadOnlySpan<T>` support across stack
  allocation, covariant conversions, generic inference, overload resolution,
  indexing, mutation, slicing, iteration, and span-targeted collection
  expressions, with `Memory<T>` and `ReadOnlyMemory<T>` interoperability.
- Added unsafe pointer-producing stack allocation with
  `stackalloc T[count]`, including runtime-sized allocations, unmanaged element
  validation, integer-count diagnostics, direct `localloc` emission, and safe
  natural `Span<T>` or explicit `ReadOnlySpan<T>` targets.
- Rejected returning `stackalloc` storage through direct expressions, locals,
  and simple pointer or span aliases while preserving returns of spans backed
  by parameters or managed arrays.
- Added `ref struct` declaration syntax and source-symbol classification,
  including modifier validation and consistency checks across partial
  declarations, and emitted the standard `IsByRefLikeAttribute` metadata for
  both generic and non-generic ref structs.
- Added `readonly ref struct` classification and `IsReadOnlyAttribute`
  emission, with diagnostics for mutable instance storage and inconsistent
  partial declarations.
- Added ref fields with `&T` field types inside ref structs, including semantic
  restrictions, symbol API classification, and standard CLR `BYREF` field
  signatures.
- Rejected returning ref structs that contain references to method locals or
  `stackalloc`-backed ref-like fields, including through simple local aliases,
  while allowing caller-owned references and spans supplied by parameters.
- Added the `allows ref struct` generic anti-constraint, including source
  semantic classification and the standard CLI `AllowByRefLike` metadata flag.
- Applied ref-like storage, capture, async, and iterator safety rules to type
  parameters declared with `allows ref struct`, not only to concrete ref-like
  named types.
- Allowed managed-reference dereferences in safe code while retaining unsafe
  diagnostics for raw pointer dereferences.
- Rejected ref fields whose referent is itself ref-like or is a generic type
  parameter that allows ref structs.
- Diagnosed misplaced, duplicated, and `class`-conflicting
  `allows ref struct` anti-constraints in both inline and `where` constraint
  lists.
- Recognized `ScopedRefAttribute` on consumed .NET parameters and exposed the
  result through the Roslyn-like `IParameterSymbol.ScopedKind` API, including
  constructed generic symbols.
- Added `scoped` parameter syntax and source-symbol classification for both
  scoped ref-like values and by-reference parameters.
- Emitted `ScopedRefAttribute` for explicitly scoped parameters when required
  by the C# metadata contract, including generic-safe metadata round trips.
- Rejected returning scoped ref-like parameters through direct expressions or
  local aliases.
- Applied C#-compatible implicit scoped defaults to `out` parameters and `ref`
  parameters of ref-like type, including metadata classification and emission.
- Highlighted contextual `scoped` parameter modifiers in both semantic tokens
  and the VS Code TextMate grammar without reserving identifier uses.
- Added Raven-native `scoped val`/`var`/`let`/`const` local syntax, scoped-value
  versus scoped-reference symbol classification, and editor highlighting.
- Diagnosed `scoped` local declarations whose resulting type is neither
  ref-like nor by-reference.
- Diagnosed by-value `scoped` parameters of non-ref-like type while permitting
  ordinary types behind `scoped ref`, `in`, and `out`.
- Rejected returning scoped ref-like locals directly or through ordinary local
  aliases.
- Propagated scoped-local escape provenance through ref-like field containment.
- Propagated scoped provenance into ref-like call results through receivers and
  unscoped parameters, while excluding arguments to scoped parameters.
- Rejected capturing scoped parameters and locals in lambdas or local
  functions, including scoped references to ordinary value types.
- Rejected scoped parameters and locals that would remain live across `await`
  or `yield` suspension points.
- Prevented overrides and explicit interface implementations from weakening a
  scoped parameter contract while allowing implementations to strengthen it.
- Required partial method declarations and implementations to agree on each
  parameter's scoped contract.
- Preserved scoped parameter attributes on emitted delegate `Invoke` methods,
  including generic delegates that allow ref-like type arguments.
- Rejected assignments that expose scoped values through by-reference
  parameters or fields of `self` and by-reference receivers.
- Enforced scoped indexer parameter contracts across overrides and explicit
  interface implementations.
- Correctly materialized value-type `self` when Raven methods request its value
  while preserving the managed receiver for address-based access, keeping
  generic `Option` and `Result` instance behavior portable across runtimes.
- Materialized empty union cases through their enclosing carrier during `?`
  propagation, preventing `None` from being returned with the incompatible
  case-only runtime layout.
- Computed async resume dispatch from the fully lowered protected-region tree
  and entered nested guards at their boundaries, preventing branches into
  `try` regions for `try? await` and Result propagation.
- Lowered non-`use` async bodies before suspension rewriting so a `try await`
  expression consumed by a match cannot acquire an unguarded protected region.
- Emitted sequential storage for unions containing managed references while
  retaining compact explicit storage for unmanaged-only unions, preventing
  invalid overlapping object/value fields across runtimes.
- Projected `DateTimeOffset`, `DateOnly`, `TimeOnly`, and `TimeSpan`
  `TryParse(string)` methods as `Option<T>` values.
- Added `lock expression { ... }` statements, lowering to exception-safe
  `System.Threading.Monitor` acquisition and release.
- Added playground samples showing guarded deconstruction in `for` iteration
  and pattern-bound `while let` consumption of a domain event stream.
- Added a contextual playground sample for mixed-era shipment references using
  a type union with target-typed construction.
- Prevented async lowering from redirecting synthesized state-machine receivers
  through their own hoisted receiver field, producing portable async-iterator IL.
- Added a cold-chain monitoring playground sample using `yield` and `await for`
  to consume an asynchronous stream.
- Added a webhook-routing playground sample that jointly matches dictionary
  metadata and sequence-shaped request paths.
- Recovered statically bound sequence types when matching nested tuple elements,
  so sequence patterns compose with dictionary and other structural patterns.
- Made cold metadata member lookup include explicit-interface properties and
  enabled middle-rest sequence deconstruction over `ImmutableArray<T>`.
- Kept parenthesized tuple patterns on tuple code generation when their runtime
  types also expose indexable interface members, preserving composed structural
  patterns across desktop and WebAssembly runtimes.
- Added a playground sample that scopes `HttpClient` with `use`, loads the
  deployed example catalog, deserializes its JSON, and models outcomes as a
  union.
- Preserved collection element types while resolving competing overloads so
  generic enumerable overloads such as `Task.WhenAll([task1, task2])` infer
  their type arguments instead of prematurely widening the elements.
- Made the WebAssembly playground await synthesized async top-level entry
  points directly instead of invoking their synchronously blocking console
  bridge.
- Added a checkout playground sample that starts independent warehouse stock
  lookups together and awaits their results before presenting availability.
- Added an order-boundary playground sample showing `Result` conditional
  access and implicit error conversion during propagation.
- Added a price-import playground sample that captures exceptions from a
  throwing .NET API as typed results with `try?`.
- Made `Result` propagation extract its error union case structurally instead
  of depending on `UnwrapError`, preserving failure behavior across runtimes.
- Added a dispatch-planning playground sample built with immutable collection
  comprehensions, filtering, and collection spreads.
- Added a contextual playground sample that models fulfillment routes as a
  sealed class hierarchy with shared behavior, property patterns, and
  exhaustive matching.
- Emitted closed-hierarchy metadata without runtime constructor inspection, so
  sealed class hierarchies compile in WebAssembly.
- Prevented Monaco's automatic layout from repeatedly increasing the playground
  workspace height by giving the desktop workspace a bounded viewport-relative
  height and sizing the editor through flex layout.
- Made the playground load Hello World deterministically on startup and added
  shareable source URLs through a base64url `source` query parameter and Share
  command.
- Expanded the playground catalog with contextual examples of `Option` and
  `let ... else`, framework parsing projected into typed flow, higher-order
  functions, and `Result` propagation.
- Resolved the playground's embedded .NET reference assemblies from MSBuild's
  selected targeting pack instead of assuming its patch version matched the
  browser runtime pack. Static builds now fail early if `System.Runtime` is
  absent, and browser coverage executes every registered example.
- Made PE attribute discovery tolerate missing transitive metadata dependencies,
  preventing wildcard namespace imports from crashing browser compilation when
  a nonessential attribute assembly cannot be resolved.
- Emitted nullable metadata through raw custom-attribute blobs instead of
  runtime constructor inspection, allowing user-defined unions and other
  nullable shapes to compile under browser WebAssembly.
- Made repository-local Raven MSBuild targets honor the active build
  configuration when locating the compiler host and Raven.Core, so clean
  Release builds no longer incorrectly require Debug artifacts.
- Published the browser playground beneath `/playground/` in the same GitHub
  Pages artifact as the documentation site, with top-level documentation links
  and a relocatable static base path.
- Added a playground-owned example catalog loaded from static files, allowing
  curated Raven programs to be registered and updated independently of the
  browser-hosted compiler code. Browser coverage compiles every registered
  example.
- Made playground completion visible and responsive by debouncing member-prefix
  requests, explicitly opening Monaco suggestions after a short pause, and
  avoiding uncancelable WebAssembly completion work on ordinary keystrokes.
  Global completion remains available through `Ctrl+Space`.
- Prevented equivalent synthesized methods from producing duplicate CLR method
  definitions during emission. Raven.Core union carriers such as `Result<T, E>`
  now emit a single executable `ToString` body instead of a bodyless method
  that could fail at runtime with `BadImageFormatException`.
- Aligned the WebAssembly playground's compilation environment with `rvnc` by
  sharing the standard generated prelude, referencing Raven.Core, and compiling
  against the .NET reference assemblies. Record equality synthesis now accepts
  equivalent nullable and non-nullable metadata representations of
  `object.Equals`, preventing platform-specific missing-member diagnostics.
- Reused the WebAssembly playground's emitted assembly when Compile and Run
  target the same immutable compilation snapshot, avoiding redundant browser
  emission while preserving incremental recompilation after edits.
- Reduced one-file compiler latency by indexing extension-conversion containers
  directly from referenced assembly metadata instead of loading conversion
  members across every referenced type.
- Added a static-hostable Blazor WebAssembly playground starter with a Monaco
  editor, Raven TextMate highlighting, separate Compile and Run commands, and
  in-browser compilation and execution of emitted Raven assemblies. A
  repository-owned browser smoke test covers the release-published static site,
  compiler-backed Monaco completion, diagnostics, and emitted-program output.
  The browser host advances an ordinary Raven workspace and reuses its current
  compilation across editor requests and explicit compilation.
- Removed two browser-WebAssembly blockers from compiler metadata loading:
  assembly identities are read from portable executable metadata where runtime
  assembly-loading APIs are unavailable, and unavailable runtime nullability
  reflection falls back to explicit nullable metadata.
- Added dotted property paths in property patterns. For example,
  `Foo { Item.Size: 2 }` is shorthand for
  `Foo { Item: { Size: 2 } }`. Completion and hover resolve each property-path
  segment against its receiver type, including while a dotted path is being
  typed.
- Removed the experimental trailing-block call syntax and its builder/receiver
  DSL infrastructure from the main language. Function values use ordinary
  function-expression syntax.
- Restored brace object initializers as a distinct construct: `Foo { Name =
  "Foo" }` selects a parameterless constructor, while `Bar("Foo") { Age = 42 }`
  initializes an object after an explicit constructor call. `value with { ... }`
  remains the separate non-destructive copying form.
- Made incremental document diagnostics independent of prior semantic queries
  by declaring same-document member signatures before binding executable code.
- Added the initial distribution contract: platform SDK archive builders,
  relocatable compiler/MSBuild assets, `rvn sdk path`, installed-SDK discovery
  in VS Code, a universal VSIX builder with a bundled language server,
  checksum-verifying installers, and automated multi-platform release builds.
  Packaged `rvn`, `rvnc`, and VSIX artifacts now share the release version;
  both command-line tools expose it through `--version`.
- Added `rvn doctor` to diagnose the .NET SDK and required Raven SDK files.
- The VS Code extension now offers SDK installation instructions when build,
  run, and debug tooling is unavailable, while retaining bundled editor support.
- Locked the built-in union C# surface and serialization contract with direct
  C# construction/extraction coverage: payload-first JSON remains the standard
  behavior, while tagged Raven serialization requires explicit opt-in.
- Highlighted constructor-form `init(...)` declarations and primary-constructor
  access modifiers in the VS Code TextMate grammar.
- Preserved keyword highlighting for parenthesized patterns such as `if let
  (...)`, `while let (...)`, `for let (...)`, and `value is (...)` in both the
  VS Code TextMate grammar and the DocFX site highlighter.
- Removed `trait` as an alias for extension declarations; use `extension`.
- Added the first generic instance framework projection:
  `Dictionary<TKey, TValue>.TryGetValue(key) -> Option<TValue>`. Missing keys
  become `None`, while constructed value-type nullability is preserved.
- Projected `Guid.Parse(string)` as `Result<Guid, FormatException>` and
  `int.Parse(string)` as `Result<int, FormatException | OverflowException>`.
  Null-argument exceptions that require forcing null through the non-null Raven
  signatures now propagate as faults rather than ordinary result errors; the
  legacy lowercase `int.parse` Raven.Core helpers remain removed.
- Added default-on framework API projections for the simplest `TryParse`
  overloads on `int`, `long`, `double`, `decimal`, `Guid`, and `DateTime`.
  Raven presents these as `Option<T>`-returning methods; projects can set
  `RavenFrameworkProjections` to `None` to restore the ordinary CLR surface.
  The exact mappings and failure recipes live in a versioned compiler catalog;
  stable projection IDs bind each catalog entry to its attributed Raven.Core
  bridge without relying on extension-method precedence.
- Added projection-specific diagnostics for missing, duplicate, and
  structurally incompatible framework projection bridges.
- Validated built-in projection source and bridge methods against their full
  reflected CLR signatures, including generic arguments and ref-kinds.
- Presented receiver-specific projection overloads in signature help and
  receiver-owned Raven signatures in hover, without exposing CLR `out`
  overloads; loose-file language-server projects now preserve the workspace's
  configured target framework when resolving framework and Raven.Core metadata.
- Added the first same-signature `Parse -> Result` projection for
  `Int32.Parse(string)`, with explicit null, format, and overflow mappings.
- Added Rust-style `let pattern = expression else { ... }` declarations. The
  `else` branch must exit, and successful pattern bindings remain available in
  the surrounding scope. Documentation now promotes `if let` and `let ... else`
  for binding-oriented control flow while retaining `is` for boolean pattern
  expressions.
- Preserved target typing for ordinary typed `let` declarations after their
  unification with pattern-declaration syntax, including shorthand union cases
  in `if` expression branches.
- Recognized interface implementations inherited from metadata base classes,
  avoiding spurious missing-member diagnostics on derived Raven classes.
- Ordered language-server diagnostic presentation per document and rejected
  older editor versions, preventing recovery diagnostics from reappearing
  after a newer compiler pass has cleared them.
- Invalidated reused metadata load contexts when a portable reference is
  rebuilt at the same path, preventing editor semantic requests from repeatedly
  failing after project outputs such as `Raven.Core.dll` change.
- Added `RAV1026`, a warning for lists that inconsistently mix comma and newline
  separators. Union case and enum member lists now diagnose the mixed style
  while continuing to parse both forms.
- Finite union payload products now understand `not`, `and`, and `or`
  combinators when proving collective case coverage. Removed the superseded
  binder-owned exhaustiveness implementation so diagnostics and semantic
  queries cannot drift between separate checking paths.
- Missing-case diagnostics now identify uncovered alternatives inside wholly
  or partially unmatched finite union payloads, such as
  `Error(OverflowException)` and `Error(.ServiceUnavailable)`, instead of
  collapsing the payload coverage to `Error`.
- Exhaustiveness analysis now proves complete positional tuple matches when
  tuple elements form a bounded finite product of booleans, enums, nested
  tuples, or discriminated unions, including nullable tuple carriers and
  pattern combinators.
- Top-level `not` and `and` patterns now participate in discriminated-union
  and enum exhaustiveness, including complements of payload cases whose
  payload domains are not themselves finite.
- Closed type unions and sealed hierarchies now apply conservative
  none/some/all coverage algebra to `not` and `and` patterns. Nullable domains
  likewise recognize `null`/`not null` as complementary coverage.
- Constant-true nested guarded patterns now contribute their underlying
  coverage, while dynamic or false guards remain conservative. A rest-only
  sequence pattern is recognized as total for a compatible sequence input,
  and reachability diagnostics use the same shared catch-all classification.
- Compile-time-true match-arm guards now contribute consistently in every
  domain, including `bool` and catch-all reporting. The singleton `unit` and
  null-only domains are analyzed explicitly.
- Match diagnostics and `SemanticModel.GetMatchExhaustiveness` now use one
  authoritative evaluator across boolean, nullable, enum, union, sealed
  hierarchy, structural, and numeric pattern domains. Diagnostics report every
  missing semantic case returned by the API, while flow-sensitive struct-union
  default-state handling remains limited to catch-all reachability warnings.
- Match diagnostics and the semantic exhaustiveness API now use the same
  interval analysis for integral comparison, range, `not`, `and`, and `or`
  patterns. Complementary numeric arms can prove a match exhaustive, guarded
  arms remain conservative, and a redundant catch-all is reported after full
  explicit coverage.
- Match exhaustiveness now combines nested discriminated-union case patterns,
  so arms such as `.Error(.WrongCredentials)` and
  `.Error(.ServiceUnavailable)` can collectively cover the complete `Error`
  payload without requiring a discard arm. Finite `bool` payloads and bounded
  Cartesian combinations of multiple finite payloads are analyzed likewise.
- Adopted `let`/`var` as the standard spelling for lexical bindings while
  retaining `val`/`var` for properties and signature-like declarations. A
  `let` local remains semantically read-only and is displayed as `val` by hover
  and symbol presentation. The former optional `PreferValInsteadOfLetAnalyzer`
  was replaced by the optional `PreferLetInsteadOfValAnalyzer` (`RAV9035`) and
  its code fix. `RAV9004` and its code fix are now provided by
  `VarCanBeLetAnalyzer` and recommend `let` when a lexical `var` is never
  reassigned.
- Async-iterator method declarations now suspend incomplete awaits in
  `MoveNextAsync` and return a
  pending `ValueTask<bool>` instead of synchronously blocking in
  `TaskAwaiter.GetResult()`. Their kickoff methods now carry
  `AsyncIteratorStateMachineAttribute` metadata, so async streams such as the
  greenhouse telemetry sample no longer occupy the caller thread while
  awaiting delays or I/O.
- Added primary-constructor accessibility modifiers after the type name and any
  type parameters, e.g. `record struct Year private (Value: int)`. Constructor
  accessibility is independent of accessibility on promoted parameters, so
  records and other primary-constructor types can expose data while restricting
  construction to factories or the containing assembly.
- Improved language-server recovery after rapid edits by keeping analyzer
  diagnostics on the active compiler snapshot and forwarding reusable
  incremental semantic state across intermediate snapshots.
- Reduced analyzer latency by enumerating narrowly registered expression-statement
  operation actions without constructing unrelated operation graphs, and by
  filtering unused-method invocation candidates before semantic lookup.
- Reused metadata load contexts across incremental compilations when portable
  metadata references are unchanged.
- Kept document diagnostics demand-driven for source declarations instead of
  eagerly declaring every project syntax tree after each edit.
- Added struct-like discriminated union cases with named payload fields, e.g.
  `case Closed { Reason: string? = null }`. Defaulted fields are optional in
  named case construction, and `.Closed { ... }` lowers through the synthesized
  case constructor rather than mutable object initialization.
- Added statement-form `loop { ... }` for unconditional loops. `break` exits the
  loop and `continue` jumps to the next iteration using the same structured
  loop rules as `while` and `for`.
- Added labeled `break label` and `continue label` for targeting enclosing
  labeled loops. Unlabeled `break` and `continue` still target the closest
  enclosing loop, and labels on ordinary statements remain `goto` targets.
- Added keyword-first `match scrutinee { ... }` as the normal match expression
  form, aligning match expressions with match statements. The older postfix
  expression form remains supported for composition cases such as
  `try expr match { ... }`.
- Added support for `[method: ...]` attributes on class, struct, and record
  declarations with primary constructors, applying them to the synthesized
  constructor metadata.
- Added unsafe block expressions, allowing `unsafe { ... }` in value-producing
  expression positions while reusing the existing scoped unsafe context rules.
- Added `RAV0404` so conditional access reports an error when `?.` is used on
  a statically non-null receiver while preserving member binding for tooling.
- Fixed interface contract diagnostics so concrete classes report missing
  required interface members such as `IDisposable.Dispose`.
- Fixed interface contract diagnostics so source explicit interface method
  implementations satisfy the required interface member even when the emitted
  method name is interface-qualified, and default interface members are not
  treated as missing required implementations.
- Fixed union case binding so bare case constructor calls require an explicit
  target type, while pattern hovers report case symbols projected from the
  matched union type arguments.
- Fixed cold language-server hover resolution for pattern locals nested in
  executable scopes such as `await for`, including both declarations and uses.
- Fixed union declaration attribute validation so source unions accept
  type-level attributes whose usage targets either class or struct carriers.
- Changed `RAV9016` member-can-be-private and `RAV9017`
  member-can-be-static analyzer diagnostics to default to informational
  suggestions. `RAV9017` no longer suggests making methods static when they
  satisfy inherited interface contracts such as `IDisposable.Dispose`.
- Added code fixes for compiler-owned match exhaustiveness diagnostics: `RAV2100`
  can insert a missing match arm, and `RAV2103` can remove a redundant catch-all arm.
- Aligned union content nullability with C# unions: Raven now tracks nullable
  parenthesized union contents from constructor/member case types, treats
  `TryGetValue(out T)` as an extraction helper instead of an extra case source
  when constructors exist, and imports nullable C# union contents from .NET 11
  metadata.
- Aligned nullable union contents with the C# access pattern: `HasValue` now
  follows `Value != null`, `null` patterns over class unions check both the
  carrier reference and active `Value`, and nullable-content parenthesized
  unions no longer expose `null` as a pseudo member type. Bare `null` no longer
  implicitly converts to nominal or Raven.Core union carriers just because one
  payload type is nullable.
- Changed plain Raven `union` declarations to synthesize struct carriers by
  default, matching the C# generated-union direction. Raven.Core `Union<...>`,
  `Option<T>`, and `Result<T, E>` now use that default struct carrier shape.
  Struct-union match exhaustiveness now follows the C# contract: declared cases
  are source-exhaustive, and the inactive `default` carrier is not treated as a
  semantic case that must be written in source. Defensive catch-all arms on
  struct unions are still allowed when local flow says the inactive carrier
  state is physically possible, but active local values report redundant
  catch-all arms. Passing a struct-union value that may still be the inactive
  `default` carrier to a struct-union parameter now reports `RAV0405` at the
  call site, so callee parameters can keep their active-value contract. Omitted
  optional struct-union arguments whose default is the inactive carrier now
  report the same diagnostic. Lowering and emit keep responsibility for
  defensive runtime fallbacks when metadata consumers or forced default carriers
  bypass Raven's source checks.
- Returning a struct-union value that may still be the inactive `default`
  carrier now reports `RAV0406` at the return boundary, preserving the same
  active-value contract for callers.
- Fixed matching over nullable union carriers (`U?`) so union case patterns are
  checked against the underlying union while `null` is treated as a separate
  nullable-wrapper case for exhaustiveness. This applies to both `union struct`
  and `union class` carriers and does not make `null` a union pseudo-case.
- Added .NET 11 C# interop coverage for Raven-produced union carriers and made
  metadata nullability loading tolerate preview reflection types that do not
  support `NullabilityInfoContext`.
- Added `SemanticModel.GetMatchExhaustiveness(MatchStatementSyntax)` so tooling
  can query the same exhaustiveness information for match statements that it
  already can for keyword-first and postfix match expressions.
- Struct-union parameters and `self` are now treated as active inside the
  callee, relying on call-site diagnostics to reject possibly inactive carriers
  before entry. Raven.Core `Option<T>` and `Result<T, E>` helpers no longer need
  source-level defensive default arms, and lowered source-exhaustive matches now
  throw when no arm matches instead of falling through with a default result.
- Raven.Core `Option<T>` and `Result<T, E>` JSON converters now serialize the
  inactive default carrier as JSON `null` instead of emitting no token or an
  empty object.
- Fixed expanded `params` argument target typing so extra positional arguments
  are bound against the params element type, including target-typed union cases.
- Fixed extension member completion after partially typed member names so
  imported metadata extension methods are offered for prefixes such as
  `widget.Dou`.
- Fixed editor compiler diagnostics after hover/inlay-style semantic queries so
  presentation-only cache entries do not cause false missing local or missing
  member errors in the same document snapshot.
- Fixed member completion for interface-typed receivers so members inherited
  through implemented interfaces are offered on values such as `IQueryable<T>`.
- Added `scripts/build-project-samples.sh` to build all source sample projects
  under `samples/projects` separately from the standalone sample compiler script.
- Converted `Raven.Core` to a normal Raven MSBuild project so it builds through
  the shared Raven language targets and participates in project references.
- Added receiver-aware pipe target completion after `|>` and in the following
  identifier, including applicable in-scope functions/static methods and
  extension methods. The language server now registers `>` as a completion
  trigger so typing `value |> ` opens the suggestion list.
- Fixed editor diagnostics after text edits so syntax diagnostics are refreshed
  from the pending document text immediately, clearing stale parser errors
  while semantic diagnostics remain deferred.
- Fixed editor diagnostic flicker while typing by translating the last computed
  snapshot diagnostics across pending edits until fresh diagnostics are ready.
- Changed `RAV0403` to report on the full `<expr>!` nullable suppression
  expression and describe that the operand is treated as non-null.
- Fixed member completion after nullable suppression expressions such as `x!.`
  and target-typed `default!.`.
- Fixed inlay hint flicker while editing by keeping visible providers stable
  until a debounced refresh can request translated cached hints for pending
  document text or fresh hints from the loaded workspace snapshot.
- Fixed semantic queries for top-level global statements so editor features bind
  through the compiler-owned top-level statement binder instead of throwing.
- Added `RAV9034` for standalone value-producing expressions whose result is
  known to be unused, such as literal/variable unary and binary expressions in
  `unit`-returning bodies. Calls remain exempt.
- Fixed `RAV9033` disposable-object diagnostics to use generic disposable-value
  wording instead of guessing an object name from locals or producer members.
- Fixed hover on `default` expressions so it shows a `default(T)` constant
  expression preview instead of being suppressed as a keyword.
- Fixed `use` declarations so nullable disposable targets such as
  `IDisposable?` are rejected and invalid resources are not registered for
  disposal.
- Fixed inlay hint refreshes for top-level invocations with function arguments
  so the refreshed request does not fail while rebinding global statements.
- Fixed semantic symbol info for callable instance members so hovering or
  analyzing the invoked name in `callback()` returns the member symbol instead
  of the delegate `Invoke` method.
- Fixed document compiler diagnostics so attributes on union declarations are
  validated against the union type instead of synthesized helper methods after
  editor semantic warm-up.
- Fixed local symbol queries so inferred generic constructor initializers such as
  `val values = List<JsonValue>()` return the constructed type instead of an
  incomplete `List<>` symbol.
- Fixed `self.` completion inside instance members and instance extension
  members, restored partial property/event definition-implementation merging,
  and re-enabled fast semantic coverage for positional pattern assignments.
- Fixed attribute diagnostics so `GetDiagnostics()` reports invalid attribute
  targets, duplicate attributes, and non-constant attribute arguments during the
  diagnostic pass instead of depending on prior `GetAttributes()` queries.
- Fixed semantic diagnostics so method-like members, primary constructor
  parameters, indexer parameters, indexer async getters, and constructor
  initializers are reported during `GetDiagnostics()` even when symbol
  declarations were already cached.
- Fixed diagnostic reuse for type declarations so partial-method, sealed
  hierarchy, and static-type storage diagnostics remain available after
  executable binding reuses cached declaration state.
- Fixed duplicate diagnostics when rebinding finalizer declarations and partial
  method definition/implementation counterparts.
- Fixed complete semantic diagnostics so `GetDiagnostics()` collects
  declaration-binder diagnostics instead of taking the document-scoped
  incremental diagnostics path.
- Fixed top-level `Main` entry-point discovery so invalid file-scoped statements
  report `RAV1021` without also synthesizing or selecting a competing
  top-level-program `Main`.
- Fixed completion on cold semantic models so earlier top-level declarations
  initialized from invocations or function expressions contribute their inferred
  types to member lists and completion descriptions.
- Fixed full diagnostics for top-level function attributes and extern
  top-level functions with bodies.
- Fixed qualified generic type lookup in member-access-shaped type expressions
  such as `System.Func<int, string>`.
- Fixed macro-expanded local declarations so documentation-comment lookup uses
  the declarator syntax node instead of a token-only span, avoiding crashes when
  inspecting expanded documents.
- Fixed member completion after `nameof(...)` so the receiver is treated as
  `string` instead of using the named symbol's type.
- Fixed target-typed `default` for reference types so it is treated as a
  nullable null value. Returning or assigning it to a non-nullable reference now
  requires `default!` and reports the existing null-assignment diagnostic when
  omitted.
- Added first-class MSBuild language targets for `.rvnproj` builds. Raven projects
  now build through `dotnet build`, produce SDK-style outputs, and can be consumed
  from C# projects through normal `ProjectReference` when wired to
  `build/Raven.Language.targets`.
- Deprecated legacy `.ravenproj` project files in favor of MSBuild-backed `.rvnproj`
  projects. The CLI now warns when compiling a legacy project file.
- Added `[Receiver]` and `[Receiver<T>]` trailing-block parameters. An
  unparameterized trailing block passed to a one-argument function parameter
  marked with `[Receiver]` can access receiver members directly inside the block;
  `[Receiver<T>]` narrows member lookup to an explicit compatible receiver type.
- Added combined builder/receiver trailing blocks for DSLs such as
  `[Builder<UiBuilder>, Receiver<WindowBuilder>] content: () -> UiNode`. The
  result builder handles block lowering, while the receiver builder exposes the
  component-specific member scope and produces the sub-result through
  `BuildFinalResult(component, receiver)`.
- Added class-only `base` expressions for instance members, enabling explicit
  base-member access and non-virtual base method invocation such as
  `base.OnFrameworkInitializationCompleted()`.
- Added `_` discard parameters for function expressions and parameterized
  trailing blocks. They consume the delegate parameter slot without introducing
  a body-visible name or unused-parameter warning.
- Trailing blocks now bind to the final visible function-typed parameter even
  when earlier optional parameters are omitted with default values, enabling DSL
  APIs such as `StackPanel(spacing: 8.0) { ... }` with
  `content: (() -> UiNode)? = null`.
- Added opt-in diagnostic `RAV9029` for bare member invocations and member accesses whose
  returned value is ignored. Assign the returned value to a target, assign it to `_`, return
  it, or pass it on. The analyzer is disabled by default while it uses whole-analyzer mode.
- Added `--returned-value-handling <default|full|none|info|warning|error>` and
  `--force-returned-value-handling` to configure `RAV9029` from the compiler CLI.
- Added project-file mode configuration for `RAV9029` through `ReturnedValueHandlingMode` /
  `RavenReturnedValueHandlingMode` and `EnableReturnedValueAnalyzer` /
  `RavenEnableReturnedValueAnalyzer`.
- Extended unused-variable analysis to report unused callable parameters as warning
  `RAV9030`, covering methods, `func` statements, constructors, operators, and function
  expressions.
- Added hidden analyzer diagnostic `RAV9031` for unused wildcard namespace imports within
  the lexical scope that declares them, with cleanup support through the redundant-import
  code fix.
- Added analyzer diagnostic `RAV9033` for disposable objects returned from calls or object
  creation that are assigned to ordinary locals or discarded without a `use` declaration or
  direct `Dispose()` call before scope exit.
- Added source-applicable invocation parameter-name inlay hints. Positional arguments now
  display their resolved parameter names, such as `StackPanel(spacing: 8.0)`, while already
  named arguments are left alone. Positional and nominal deconstruction patterns now also
  display inferred element names when the tuple or `Deconstruct` shape provides them.
  Raven inlay hints now have a master VS Code setting plus separate per-kind settings for
  inferred types and name hints.
- Fixed editor diagnostic scheduling so open, edit, and save follow-up passes include
  analyzer diagnostics such as unused locals and parameters, while typing uses a throttled
  document-scoped analyzer pass instead of running full-project analyzers on every edit.
- Fixed Raven.Core metadata union case imports so `import System.Result.*` and
  `import System.Option.*` bring `Ok`, `Error`, `Some`, and `None` into
  unqualified scope even though the PE case types are emitted as standalone
  types.
- Fixed `RAV9012` so inferred target declarations such as `val x = ...` are not
  reported just because the initializer has a nullable type.
- Fixed `RAV9019` so async `Main(args: string[]) -> Task` methods identified as
  application entry points are not reported as unused when a synthesized entry-point
  bridge is used.
- Fixed pattern matching, propagation, and carrier conditional access over
  Raven.Core metadata unions by matching logical case wrappers and constructed
  PE case types by stable metadata identity.
- Unused parameter, method, and property analyzer diagnostics now skip members
  that are required by virtual/override or interface implementation contracts.
- Fixed full-document and focused-range inlay hints for small real-world files
  so target-typed constructor shorthand arguments such as
  `.(1, "Ana", 29, true)` still show source-applicable parameter names.
- Fixed VS Code inlay refresh behavior so Raven edits re-request visible hints
  after the existing debounce, and locally superseded inlay requests no longer
  publish an empty hint set that can make hints flicker off.
- Fixed VS Code project build, run, and debug commands so `.rvnproj` targets use
  the `rvn build` frontend instead of invoking the `rvnc` compiler driver with
  publish-only arguments.
- Split unused local and unused parameter analysis into distinct built-in analyzers while
  keeping `UnusedVariableAnalyzer` as a compatibility disable name, and tightened analyzer
  symbol matching so equivalent lazy-bound symbols are compared with Raven symbol equality
  instead of object identity.
- Renamed the property initialization diagnostic analyzer to
  `UninitializedPropertyAnalyzer`, generalized its wording from auto-properties to
  stored properties, and added `UninitializedFieldAnalyzer` for explicit private fields.
- Constructor declarations now participate in lightweight member signature declaration,
  making symbol-based analyzers see constructor parameters deterministically after edits
  without requiring a prior full body bind.
- Workspace analyzer diagnostics now log cache hits, misses, stores, cancellations,
  failures, and per-analyzer execution failures so editor diagnostic latency can be
  traced without conflating it with foreground semantic requests.
- Analyzer infrastructure now supports Roslyn-style operation actions through
  `RegisterOperationAction`, and the document analyzer driver dispatches them from one
  shared operation traversal. Returned-value and immutable-collection result analyzers
  now use operation actions instead of syntax callbacks that each queried operations.
- Language-server semantic tokens now focus on semantic symbol classifications and
  regex string specialization, leaving keywords, literals, comments, and operators to
  the VS Code TextMate grammar. The grammar now covers ordinary attributes,
  documentation comments, character literals, labels, constructor-like calls, dot
  punctuation, and missing Raven keywords such as `goto`, `yield`, `fixed`, and `new`.

### Changed
- Unused local value diagnostics now say `Value '<name>' is never used.` while unused
  parameters continue to say `Parameter '<name>' is never used.`.
- `TopLevelAttribute` is now generated in the `System.Runtime.CompilerServices`
  namespace, so namespace-member containers are marked with
  `System.Runtime.CompilerServices.TopLevelAttribute`.
- Namespace-level `func` and `const` declarations now bind as namespace-level members emitted into a synthesized `[TopLevel]` `NamespaceMembers` container, and static types marked with `[TopLevel]` promote their static members through namespace lookup/completion. `AllowNamespaceMembers` controls declarations independently from top-level statements, while `AllowNamespaceMemberImports` controls namespace promotion from namespace-member containers.
- Project and single-file compilations now generate a prelude of global imports by default, including common `System` namespaces plus `System.Result.*` and `System.Option.*`; ordinary union cases are no longer introduced unqualified unless imported or referenced with target-typed `.Case` syntax.
- Attached declaration macros targeting types are now valid on union case declarations, matching the compiler's representation of cases as generated case types.
- Records now use the full primary-constructor parameter list as their canonical value shape, including non-public promoted parameters, and record bodies now reject extra instance storage and secondary instance constructors.
- The language server now provides source-applicable inlay hints for inferred local type annotations and inferred function return type annotations, and the VS Code extension can toggle those hints with `raven.inlayHints.inferredTypes.enabled` or `Raven: Toggle Inferred Type Inlay Hints`.
- Language-server document edits now preserve `SourceText` change ranges through incremental sync, fall back to full parsing for whole-document or large paste edits, debounce macro-consumer refreshes, and keep normal typing diagnostics syntax-only so expensive semantic diagnostics wait for open/save.
- Match expression arms now accept direct `return` expressions, aligning them with other expression-oriented value positions while preserving diagnostics for statement `return` inside block-expression arms.
- `for` loop identifier targets now support explicit type annotations such as `for item: int in items`, and inferred type inlay hints are offered for unannotated identifier targets.
- Outer pattern-binding contexts now allow implicit deconstruction captures to carry type annotations without repeating the binding keyword, so forms such as `val (key: string, value: int) = entry` and `val [head: string, ..tail: string[]] = values` parse as typed captures.
- Equality operands now target-type member-binding shorthand such as `value == .Case`, matching pattern shorthand while still allowing `value is .Case` when pattern syntax better communicates intent.
- Raven unions now align their emitted interop surface with the .NET 11 union
  direction by implementing `IUnion`; body-declared Raven case types are recorded
  on the carrier with Raven-owned metadata instead of non-standard system case
  marker attributes.

### Fixed
- String `==` and `!=` now use `System.String` value equality instead of
  reference equality.
- Function expressions inside instance methods now capture unqualified instance
  property receivers correctly.
- Interface-typed receivers now resolve `System.Object` instance members such
  as `GetType`, `ToString`, `Equals`, and `GetHashCode`.
- Function expressions now capture variables assigned through the left side of an
  assignment, including function expressions passed as call arguments, and nested
  function expressions now reuse the owning method closure instead of snapshotting
  stale values.
- Diagnostic binding now follows macro replacement declarations, preventing
  attached property macros from reporting the original property as a duplicate
  member after the replacement property has already been registered.
- Target-typed enum member defaults on external enum parameters and `double`
  default parameter constants now bind and emit without compiler crashes.
- Emitting direct signatures over NuGet `ref/` assembly types now prefers the
  corresponding `lib/` runtime assembly and guards type probing failures,
  preventing external packages such as Avalonia from crashing emit during
  runtime type resolution.
- Metadata base-type resolution now falls back to compilation-level package
  references when a module-local reference walk misses, fixing inherited
  member lookup and reference conversions across package sibling assemblies
  such as Avalonia `Button` to `Interactive`.
- Runtime MethodInfo resolution now handles methods from constructed generic
  package types, fixing emit for calls such as Avalonia `StackPanel.Children.Add`.
- The compiler CLI now copies native NuGet runtime assets for the current
  platform when running or publishing, so packages such as Avalonia can load
  native dependencies like SkiaSharp from the output directory.
- Unused-parameter analysis now treats constructor parameters passed to
  constructor initializers such as `base(value)` as used.
- `MemberCanBeStatic` now recognizes instance callable members invoked through
  bare identifier syntax, avoiding false positives for callback wrapper methods.
- Unused-property analysis now respects interface property implementations.
- Removed builder-method-name exemptions from unused-method analysis so generic
  unused-member diagnostics are not coupled to DSL lowering conventions.
- Incremental executable-owner analysis now treats top-level `func` statements as
  function owners instead of generic global-statement owners, improving editor
  recovery after wrapping top-level statements in `func Main`.
- Semantic invocation queries can now use already-available argument types to
  construct simple generic metadata candidates, avoiding unnecessary body
  rebinding for language-service hovers such as `JsonSerializer.Serialize` /
  `Deserialize<T>` chains.
- Large full-document inlay hint requests now avoid cold expensive binding
  fallbacks, reducing editor request pile-ups while small documents and precise
  range requests can still bind to fill missing hints.
- Full-document inlay hint responses now skip eager tooltip markdown generation,
  keeping initial annotation payloads lighter while focused range requests still
  include richer tooltip content.
- Pattern inlay hints now skip assignment patterns that deconstruct into
  existing variables, while still annotating `val`/`var`/`let` pattern
  declarations and inline pattern bindings.
- The redundant-import quick fix now offers a document-level action to remove all imports already covered by global imports.
- Optional enum parameter defaults now accept target-typed member binding syntax such as `value: ServiceLifetime = .Scoped`.
- Qualified constant-member patterns such as `value is Math.PI` and enum-member patterns such as `value is JsonValueKind.True` now bind and emit as value comparisons instead of type tests.
- Enum conversions now follow C#/CLR rules for explicit enum-to-integral, integral-to-enum, and enum-to-enum conversions, and emitted casts preserve CLR-open enum values that are not declared members.
- Attribute arguments now accept enum constants in qualified and target-typed forms, including enum flag compositions such as `.Class | .Delegate`.
- Type wildcard imports now expose enum members alongside normal static members and constants, and individual enum members can be imported as specific constant imports.
- Delegate declaration attributes now bind to the delegate type, validate against the CLR `delegate` attribute target, and emit to delegate metadata.
- Generated display-class closure frame types now consistently carry `CompilerGeneratedAttribute` metadata.
- Metadata type symbols now preserve declared visibility from referenced assemblies, and delegate declarations now emit CLR delegate metadata with nested placement, `abstract sealed` flags, by-ref parameter shapes, and `unit` `Invoke` returns as `void`.
- Conditional element access such as `values?[index]` now emits correctly for array receivers.
- Line-leading pointer dereference assignments such as `*ptr = value` now parse as new statements after expression statements instead of being treated as multiplication continuations.
- Semantic symbol queries for user-defined unary and binary operator expressions now return the selected operator method instead of rebinding the expression out of scope.
- Mixed nullable equality checks with user-defined equality operators no longer recurse through target-type lookup.
- `GetDeclaredSymbol` on field declarators now returns the declared field instead of routing through local-variable binding.
- `GetDeclaredSymbol` on function-statement parameters now returns the method parameter symbol, and member-access completion can resolve parameter receivers without a prior bind.
- `GetDeclaredSymbol` on async methods with annotated `Task<T>` return types now completes the method signature before returning the symbol, avoiding stale provisional `Task` skeletons.
- Early method signature symbols now mark `ref` and `out` parameters as mutable, matching fully bound parameter symbols.
- Field-targeted attributes on auto-properties are now attributed to the synthesized backing field rather than validated against the property symbol.
- Expression-backed value patterns such as `person is { Name: name }` now compare against the runtime value of `name` even when `name` is a parameter or local rather than a compile-time constant.

## 2026-05-09

### Changed
- `RavenUnionJsonConverter` has been renamed to `RavenTaggedUnionJsonConverter` to make its tagged JSON shape explicit.
- `RavenTaggedUnionJsonConverter<TUnion>` now writes direct parenthesized union members such as scalar values and arrays under a tagged `value` payload, preserving existing flattened output for body-form cases while allowing `JsonValue[]` members to serialize.
- Added a dedicated `json-modeling-playground` sample that models JSON structure with records and unions, using both built-in Raven.Core and custom JSON converters.
- Collection literals target-typed as a union now use the single collection-shaped union member when one exists, so nested values such as `JsonValue[]` can be inferred inside `JsonValue` dictionaries.
- Language-server open and save events now schedule a deferred full diagnostic pass after the immediate syntax pass, so analyzer diagnostics appear without requiring a document edit and stale analyzer diagnostics can be cleared after saving.
- Language-server semantic tokens now skip unmapped classifications instead of emitting default keyword tokens, and classify local declaration/designation identifiers from syntax so open-document tuple deconstruction edits keep correct spans and token types.
- Semantic declared-symbol lookup for pattern declaration assignments now binds the owning statement before fallback synthesis, so hovering a tuple-deconstruction declaration such as `val (no, _) = Get()` reports the same element type as later references.
- Language-server tuple type hovers now present the underlying `ValueTuple<...>` shape with its implemented interfaces, while tuple element type hovers resolve to the individual element types for both named and unnamed tuple syntax.

## 2026-05-08

### Changed
- Attached property macros that replace their target declaration with syntax derived from the original property now reuse the effective declaration symbol, preventing false duplicate-member diagnostics while preserving generated accessors.
- The language server now ignores IDE build/debug artifact folders such as `.raven-build` and `.debug` when deciding whether watched file changes should reload the workspace, so compiling from the editor does not disturb open-document semantic state.

## 2026-05-07

### Changed
- Target-typed constructor binding now supports `.(...)`, allowing assignments, arguments, and collection elements with a known target type to construct that type without repeating its name.

## 2026-05-04

### Changed
- Runtime-async entry-point bridges targeting .NET 11 now call `System.Runtime.CompilerServices.AsyncHelpers.HandleAsyncEntryPoint(...)` for `Task` and `Task<int>` `Main` methods instead of hand-emitting awaiter blocking, while Raven-specific `Result<..., ...>` entry points keep their result-mapping bridge.
- `Raven.Core` now treats each target-specific `bin/<Configuration>/<TargetFramework>/Raven.Core.dll` as an incremental build output, so it is regenerated only when the Raven source list or sources change, or when the target DLL is missing.
- Standard union type syntax is back: `T1 | T2` now parses as a type annotation
  and binds to `System.Union<T1, T2>` from `Raven.Core`, with arities two
  through five.
- `$identifier` string interpolation shorthand now preserves the identifier width even for keyword-shaped names, preventing subsequent syntax and editor spans from drifting while binding can still diagnose unresolved names.
- `Option<T>` JSON serialization now maps `.Some(value)` directly to the payload JSON and `.None` to `null`, matching JSON's native nullable-property shape. `Result<T, E>` keeps its tagged converter shape.
- Constant field emission now supports narrow and unsigned primitive constants, fixing metadata enum members with byte-backed values such as `JsonValueKind.Null`.

## 2026-05-03

### Changed
- Trailing blocks now support an optional parameter clause before the body, such as `GET("/{id:int}") { id => ... }` or `Combine { (left, right) => ... }`, and use the declared arity during overload resolution.
- Target-typed union case construction now works in constructor arguments even when overloads have same-arity parameters, so nested calls such as `Theme(None)` and `Theme(.None)` bind against an `Option<T>` parameter without requiring `Option<T>.None`.
- The language server now ignores generated, package/cache, build output, and temporary probe directories when discovering projects or reacting to watched-file changes, reducing full workspace reload storms in VS Code.
- Semantic queries over brace trailing blocks now bind the trailing block expression instead of throwing, so hover and related editor features remain stable when the cursor lands inside that syntax.
- VS Code syntax highlighting and language-server semantic tokens now classify call targets inside trailing-block DSLs consistently, including uppercase extension-style calls and constructor-like calls such as `GET("/") { ... }`.
- Generic type construction can now infer type arguments from explicit function-expression parameters even when a same-named non-generic type exists, enabling DSL shapes such as `GET("/{id:int}", func (id: int) => ...)` to select `GET<int>` when the non-generic constructor is not applicable.
- Repeated trailing-block calls in the same scope now emit distinct lambda bodies, including builder-rewritten trailing blocks used by lightweight DSLs.

## 2026-05-02

### Changed
- `Raven.Core` now includes the generic `RavenTaggedUnionJsonConverterFactory`/`RavenTaggedUnionJsonConverter<TUnion>` implementation for opt-in JSON serialization of ordinary Raven unions, while `Option<T>` and `Result<T, E>` keep their specialized JSON converters.
- Generic Raven union JSON serialization now supports a configurable case discriminator property. `"$case"` remains the default, and `[RavenTaggedUnionJsonConverter("kind")]` can be used when a domain-specific property name fits better.

## 2026-05-01

### Changed
- Overload resolution now target-types collection literal arguments against array-shaped parameters when overload candidates disagree on the parameter type, so calls such as `Activator.CreateInstance(type, [value])` bind to the `object?[]` overload instead of falling back to an inferred immutable list.
- Trailing blocks can now receive implicit closure parameters from the selected final function parameter. Parameters are available as Swift-style `$0`, `$1`, etc., and `it` aliases the first lambda parameter.

## 2026-04-30

### Changed
- Brace trailers now bind as Swift-like trailing closure call syntax. `callee(args) { ... }` appends a zero-argument closure as the final argument, and `callee { ... }` is accepted when overload resolution can bind that trailing closure.
- Trailing closure parameters annotated with `[Builder<T>]` now activate builder-block binding for expression components and `if`/`else` composition through Swift-like builder methods such as `BuildExpression`, `BuildBlock`, `BuildOptional`, `BuildEither`, and `BuildFinalResult`.
- `TrailingBlockExpressionSyntax` now wraps a normal block body, so statements inside trailing blocks are ordinary Raven statements instead of initializer-style entries.

Impact:
- `Type { ... }` is no longer an initializer-like DSL placeholder. It is valid only when a function, method, delegate invocation, or constructor accepts the final closure argument; object initialization remains `Type with { ... }`.

## 2026-04-24

### Changed
- `while` statements now support the same outer pattern-binding form as `if`, allowing loops such as `while val pattern = expr { ... }` where captured pattern locals are available inside the loop body.
- Object initialization can now use the `Type with { ... }` form. The compiler binds this through the existing object-initializer path, so `init`, `required`, compound assignment, and event subscription semantics are preserved while brace trailers remain available for future DSL work.
- Brace trailers are now represented in the syntax tree as `TrailingBlockExpression` nodes with `TrailingBlockEntry` children instead of object-initializer syntax nodes, matching their role as the future DSL block surface.
- Brace trailers no longer bind as object initializers. They now report a dedicated trailing-DSL diagnostic until DSL binding support is introduced.
- The language docs and reference spec now describe the current union-body model more directly: body-form unions use `case` declarations inside an ordinary member body, may contain authored members beside cases, may be declared `partial`, reserve `Value`/`HasValue`, and follow record-like `ToString()` override behavior while still rejecting authored union equality/hash special members.
- The compiler diagnostics reference now includes the union-specific reserved-name and unsupported-special-member diagnostics `RAV2111` and `RAV2112`.
- Language-server semantic tokens now recognize string arguments passed to parameters annotated with `System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.Regex` and classify those literals as `regexp`.
- Attribute binding and emission now route `[module: ...]` attributes to the module symbol and `[field: ...]` attributes on auto-properties to the synthesized backing field.

Impact:
- The written language reference now matches the compiler and `Raven.Core` surface more closely for modern unions such as `Option<T>` and `Result<T, E>`.
- Editors can apply regex-aware highlighting to Raven string literals when APIs use the standard .NET string-syntax annotation.
- Module-level metadata and backing-field-specific property annotations now round-trip through symbols and emitted assemblies.

## 2026-04-19

### Changed
- Union body declarations now require the `case` keyword for each declared case, and the parser preserves that keyword in the syntax tree for nested union case clauses.
- Samples, language docs, and compiler-facing symbol displays now reflect the prefixed declaration form, including member-keyword formatting for nested union case types.

Impact:
- Union declarations now read as `union Result<T> { case Ok(value: T) case Error(error: E) }`, which gives nested case declarations an explicit syntactic marker ahead of larger union-surface changes.
- Tools and diagnostics that request member keywords now identify nested union case types as `case` members instead of displaying them like ordinary nested types.

## 2026-04-18

### Changed
- Top-level binder creation no longer eagerly binds global statements during root-binder setup. Raven now finishes source declaration/member registration across the compilation before top-level statements are bound, which removes file-order sensitivity for top-level code that touches members declared in other source files.
- Match-expression arms now tolerate the arm expression starting on the line after `=>`, which fixes recovery/binding failures for multiline union matches such as JSON serialization helpers.

Impact:
- Multi-file Raven projects no longer spuriously report missing members like `RAV0103` / `RAV0117` just because a top-level statement bound before another file had registered its members.
- Newline-styled `match` arms bind the same way as single-line arms, which makes editor diagnostics and sample projects much less brittle around multiline union handling code.

## 2026-04-08

### Changed
- Deconstruction patterns now support named elements for `Deconstruct`-backed shapes in both matching and declaration/assignment forms. Raven accepts forms such as `Person(Items: val items, Name: val name, Age: 42)` and `val (Items: items, Name: name, Age: age) = person`, binds named elements by `Deconstruct` parameter name in any order, and now reports `RAV1602` when a supplied deconstruction name does not exist on the target shape.
- Union carriers now expose a conventional union-root `Value` property, and `union struct` carriers reserve discriminator `0` as an uninitialized/default state so `default(U).Value` is `null` until a real case is assigned.
- `Raven.Core` now declares `Option<T>` and `Result<T, E>` as `union class` carriers instead of `union struct`, removing the implicit default/uninitialized state from the standard library’s primary algebraic carriers.
- Synthesized union `Value` now follows the carrier nullability contract more closely: `union struct` exposes `Value: object?`, ordinary class carriers expose `Value: object`, and class carriers with nullable member payloads expose `Value: object?`.
- Synthesized union carriers now also expose `HasValue`, allowing callers to distinguish default/uninitialized `union struct` values from active cases even when nullable annotations are not observed by consuming C# code.
- Statement-form `if`, `if val`, `while`, and `for` bodies can now be written without braces when the body statement starts on the next line. Raven now rejects same-line non-block forms such as `if flag return`, while still allowing block bodies and `else if` chaining on one line.
- Parenthesized union declarations now use `|` between member types instead of `,`, and compiler-facing displays such as symbol formatting, hover text, signature help, samples, and spec examples now reflect the bar-separated form consistently.
- Async iterators now support C#-style enumeration cancellation. `CancellationToken` parameters marked with `[EnumeratorCancellation]` receive the token passed to `GetAsyncEnumerator(...)`, Raven warns when async iterators declare `CancellationToken` parameters without marking one, and parenthesized async lambdas accept the inline parameter-attribute form `async ([EnumeratorCancellation] token: CancellationToken) => ...`.
- Iterator statements now accept the shorthand `yield expression` in addition to `yield return expression`. Both spellings lower identically, while `yield break` remains the early-termination form.

Impact:
- Raven unions now align more closely with the emerging .NET/C# union contract: tooling and runtime consumers can inspect the active carrier payload through `Value`, while defaulted struct unions no longer masquerade as the first declared case.
- `Option` and `Result` now model only their authored case sets in ordinary use, instead of also carrying a silent struct-default state that callers had to treat as an extra runtime possibility.
- Control-flow statements read more naturally in Raven’s newline-sensitive style without reopening the same-line ambiguity that previously made single-statement bodies look like adjacent tokens instead of a structured body.
- Parenthesized unions now align their declaration syntax with Raven’s broader union-type notation, so authored code and tooling output present the same shape for unions like `union Payment(Cash | Card)`.
- Async streaming code now follows the same cancellation model as C# async iterators, including Minimal API-style handlers that expose the request cancellation token through an attributed lambda parameter.
- Iterator code can now use the shorter `yield value` spelling without changing semantics, which better matches the fact that iterator elements are produced rather than returned from the method.

## 2026-04-05

### Changed
- Collection expressions now continue to honor builder-backed target types such as `ImmutableArray<T>` in ordinary assignments, expression-bodied returns, and object-initializer property assignments. Non-empty `[...]` still reject non-collection targets, but they no longer fall back to `ImmutableList<T>` when the target is a supported builder-backed collection.

Impact:
- Samples and APIs that expose `ImmutableArray<T>` regain target-typed `[]` behavior, including macro expansion results and object-initializer assignments, while the earlier overload-resolution fix still preserves the intended `Cannot convert from 'ImmutableList<T>' to 'T'` diagnostic for non-collection targets.

## 2026-04-04

### Changed
- The Raven VS Code extension now supports `raven.sdkPath`, an SDK-root override that lets one extension installation target different Raven toolset builds by resolving `Raven.LanguageServer.dll`, `rvn.dll`, and related assemblies from a chosen SDK directory.
- Workspace-built language servers launched by the VS Code extension are now staged into an isolated extension-owned directory but keep the repository root as their working directory, so repo-relative assets such as `Raven.Core.dll` still resolve without leaving the live workspace binaries locked.
- The VS Code extension now restarts the Raven language client when workspace folders change in the same session, so diagnostics and project loading re-root correctly after switching between sample folders.

Impact:
- Developers can validate multiple Raven SDK builds or packaged toolsets against the same VS Code extension without rebuilding or retargeting the extension itself.
- Using the editor no longer competes as aggressively with local Raven builds, while project-backed diagnostics like `HelloWorld` still resolve `Raven.Core` and other repo-relative assets correctly.
- Opening a different Raven workspace in the same VS Code session no longer leaves the language server pinned to stale project roots.

## 2026-04-02

### Changed
- Overload resolution now respects `System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute` on applicable methods, including methods imported from referenced assemblies. Higher-priority candidates are kept before Raven runs its usual specificity comparison.
- Function and block bodies can now declare local `class`, `struct`, `record`, and `enum` helper types. These declarations are block-scoped in source and emitted as compiler-mangled nested types under the enclosing containing type.
- Imported extension members are now classified per member instead of per container. Classic extension methods continue to use `IsExtensionMethod`, while Raven/C#-style static extension members bind through extension-receiver metadata even when they live in mixed extension containers. Generic metadata extension methods now recover method type parameters correctly during PE import.
- Source classic extension methods declared with `static` members plus `ExtensionAttribute` now remain discoverable during same-compilation binding and through `CompilationReference` imports. Raven no longer caches `IsExtensionMethod = false` just because the method symbol was observed before its parameter symbols were assigned.
- `Raven.Core` parse errors now use a stricter `IError` contract: `Message` is required, `Cause` remains optional, and `IError.WithMessage(...)` now preserves the original error as the wrapped cause instead of constructing an invalid `ContextError`.
- `Raven.Core` now exposes generic `ContextError<TError>` and a typed `WithMessage(...)` wrapper, so Raven code can retain the concrete wrapped error type while still surfacing the shared `IError` contract. When callers only have `IError`, the erased wrapper shape is `ContextError<IError>`.
- `Result<T, E>` now also supports `WithMessage(...)` when `E : IError`, projecting only the error channel to `ContextError<E>` instead of wrapping the entire result carrier.
- Imported metadata types now compute `AllInterfaces` transitively from declared interfaces and base types instead of relying on reflection’s flattened view. This restores generic constraint checks like `E : IError` for metadata-backed types such as `ParseIntError` implementing `IParseError : IError`.
- Source explicit interface implementations now bind correctly for methods and properties because source interface members are registered before classes that implement them, including nested interface declarations. This also unlocks explicit interface property implementations such as `val IError.Cause`.
- Generic methods that lower captured lambdas now emit generic display classes when they need the enclosing method's type parameters. This fixes runtime `BadImageFormatException` failures in patterns such as `Result<T, E>.WithMessage(...)` implemented via `MapError(error => error.WithMessage(message))`.

Impact:
- Raven now matches C#’s overload-priority behavior for APIs that intentionally hide more specific overloads behind `OverloadResolutionPriorityAttribute`, which improves interop with modern .NET libraries and C#-authored metadata.
- Helper types can now live next to the code that uses them without being promoted to outer type scope, while keeping runtime metadata isolated behind compiler-generated nesting names.
- Mixed extension containers in referenced assemblies now interoperate more like .NET/C#: `int.parse(...)` binds again as a static extension member, while classic generic extension methods like `OptionExtensions.UnwrapOr<T>` continue to import as extension methods instead of degrading to unreadable metadata signatures.
- Classic C#-style source extension methods are stable again across both direct source binding and referenced-compilation imports, which restores samples and semantic tests that rely on `static class` + `[ExtensionAttribute]` interop semantics.
- Parse-oriented Raven APIs now expose a more coherent error surface to both Raven code and .NET consumers: every `IError` has a meaningful message, wrapping keeps provenance through `Cause`, and `Parse.rav` no longer relies on an invalid constructor call during core emission.
- Error-wrapping code no longer has to choose between provenance and static type information: callers can use `ContextError<TError>.Cause` when they want the concrete wrapped error, or treat the wrapper as plain `IError` through the explicit interface `Cause`.
- Result pipelines can now add context at the right abstraction level: `int.parse(text).WithMessage("...")` keeps the carrier as `Result<T, ...>` and only enriches the error payload.
- Metadata-backed generic constraints now see transitive interface implementations consistently, so extension members like `Result<T, E>.WithMessage(...) where E : IError` bind correctly from `Raven.Core.dll` and other referenced assemblies.
- Raven can now express .NET-style explicit interface members in source without spurious `RAV0315` failures, which makes contracts like `IError.Cause` compose cleanly with typed overload properties on the same type.
- Captured-lambda codegen is now stable for generic helper methods that flow constrained type parameters through higher-order functions, so error-channel projection helpers like `WithMessage` no longer compile successfully and then fail at runtime with invalid IL.

## 2026-04-01

### Added

- Added the standard `timer! { statements }` statement macro, including
  hygienic `Stopwatch` expansion, a release-code warning, and a runnable sample
  project. Its expansion is implemented in Raven inside `Raven.Macros`, making
  it an initial dogfooding case for moving standard macro behavior out of
  compiler-owned C# helpers.
- `Raven.Core` now defines `System.IParseError`, `System.ParseIntError`, `System.IntErrorKind`, and lowercase `int.parse(...)` static extension helpers that return `Result<int, ParseIntError>` instead of throwing for null, empty, format, and overflow failures.

Impact:
- Raven code can now use `int.parse("42")` and propagate parse failures through `Result` pipelines with `?`, avoiding direct dependency on CLR exceptions at the call site.

## 2026-03-28

### Changed
- `catch` clauses now reuse Raven’s pattern syntax instead of a bespoke `catch(Type name)` declaration form. Raven accepts preferred forms like `catch FormatException ex` and still parses parenthesized patterns such as `catch (FormatException ex)` for grouping and forward compatibility.
- Sealed hierarchies now include interfaces: Raven accepts `sealed interface` declarations, allows optional `permits` clauses on interfaces, and enforces the closed set across direct implementors and subinterfaces.
- Nested type declarations inside interfaces now participate in sealed-hierarchy modeling, so interface-scoped case-like records/classes can be used as direct sealed-interface members.
- Nested direct cases inside generic sealed hierarchies no longer capture outer type parameters at runtime. They now behave like algebraic-data-type cases, which fixes invalid CLR generic nesting and runtime failures such as `BadImageFormatException` when constructing generic sealed-interface cases.
- Sealed hierarchy signatures and hover now print `sealed` for sealed classes and interfaces, bare generic sealed roots are diagnosed consistently in storage-type positions, and target-typed `.Case(...)` patterns now bind for nested sealed-hierarchy direct cases when the scrutinee already determines the sealed root.
- Method generic `where` clauses are now initialized consistently for member methods as well as local functions, which fixes constrained generic math scenarios such as `where T : INumber<T>` inside sealed-hierarchy evaluators and other generic member bodies.
- Built-in binary operator binding now follows a fuller predefined numeric-promotion model, so `float`, `uint`, `ulong`, `short`, `ushort`, and `sbyte` participate consistently instead of only `int`/`long`/`double`/`decimal` plus a few ad hoc promoted cases.
- Unused-variable analysis now treats interpolated-string identifier reads as real local usage and falls back to binder-based local lookup when symbol lookup does not report the local directly, which fixes false positives such as `val content = ...; return "submitted: $content"`.
- `typeof` over open generic source types now emits the generic type definition token instead of an invalid placeholder-instantiated runtime type, which fixes runtime failures in scenarios like `typeof(Result<,>)` inside JSON converter factories.
- The file-local type modifier is now spelled `fileprivate` instead of `filescope`, aligning the surface syntax with its accessibility semantics and Swift-style precedent.

Impact:
- Exception handling syntax now aligns more closely with the rest of Raven’s pattern surface, reducing one-off grammar and leaving room for future richer catch-pattern work.
- Raven can model Java/Kotlin-style sealed interface families directly, including patterns where the direct cases live inside the interface declaration.
- Exhaustiveness and hierarchy validation now treat sealed interfaces consistently with sealed classes and record classes.
- Generic sealed hierarchies can now use nested case declarations without forcing CLR-style outer generic qualification such as `Expr<float>.Case`, which better supports ADT and future GADT-style modeling.
- Sealed-hierarchy direct cases are now documented and implemented as full named types whose nesting is optional source organization, while the nested form still supports `Expr.Case(...)` construction and target-typed `.Case(...)` patterns.
- Generic member methods now honor their declared `where` constraints during body binding, so generic math interfaces like `INumber<T>` can drive operator binding in normal member methods and generic sealed-hierarchy evaluators.
- Numeric expressions across Raven’s predefined types now behave much more uniformly, including float arithmetic/order comparisons and the unsigned/small-integral families.
- Interpolated strings no longer trigger bogus `RAV9027` warnings for locals that are only read inside `$name` / `${expr}` segments, including project-based app samples and async handler code.
- Project-based apps and Raven.Core JSON converters no longer fail with `BadImageFormatException` just from inspecting `Result<...>` / other open generic source types via `typeof`.
- Source code, tests, specs, and editor grammar should now use `fileprivate` for file-local type-like declarations and extensions.

## 2026-03-26

### Changed
- Synthesized union `ToString()` bodies now quote generic string and char payloads on the bound-body path, so parenthesized generic unions print values like `Either<Int32, String>("invoice")` and `Either<Char, String>('x')` instead of emitting raw unquoted payload text.
- Hover text for extension members now identifies them as extension methods/properties and shows the qualified declaring extension container instead of collapsing them into an ordinary containing type display.

Impact:
- Generic union `ToString()` output is now consistent with other quoted literal-style displays for string and char payloads, especially on synthesized carrier formatting paths.
- Extension APIs are easier to distinguish from ordinary instance members during hover in VS Code, especially when users need to see which extension declaration contributes a member.
- Hover now makes it clear which extension declaration contributes a member when multiple similarly named members are in scope.

## 2026-03-25

### Changed
- Raven unions now explicitly use one runtime model: a carrier plus independent case types. Body-form unions continue to synthesize case types, but those case types no longer form an inheritance hierarchy with the union root.
- Union construction, matching, propagation, and conditional-access lowering now consistently target carrier semantics, with `TryGetValue(out CaseType)` and pattern matching as the extraction surface.
- Compiler naming and docs continue the move away from the old “discriminated union” terminology toward the simpler `union` / `union case` vocabulary where possible.

Impact:
- `union` now has a clearer contract: it describes a closed carrier type rather than an inheritance-oriented object model.
- Users who want OOP subtype semantics should prefer Raven sealed hierarchies, while unions remain the right tool for closed carrier-style data modeling and Result/Option-style APIs.

## 2026-03-24

### Changed
- Declaration-oriented separated lists now accept newline-delimited separators in more places, including enum member lists, parameter lists, type-parameter lists, and type-argument lists. The syntax tree preserves explicit separator tokens when present, uses `SyntaxKind.None` for valid newline-delimited boundaries, and recovers same-line omissions with missing expected separator tokens.
- Enum member lists now also accept `;` as an explicit separator alongside `,`, while keeping comma as the canonical recovery separator when an explicit same-line separator is missing.
- Enum member lists now also diagnose mixed explicit separator kinds within the same declaration, so `,`/`;` style stays internally consistent while newline-delimited implicit boundaries remain neutral.
- Added warning `RAV9028` for unnecessary trailing separators in ordinary comma-delimited separated lists with closing delimiters. The warning only applies to real explicit trailing separator tokens and does not fire for newline-delimited implicit boundaries or enum member lists.
- Newlines are now modeled strictly as trivia in the syntax tree. Implicit statement and declaration termination uses surrounding end-of-line trivia together with `SyntaxKind.None` terminator/separator slots instead of any dedicated newline token.
- Imported .NET nullability now preserves ordinary nullable reference annotations such as `object.Equals(object?)`, `object.ToString() -> string?`, and `Console.ReadLine() -> string?`, while generic type-parameter positions only become nullable when metadata carries explicit `NullableAttribute` flags. This restores metadata-backed conversions like `string? -> Option<string>` without regressing LINQ and collection APIs that use plain `T`.
- Record value-member synthesis and record `with` expressions now work again under the imported-nullability model, because synthesized record support can once more see the expected nullable `object` members from metadata.
- Property patterns and nominal deconstruction patterns now treat nullable scrutinees such as `object?` as valid runtime-test inputs when the underlying non-nullable type can participate in the pattern. This fixes cases like `if candidate is Shipment { ... }` and `if x is Foo(...)` where the input was nullable only because of flow/state, not because the pattern itself was invalid.
- Patterns that introduce bindings now support nested `when` guards inside the pattern itself. This works in statement-form conditional binding, `for` pattern targets, and collection-comprehension pattern targets, so forms like `for val (id, amount when > 100) in orders` and `if val (id, name when name.Length > 5) = customer { ... }` bind the value and then apply either a secondary pattern guard or a boolean guard expression in the bound-local scope.
- Match exhaustiveness now treats pure deconstruction inside discriminated-union case payload patterns the same as direct payload binding when the deconstruction is total. In particular, extension-based `Deconstruct` patterns such as `.Error((val message))` no longer force a redundant `_` arm just to satisfy exhaustiveness.
- Syntax highlighting now treats `default`, type-parameter variance keywords (`in`/`out` in generic parameter lists), and conversion-operator keywords (`implicit`/`explicit`) as first-class keyword/modifier scopes in the editor grammar, and focused semantic/highlighter tests lock that coverage in.
- Static framework and user-defined types now follow normal .NET storage rules during binding. Raven reports `RAV2810` when a static type is used for a local, field, property, indexer, or parameter type instead of silently accepting declarations such as `val file: File`.

Impact:
- Deconstruction code can now keep the matched value in scope while still filtering on that same value, instead of forcing users to choose between pattern-only matching (`> 100`) and a named binding (`amount`).
- Result-style matches can now use payload deconstruction directly inside a case arm without losing redundant-catch-all warnings or adding placeholder fallback arms.
- Editor coloring for generic variance, conversion operators, and `default` literals is now more consistent across themes that did not visibly style the generic operator-word scope.
- Static types now behave more like they do in C# at declaration sites, so invalid storage declarations fail early with a targeted diagnostic instead of surfacing later binder or emit noise.

## 2026-03-20

### Added
- Raven now supports F#-style scoped pinning through `use ptr = fixed &expr` in unsafe contexts. The `fixed` initializer yields a native pointer, requires explicit address-taking with `&`, and releases the pin automatically when the `use` scope exits.
- `use` declarations now also support an explicit nested-scope form, `use value = expr in { ... }`, which is equivalent to a nested block starting with the `use` declaration and avoids ambiguity with object initializer braces.
- The macro spec and focused tests now explicitly define how attached declaration macros compose when multiple macros target the same declaration and when both a parent declaration and its members use macros.
- Collection comprehensions now accept pattern targets, including deconstruction patterns, so forms like `[for val (key, value) in pairs => ...]` and `[for val (2, name) in people => name]` behave consistently with `for` statements.

### Changed
- Attached declaration macros are now documented as a source-ordered same-target pipeline: each macro sees both the original authored declaration and the current pre-application declaration, replacement results feed later macros on that declaration, introduced members are integrated first, the last replacement wins for the declaration itself, and peer declarations are integrated afterward.
- Result propagation lowering and block-expression codegen are now more robust in composed expression contexts. Propagated expressions used inside invocation/object-creation arguments are lowered through temporaries before emission, nested propagate nodes are rewritten consistently, exception-to-error rewriting only synthesizes a catch path when an actual `Exception` can convert into the enclosing error payload, and discard-context block expressions no longer leak `Unit` values onto the evaluation stack.

Impact:
- Managed storage can now be pinned without introducing a separate C#-style `fixed (...) { ... }` statement, so pinning composes with Raven’s existing `use` lifetime model and keeps address selection explicit.
- Resource lifetimes can now be narrowed inline without relying on extra surrounding braces, while object-initializer forms such as `use obj = Foo { Value = 2 } in { ... }` remain syntactically clear.
- Macro authors now have a stable, documented composition model to target, including explicit access to both authored syntax and composed same-target syntax, while IDE expansion views still show the full declaration result after all attached macros have run.
- Comprehensions can now reuse Raven’s existing pattern/deconstruction surface directly in collection-building code instead of forcing tuple/item access inside the selector.
- Raven code that combines `?` propagation with method arguments, generic calls, and lowered block expressions now emits valid IL and runs correctly instead of failing with `InvalidProgramException` or stack-shape bugs in mixed lowering/codegen paths.

## 2026-03-19

### Changed
- Nullable conditional member access now supports statement-form assignment. Raven accepts `x?.Name = value` and compound forms like `x?.Name += delta`, evaluates the receiver once, and skips the write when the receiver is `null`.
- Collection literals now have a clear split between general collection expressions and explicit arrays. Plain `[...]` remains the general collection form, defaulting to `ImmutableList<T>` in untyped contexts and `List<T>` when prefixed with `!`, while explicit arrays now use `[| ... |]`.
- Target typing still governs how `[...]` binds in typed contexts, so existing assignments such as `int[] = [1, 2, 3]`, `ImmutableArray<int> = [1, 2, 3]`, and `List<int> = [1, 2, 3]` continue to work without extra syntax.
- Collection expressions now also support dictionary-shaped literals. In addition to `key: value` entries, dictionary literals can now spread other dictionary-compatible sources with `...expr`, use single-entry spread syntax like `...key: value`, and build entries through dictionary comprehensions such as `[for item in items => item.Name: item.Value]`. Targetless forms follow the same immutable-by-default rule as list literals: bare forms infer `ImmutableDictionary<TKey, TValue>` and `!` forms infer `Dictionary<TKey, TValue>`.
- Pattern matching and deconstruction now support keyed dictionary forms. Raven can match dictionary-compatible values with patterns like `["a": val first, "b": 2]`, and declaration/assignment deconstruction now supports keyed extraction such as `val ["a": first, "b": second] = values`.
- Sequence-pattern slice captures now preserve concrete collection families when the scrutinee has one. Rest and fixed-segment captures over `List<T>`, `ImmutableList<T>`, and `ImmutableArray<T>` now bind back to those same collection types instead of degrading to `T[]`, while strings and arrays keep their existing slice behavior.
- Array support is now more stable across jagged and multidimensional CLR shapes. Jagged arrays continue to work through nested one-dimensional arrays, multidimensional array indexing/assignment now binds and emits correctly, and internal CLR type normalization no longer collapses multidimensional array metadata to `T[]`. Collection/array literal syntax remains intentionally single-dimensional, so explicit multidimensional array construction still goes through runtime APIs such as `System.Array.CreateInstance(...)`.
- Statement-form conditional pattern binding is now explicitly documented and test-covered for property patterns, so forms like `if val Person { Name: "Ada", Age: age } = value { ... }` are treated as part of the normal general-pattern surface rather than as an undocumented side effect of the shared binder path.

Impact:
- Raven code can now express common null-guarded property/field updates without spelling an explicit `if receiver != null` block, while compound assignments preserve the usual single-evaluation guarantee for the left-hand side.
- Raven local code now reads more consistently: `[...]` stays list-oriented unless target-typed otherwise, while `[| ... |]` carries explicit array intent through spreads and other composed expressions.
- Raven collection literals can now describe both list-like and dictionary-like construction without introducing a separate keyword or constructor-style syntax.
- Destructuring and pattern matching over immutable collections are now more predictable because captured slices keep the same collection semantics as the source value instead of silently changing APIs and mutability characteristics.
- Keyed lookup scenarios can now stay in Raven’s existing pattern/deconstruction syntax instead of dropping to manual `ContainsKey` / indexer code for dictionaries.
- Existing array code is more predictable: nested array literals keep working for jagged arrays, multidimensional interop no longer loses rank information in emitted metadata, and unsupported multidimensional literals now fail at analysis time instead of reaching broken codegen.

## 2026-03-18

### Added
- `SemanticModel.GetExpandedRoot()` and `Document.GetExpandedSyntaxRootAsync()` now expose an incremental expanded-document view that rewrites attached declaration macros and invocable macros into a single syntax root for tooling and debugging.
- Raven now supports a `fileprivate` modifier on type-like declarations. File-scoped declarations bind only within the declaring source file, file-scoped partial types must stay in one file, and emitted type/container metadata names are mangled so file-local helpers do not publish a stable CLR-facing name.

### Changed
- `rvn` now supports `--dump-macros [original|expanded|both][:plain|pretty[:no-diagnostics]]` so a single-file compile can show the pre-expansion source beside the currently expanded macro view, either as raw text or highlighted output.
- `.debug` compiler captures now also include per-document macro original/expanded source snapshots, including a plain text highlighted dump for the expanded view.
- Macro language-service support now treats macro names as first-class completion sites: `#[...]` offers attached macro names, `name!(...)` offers invocable macro names before the call is complete, and macro hovers include kind/target/argument hints alongside the existing expansion preview.

Impact:
- Macro debugging from the CLI no longer requires manually inspecting per-node expansion results just to compare authored source with the compiler’s current expansion output.
- Tooling and tests can request one expanded syntax root directly instead of reconstructing document-level macro output ad hoc.
- The ReactiveMacros-style editing loop is more discoverable because authors now get completion at the macro invocation site and immediate hover guidance about what a macro applies to before expanding it.

## 2026-03-17

### Changed
- Partial nominal types now behave consistently across classes, structs, records, and interfaces. Matching partial declarations merge into one type symbol, interface parts can contribute members across files, and conflicting accessibility/type-parameter shapes now report dedicated diagnostics instead of silently taking whichever declaration bound first.
- Partial methods, partial properties, and partial events are now supported inside partial nominal types. Raven accepts declaration/implementation pairs, merges them into a single symbol, and reports dedicated diagnostics when either side of the pair is missing or when a property/event implementation is left as auto/field-like syntax.

Impact:
- Multi-file type organization is now more predictable because partial-type compatibility is checked explicitly instead of depending on declaration order.
- Library authors can now split method/property/event contracts from their implementations in the same way they already split types, while still getting clear compiler feedback when a partial-member pair is incomplete.

## 2026-03-16

### Changed
- Collection expressions now reserve `...` for general spread segments and treat bare range elements such as `[1..3]`, `[1, 3..4, 9]`, and `[1..<4]` as inline sequence expansion. Constant-bounds range elements also participate in fixed-length array inference for targetless literals, including constant endpoints like `const MAX_VALUE = 10; [3..MAX_VALUE]`. This also fixes exclusive upper-bound handling for range-backed collection comprehensions so `..<` stops before the upper endpoint consistently.
- Raven now supports single-dimensional fixed-length array types written as `T[N]`. The compiler tracks the declared length on array symbols, preserves it through emitted `System.Runtime.CompilerServices.FixedLengthArrayAttribute` metadata, allows implicit conversion from `T[N]` to open `T[]`, and uses the fixed length during sequence-pattern/deconstruction analysis.
- Plain local collection literals now infer fixed-length arrays when the total length is statically known. That includes fixed-length array spreads, so expressions like `[...a, 3]` infer a fixed-length result when `a` is `T[N]`, while spreads from open arrays and comprehensions still infer open arrays.
- Fixed-length-array assignment/conversion failures now report size-aware diagnostics for open-array-to-fixed-array and mismatched fixed-length assignments instead of falling back to generic conversion errors.
- Sequence patterns now accept bare `...` as a non-capturing rest segment, so forms like `[first, ...]` and `[first, ..., last]` ignore the unmatched slice without introducing a binding. Captured rest segments like `...rest` may likewise appear in the middle or at the end of the pattern.
- Sequence-pattern captures over fixed-length arrays now preserve inferred segment sizes when the width is statically known. For example, deconstructing `int[4]` with `[a, b, ...rest]` binds `rest` as `int[2]`, and `[..2 head, tail]` over `int[3]` binds `head` as `int[2]`.

Impact:
- Raven now preserves obvious fixed array lengths without forcing annotations in local collection-expression code, while still keeping inference conservative in cases such as comprehensions and open-array spreads where the compiler does not yet model a statically known length.
- Raven now supports postfix nullable suppression via `expr!` as a narrow interop-oriented escape hatch. The parser models it as `SuppressNullableWarningExpression`, nullable references narrow to their underlying non-nullable type without changing runtime codegen, and nullable value types reuse the existing unwrap path. Using `!` now reports warning `RAV0403`, and this also fixes false `RAV0162` unreachable-code warnings on forms like `return value!`.
- Added statement-form conditional pattern binding via `if val pattern = expr { ... }` / `if var pattern = expr { ... }`. The compiler lowers this through the existing pattern-matching machinery, and the dedicated syntax node for the form is now `IfPatternStatement`.
- Statement-form conditional pattern binding now supports typed implicit captures under the outer binding keyword, so forms like `if val x: int = input { ... }` narrow nullable values and bind `x` without requiring an inner `val x: int`.
- Nominal `Type(...)` patterns now work for deconstructable primary-constructor classes and structs in addition to records. Public promoted `val` / `var` parameters synthesize a `Deconstruct` method in declaration order, so class patterns like `if val Person(1, name, _) = person { ... }` bind and type-check the same way as record patterns.
- `for` loop headers now accept an optional outer binding keyword before the iteration target. Forms like `for val item in items { ... }` and `for val Person(1, name, _) in persons { ... }` are supported, and for pattern targets the outer binding keyword supplies the binding mode for otherwise bare captures using the same shorthand rule as deconstruction assignment.
- `match` arms now accept an optional outer binding keyword before the arm pattern. Forms like `val [first, second, ...rest] => ...` and `val Some((x, y)) => ...` are supported, and the outer keyword supplies the binding mode for otherwise bare captures in the arm pattern.
- Structural patterns now support trailing whole-pattern designations consistently across `if val pattern = expr`, `for val pattern in values`, and match arms. Forms like `if val (2, > 0.5) point = input`, `for val Person(1, name, _) person in persons`, and `val Some((x, y)) pair => ...` bind the full matched value when the pattern succeeds.
- Explicit pattern comparisons now use a single comparison-pattern family across `==`, `!=`, `<`, `<=`, `>`, and `>=`. The parser no longer produces a separate explicit-value-pattern syntax node for `== expr`; compiler APIs now expose `ComparisonPatternSyntax` / `BoundComparisonPattern` / `IComparisonPatternOperation` consistently for all operator-led pattern comparisons.
- Comparison and range patterns now require the operand/bound type to match the scrutinee type after plain-type unwrapping. Raven no longer applies ordinary implicit numeric widening inside patterns, so forms like matching an `int` against `> 0.5` now report `RAV1606` instead of silently converting the operand.
- Record-pattern diagnostics now describe the real requirement: the nominal type must support deconstruction, not merely carry the `record` modifier.
- `RAV2704` now suggests the concrete `Task<...>` wrapper Raven expects when an `async` method, property getter, or function expression is annotated with a non-task return type, and async lambdas with that error now suppress the confusing follow-on body conversion diagnostic that previously obscured the root cause.

Impact:
- Swift-style conditional binding can now be written directly in statement form without introducing a separate `is` condition by hand, while still reusing Raven’s existing pattern scoping, shadowing, and flow analysis rules.
- Primary-constructor nominal types participate more naturally in positional matching and deconstruction-based APIs because the compiler now supplies a consistent `Deconstruct` surface for their promoted public state.

## 2026-03-15

### Changed
- `for` loop headers now accept pattern targets in addition to simple identifiers, so forms like `for (val x, 0) in points { ... }` and `for [val head, ..val tail] in values { ... }` lower to per-element pattern guards instead of requiring a manual `if value is ...` inside the loop body.
- Removed the legacy `for each` / `await for each` syntax. Raven now uses `for` and `await for` exclusively, with `_` or an omitted target for element-discarding loops.
- Macro plugins can now report macro-specific validation diagnostics with custom messages and optional argument locations through `MacroExpansionDiagnostic` plus helper methods on macro contexts, without having to manufacture raw compiler `DiagnosticDescriptor` instances.
- The existing `RAV9012` nullable-type guidance now offers a scoped `"Rewrite nullable flow to Option pattern matching"` code fix for simple local flows, rewriting a nullable local plus its immediately following `if x != null` / `if x is not null` branch into an `Option<T>` local and `Some(...)` pattern check when all uses stay inside that guarded flow.
- Style-only source-shape rewrites now use the new context-driven refactoring pipeline instead of built-in analyzer diagnostics. Target-typed union-case rewrites, expression-body/block-body conversions, redundant accessor removal, and string-concatenation rewrites now surface as on-demand editor suggestions without occupying the diagnostics list.
- Added a separate `"Convert if/else to match"` refactoring for pattern-based `if` statements, so control-flow shape changes are independent from the nullable-to-`Option` migration.
- `"Convert if/else to match"` now preserves common complementary union cases when rewriting pattern checks, so `Some(...)` rewrites pair with `None` and `Ok(...)` rewrites pair with `Error` instead of falling back to `_`.
- Raven code actions now expose preview entries that open a before/after diff for both diagnostic-backed fixes and context-driven refactorings, using the same general preview model instead of feature-specific expansion viewers.
- Signature help now behaves more like C#: partial invocations no longer crash extension-method pre-inference, and the language server gathers overloads from the underlying method group so `Foo(` can continue showing the full overload list instead of collapsing to only the currently selected candidate.

Impact:
- Collection iteration can now express filtering and deconstruction directly in the loop header, and the published grammar/editor tooling no longer advertises the retired `each` keyword.
- Macro authors can surface input-validation errors at the macro or argument site using a stable compiler-owned diagnostic path (`RAVM021`) while still keeping existing raw diagnostic emission available for advanced cases.
- Nullable-to-option guidance can now upgrade straightforward user-authored null-guarded locals into idiomatic `Option<T>` flow without crossing broader API boundaries or forcing a separate control-flow shape rewrite.
- Built-in diagnostics are now more focused on policy and correctness guidance, while purely optional shape rewrites come from refactoring providers and no longer require suggestion-mode analyzers.
- Users can inspect the effect of a Raven fix/refactoring before applying it, which makes the new suggestion-only actions usable without having to trust the edit blindly.
- Overload help is now more stable while typing incomplete calls and more useful for overloaded APIs, because the editor keeps showing the full callable surface even after one overload becomes the current best match.

## 2026-03-13

### Changed
- Inline and freestanding positional/list/record/member patterns now require an explicit binding keyword (`val`, `var`, or `let`) to capture variables; bare identifiers in those pattern positions are interpreted as existing-value matches instead. Assignment/declaration deconstruction shorthand such as `(a, b) = expr`, `val (a, b) = expr`, `[a, b] = expr`, and `val [a, b] = expr` is unchanged, and inline collection rest captures now use forms like `..val rest`.
- Collection patterns and collection deconstruction now support fixed-size sequence segments with operator-first syntax such as `[..2 val start, val end]`, alongside `..val rest` / `...val rest`. Strings participate in the same model: single-element captures bind `char`, while fixed/rest segment captures bind `string`.

- Added Roslyn-style syntax formatting hooks: `Formatter.Annotation`,
  `SyntaxAnnotation.ElasticAnnotation`, and elastic trivia helpers on
  `SyntaxFactory`, with `SyntaxNormalizer` updated to honor formatter
  annotations and elastic whitespace.
- Clarified the syntax API docs to state that `SyntaxFactory` creates raw
  structured nodes that callers must format or attach trivia to explicitly.

### Added
- Added initial macro-system scaffolding: `#[MacroName]` syntax is now recognized as a distinct macro-style annotation surface, and public .NET plugin contracts were introduced under `Raven.CodeAnalysis.Macros`.
- Added targeted parser/semantic tests for macro-style attributes and plugin reference discovery.
- Added a sample project layout under `samples/projects` showing the intended `AddEquatable` Raven source and companion .NET macro plugin shape.
- Added project-system/compiler support for `RavenMacro` assembly references plus initial macro diagnostics for unknown/duplicate/invalid attached macros and plugin load failures.
- Added generic attached-macro expansion invocation and caching on `SemanticModel`, including plugin diagnostics and expansion-failure diagnostics, so tooling can inspect expansion results without compiler-side macro synthesis.
- Added optional replacement-declaration support to `MacroExpansionResult` so attached macros can move beyond additive member generation toward property/declaration rewriting scenarios.

### Changed
- Fixed generated TargetFramework handling so SDK-style projects with an explicit top-level `func Main() -> unit` no longer synthesize a competing entry point from the generated framework-attribute document.
- Fixed value-type indexer call emission so Raven-authored macro plugins can safely access struct-backed syntax collections without generating invalid IL.
- `use` declarations in async contexts now prefer `IAsyncDisposable.DisposeAsync()` when available and fall back to `IDisposable.Dispose()` otherwise, while keeping sync contexts on ordinary `Dispose()`.
- Attached macro replacement/introduction now participates in semantic declaration binding for type members, so replacement properties and generated members show up through declared-symbol lookup instead of remaining expansion-only metadata.
- Attached macro-generated syntax now participates in emit as well as semantic binding, so introduced methods and replacement properties change the generated IL instead of remaining tooling-only expansions.
- MSBuild `RavenMacro` items can now point at Raven macro projects directly, and the project system will build/load the current plugin assembly instead of silently using a stale checked binary.
- Added an initial macro-expansion editor experience: hovering a macro shows an expansion preview, and VS Code now offers a `Show macro expansion` code action that opens the rendered expansion in a preview editor.
- Fixed the Raven-authored `#[Observable]` sample macro to use the property type itself instead of the full type-annotation clause, so the sample now produces a real replacement setter and raises `PropertyChanged` as intended.
- Macro attributes now use `#[...]` instead of escaped attribute identifiers, `#` only tokenizes that way when immediately followed by `[`, and the VS Code grammar now highlights macro attributes separately from ordinary attributes.
- Macro project loading is now deterministic across target frameworks and dependencies: Raven-authored macro projects emit under framework-specific output folders, rebuild inputs include referenced project outputs, and macro load contexts no longer reuse arbitrary same-name process assemblies.
- Metadata methods with unreadable signatures no longer collapse to arity-zero methods during symbol loading; the compiler now preserves them as invalid signatures instead of silently rebinding them as parameterless APIs.
- Attached macro plugins now receive both the raw parsed argument list through `AttachedMacroContext.ArgumentList` and a convenience parsed view through `AttachedMacroContext.Arguments`, where each `MacroArgument` exposes both a richer constant representation and a direct CLR `Value`.
- Added `IMacroDefinition<TParameters>` as the public marker for the typed macro-parameter-object direction, so attached macros can move toward attribute-like argument binding and editor experience without changing invocation syntax again.
- Added `IAttachedDeclarationMacro<TParameters>` and the first compiler-bound typed-parameter path for attached macros: positional arguments bind through a single public constructor, named arguments bind through writable properties, and invalid names/conversions now report dedicated macro diagnostics before expansion.
- Added invocable macros with `name!(...)` syntax, typed parameter binding, semantic-model expansion lookup, and initial language-server preview/definition support.
- Macro argument constant values are now evaluated without re-entering semantic diagnostics during expansion, so macros can read literal argument values without recursively re-triggering their own expansion and blowing the stack.
- Accessor parsing and formatting now preserve explicit same-line `;` separators, and `SyntaxNormalizer` inserts line breaks between adjacent accessors and block statements when raw generated syntax omits trivia, so macro expansion previews stay readable without requiring macros to hand-format every token.
- `SyntaxFactory.ArrowExpressionClause(...)` now defaults to the fat arrow token `=>` at the syntax-model level, so generated accessor and member expression bodies no longer drift back to pointer-style `->` after regeneration.
- `SyntaxFactory` token convenience members now return fresh token instances instead of reusing shared singleton tokens, so Raven-authored macros can safely use helpers like `CommaToken` and `SetKeyword` multiple times while building detached syntax trees.
- Statement factory convenience overloads now default `TerminatorToken` to `SyntaxKind.None`, matching the parser’s newline-as-trivia model so raw `SyntaxFactory` statements no longer synthesize newline terminator tokens.
- `SyntaxFactory` convenience overloads can now be defined explicitly in `Syntax/Factories.xml`, and the node generator validates those overload definitions against the syntax model so invalid slot mappings and hazardous combinations like non-null `Body` plus `ExpressionBody` are rejected during generation.
- Nodes with explicit `Syntax/Factories.xml` definitions now expose only those validated red `SyntaxFactory` overloads, instead of also publishing a raw full-slot overload that could bypass invariants such as `AccessorList` plus `ExpressionBody` on the same declaration.
- Explicit syntax-factory overloads can now declare carefully-chosen aliases such as `StoredPropertyDeclaration`, with generated XML docs that make clear the alias is only a descriptive wrapper over the canonical factory shape.
- `Raven.CodeAnalysis` now emits XML documentation files, and PE symbol documentation lookup correctly resolves sidecar XML member IDs for generic parameter types, so Raven code can consume generated `SyntaxFactory` documentation from metadata references.
- Metadata documentation lookup now supports assembly-adjacent Markdown sidecars (`<AssemblyName>.docs/manifest.json` + symbol files), prefers Markdown over XML when both exist, and uses hashed XML-doc-ID filenames to keep metadata doc paths stable and filesystem-safe.
- Hover and signature help now render XML documentation comments into readable Markdown sections instead of showing raw XML fragments, so metadata docs from XML sidecars display cleanly in the editor.
- Markdown documentation comments now support structured `.NET`-style block tags such as `@param`, `@typeparam`, `@returns`, and `@remarks`, and the shared documentation formatter renders those tags into clean hover/signature-help sections instead of showing the raw tag lines.
- Added a sibling-project `markdown-docs` sample that exercises Markdown documentation, structured tags, `xref:` links, and XML/Markdown sidecar emission across a library and consumer project.
- Hover and signature help now rewrite documentation `xref:` links into actionable editor commands that open Raven symbol documentation pages, instead of degrading those references to plain display text.
- Markdown sidecar files may now carry optional top-of-file front matter such as `xref: ...`; that metadata is stripped before rendering and used only to bind/validate the document against a specific symbol.
- Markdown documentation structure extraction is now exposed through a shared API, and XML emission reuses that extracted summary/parameter/returns/remarks shape instead of flattening Markdown comments into a single raw `<summary>` blob.
- Documentation extraction is now centered on a format-neutral Raven documentation structure, so both Markdown and XML comments project into the same intermediate model before being rendered or emitted.
- Project builds now require an explicit `GenerateXmlDocumentationFromMarkdownComments` opt-in before Markdown-authored comments are projected into emitted XML documentation; XML-authored comments continue to emit normally without that flag.
- Recognized Markdown documentation headings such as `### Remarks` now flow through the shared documentation structure instead of being rendered once as raw body text and again as a structured section, so hover/signature-help output no longer duplicates those sections.
- Delegate parameter inference is now covered for both direct metadata-delegate assignment and `PropertyChanged += (sender, args) => ...` event subscriptions, including the observable sample shape.
- The `macro-observable` sample now uses inferred lambda parameter types for its `PropertyChanged` handler, matching ordinary delegate assignment behavior.
- Lambda parameter declarations in target-typed function expressions now resolve through the same contextual semantic binding as identifiers inside the body, and compound assignment statements now surface stable assignment operations instead of crashing operation traversal.
- The language server now keeps project-backed documents stable across multi-project workspaces: sibling-project files can be resolved by URI on demand, and closing an open project document no longer removes it from the underlying workspace project graph.
- Language-server diagnostics now match source-backed compiler diagnostics by file path instead of requiring the exact same syntax-tree instance, so compiler `Info`/hint diagnostics keep showing up for open documents instead of only analyzer suggestions surviving the filter.
- Semantic diagnostics no longer crash on malformed invocations inside match arms; argument binding now tolerates missing argument nodes and continues reporting parser/binder diagnostics.
- Top-level and namespace parsing now correctly distinguishes sequence-pattern assignment statements from attribute/declaration preludes, so `[val first, val second] = values` no longer gets misparsed as a broken attribute list.
- Hover resolution inside lambda bodies is now more robust: member-name tokens are resolved before enclosing-block locals can hijack them, and lambda pattern locals no longer get misidentified as plain parameters.
- Attached macros can now return syntax built directly with `SyntaxFactory` without needing synthetic source rooting first; replacement members are contextualized against the real containing declaration before binding/emit, and detached generated syntax no longer crashes source symbol or method-body emission paths.
- Project-reference compilations now force source declaration symbols for referenced Raven projects before they are exposed as `CompilationReference`s, so sibling-project source types participate in name binding and editor navigation instead of degrading to `Error` across workspace boundaries.
- `Go to definition` now resolves `#[MacroName]` sites back to the macro declaration project when the macro project is open in the workspace, using the macro reference’s source project path to map the loaded macro type back to source.
- `Go to definition` and expansion preview now also work for invocable macro invocations such as `answer!()`.
- Fixed `SeekableTextSource.PeekChar(offset, ...)` so offset-aware peeks actually honor the requested offset; this was required to keep `#pragma` on the directive path while adding invocable `name!(...)` parsing.

Impact:
- Raven now has a stable syntax and host API foundation for attached macros without routing them through the normal CLR attribute pipeline.
- Plugin authors have a concrete contract to target, Raven projects can point at macro plugin assemblies, and the compiler can now execute attached macros generically while keeping generated-member semantics out of the compiler for now.
- Raven-authored macro plugins now load cleanly even when they index into value-type syntax collections, and SDK-style executable projects no longer hit spurious entry-point ambiguity from generated framework metadata.
- Macro-driven member replacement is now visible to semantic tooling, and the editor can surface the generated expansion without requiring a debugger or ad hoc compiler logging.
- The Raven-authored observable sample now exercises a real end-to-end replacement macro path instead of silently falling back to the source auto-property.
- Multi-target workspaces can now reference the same Raven-authored macro project without reusing the wrong plugin binary, and metadata probing no longer risks rebinding unreadable APIs as parameterless methods.
- Attached macros can now safely inspect literal argument constants during expansion, and the observable sample no longer appears to hang when the plugin reads `context.Arguments[0].Constant`.
- The macro contract now has an explicit typed-parameter direction, aligning future completion/signature help and argument diagnostics with the way normal attributes are presented in the IDE.
- Raw `SyntaxFactory`-built macro expansions now display with sensible accessor and statement layout in the editor even when the macro only supplies structural terminators instead of fully formatted trivia.
- Macro expansion hover/code-action previews are stable again after syntax regeneration, because detached `SyntaxFactory` expression bodies now render with `=>` consistently.
- Macro expansion hover/code-action previews no longer disappear when a macro builds syntax from repeated `SyntaxFactory` token helpers, because formatter rewrites now see distinct token identities instead of duplicate singleton token objects.
- `SyntaxFactory` statement builders now produce structurally terminated statements by default, keeping the API focused on syntax structure while leaving indentation and spacing to normal formatting.
- Public syntax-factory API shape is no longer forced to follow slot heuristics alone; explicit factory definitions now let Raven control convenience overloads separately from raw tree structure while keeping the generated API validated against the underlying slots.
- Red `SyntaxFactory` now trends toward valid-by-construction APIs for nodes with explicit factory definitions, while low-level tests can still use node constructors when they intentionally need malformed or manually-tokenized syntax.
- Raven-authored tools and macro projects can now surface XML documentation from referenced `Raven.CodeAnalysis` APIs such as `SyntaxFactory` aliases instead of seeing empty metadata docs.
- Raven-authored tools and future RavenDoc output can target one shared metadata documentation convention, with Markdown sidecars taking precedence while preserving XML fallback for ordinary .NET libraries.
- Cross-project workspace navigation is now reliable for both normal Raven project references and open Raven macro projects, so definition requests no longer fall back to same-file error locals or stay stuck on the `#[]` use site.
- Delegate inference behavior around event subscriptions is now locked by focused tests, and the observable sample demonstrates the inferred-parameter form directly.
- Hover/symbol lookup for inferred lambda parameters is now consistent with the compiler’s actual binding, and operation-based tooling no longer trips over `+=` statements while walking child operations.
- Hover/code-action requests for files in referenced sibling projects no longer lose their semantic model because the LSP workspace was deleting real project documents on close or relying solely on transient open-document ownership.
- Open-document diagnostics in the editor are now resilient to equivalent syntax-tree instances, which fixes missing compiler hints/information diagnostics in the normal LSP publish path.
- Broken source inside a match arm now degrades to diagnostics instead of throwing a null-reference exception during semantic-model construction.
- Sequence-pattern assignment now binds from the correct syntax shape at top level and inside namespaces, which restores parser/semantic coverage for destructuring assignment scenarios.
- Hover over member-access names and lambda pattern locals is now less sensitive to stale or over-broad fallback resolution, reducing false symbol results in the language server.
- Raven-authored macros can now construct generated declarations structurally and preserve reused source syntax such as property initializers, instead of having to round-trip through parsed helper strings or synthetic wrapper trees.

## 2026-03-12

### Added
- Expanded Operations API coverage for newer language constructs and bound nodes.
- Added targeted sample coverage around generic parsing with static interface constraints.
- Added style analyzer + code fix to convert expression-bodied members to block-bodied form.
- Added an MSBuild-backed Raven project-system service so workspaces can open SDK-style project files with `RavenCompile` items and traverse `ProjectReference` through the project-system abstraction.

Impact:
- Compiler API consumers can inspect more semantics directly.
- Regressions in generic-constraint scenarios are easier to catch with samples.
- Raven workspace consumers are no longer limited to the custom `.ravenproj` file format.

### Changed
- Null-assignment diagnostics were tightened and message quality improved (clearer assignment errors and hint formatting).
- Static interface member resolution and generic constraint checks were corrected for `IParsable<T>`-style flows.
- Cascade behavior after failed generic binding was reduced to avoid misleading downstream errors.
- Generic method calls with explicit type arguments now follow C# more closely by skipping extra method-type inference passes for later lambda arguments, and overload reporting suppresses more downstream cascades when an argument already carries an error type.
- Several binder/codegen regression fixes landed (including interpolation/object-dumper/runtime sample paths).
- Hover/signature display for promoted primary-constructor parameters now preserves binding keyword semantics (`val`/`var`) when the parameter maps to a property.
- Compiler projects were retargeted from `net10.0` to `net10.0` (including build scripts/default framework switches).
- The primary Raven CLI command name is now `rvn`, and project scaffolding/help now advertise SDK-style `.rvnproj` files.

Impact:
- Fewer false diagnostics and better first-error quality.
- Fewer compile-success/runtime-fail scenarios in generic and interpolation-heavy code.

### Removed
- Removed stale/incorrect operation naming in favor of updated terminology alignment (for example, moving from switch-centric naming toward match-centric naming where applicable).

Impact:
- Operations API is more consistent with current language semantics.

---

## 2026-03 (early to mid)

### Added
- Added destructuring and pattern expressiveness upgrades: nested patterns, explicit value patterns, sequence deconstruction support across more shapes.
- Added collection builder support and spread/target-type inference improvements.
- Added analyzers/code fixes for expression-body preferences and diagnostic suppression directives.

Impact:
- Pattern-based code became more expressive and concise.
- Collection inference became more predictable in real-world generic code.

### Changed
- Function syntax direction shifted toward first-class function expressions and updated signature/hint presentation.
- Parameter deconstruction support expanded (including lambda parameter deconstruction).
- Parser hardening for argument lists and continuation/newline-sensitive forms.

Impact:
- Improved ergonomics for functional style and lambda-heavy APIs.
- Reduced parser drift on edge-case call syntaxes.

### Removed
- Removed residual syntax/display traces that no longer match current function and parameter terminology.

Impact:
- Tooling output better matches current language surface.

---

## 2026-02

### Added
- Added/expanded language server capabilities: hover docs, completions, signature help, code actions, symbol outline, logging hooks.
- Added project-system and runtime integration work: framework references, NuGet support, output layout improvements, .editorconfig participation.
- Added async runtime support/stabilization work (including runtime async and ValueTask-oriented paths).
- Added richer pattern and control-flow support: range patterns, guarded matching improvements, return-expression and throw-expression support.
- Added OOP surface enhancements: abstract classes, interface support maturation, property/accessor and constructor-related semantics.

Impact:
- Authoring/debugging experience improved materially in editor workflows.
- More practical .NET integration for non-trivial Raven projects.
- Broader set of control-flow/pattern constructs compile and run reliably.

### Changed
- Match semantics and exhaustiveness checks were repeatedly hardened (including diagnostics and generic display improvements).
- Async lowering behavior was stabilized across edge cases (implicit return interactions, try/catch flows, lambda paths).
- Accessibility/default-member behavior and declaration rules evolved, with related diagnostic updates.

Impact:
- Fewer runtime surprises in async/match heavy code.
- Stricter, clearer declaration behavior for class members and access control.

### Removed
- Removed type unions and type literals from active language surface (and associated normalization/parsing paths).
- Removed named constructors feature.
- Removed legacy `Try*` LINQ extension route.

Impact:
- Breaking change for code depending on union/literal type syntax.
- Language surface became narrower and easier to stabilize.

---

## 2025-09 (from 2025-09-12 onward)

### Added
- Added generics foundation and constraints across types/methods.
- Added interface declarations and base-list support for classes/interfaces.
- Added extension-method consumption and lowering support (including staged parity improvements).
- Added Operations API initial infrastructure.
- Added attribute support across assembly/type/member/parameter/return contexts.
- Added control-flow/codegen support for break/continue, goto/labels, and more lowering targets.
- Added CLI and diagnostics tooling improvements (`-bt`, diagnostics-only highlighting, source-symbol/bound dumps).

Impact:
- Major expansion in language expressiveness and tooling introspection.
- Better parity with .NET expectations for attributes, interfaces, and generic constraints.

### Changed
- Overload resolution and conversion logic was hardened (nullable/lambda/extension interactions, byref matching, generic substitution paths).
- Match lowering and diagnostics were corrected for null/literal/value-type cases and exhaustiveness flows.
- Parser robustness improved for rewinds, continuations, skipped tokens, and missing-terminator recovery.

Impact:
- More deterministic binding decisions.
- Better diagnostic precision and fewer parser-induced semantic cascades.

### Removed
- Removed or phased out unstable/unsupported intermediate behavior around extension and union-related paths as the model converged.

Impact:
- Some experimental edge behavior no longer compiles; diagnostics are now more explicit.

---

## Migration Notes

- If old code assigns `null` to non-nullable types, migrate to nullable/optional forms.
- If old code uses union/type-literal syntax, migrate to current Raven constructs.
- Re-check overload-heavy calls (especially lambdas/extensions/generics) because binder behavior is now stricter and more correct.
- For compiler API integrations, prefer current Operations API names/shapes aligned to match-oriented semantics.
- Changed: invocation arguments for `ref`, `out`, and `in` parameters now use explicit call-site keywords instead of `&` at ordinary call sites. Raven now supports `Set(ref value)`, `TryParse(text, out result)`, and declaration forms like `TryParse(text, out var result)` and `TryParse(text, out val result)`.
- Added a sibling-project `samples/projects/macro-invocable` sample showing a Raven-authored invocable macro plugin and executable app project using `add!(...)`.
- Added a sibling-project `samples/projects/macro-reactive` sample showing an attached property macro and an invocable subscription macro working together in Raven-authored projects.
- Changed the VS Code extension defaults to disable color decorators in Raven files so invocable macros like `add!(...)` do not trigger hex-color pickers.
- Changed macro contracts so `MacroKind` is inferred from `IAttachedDeclarationMacro` and `IInvocableMacro`, removing redundant boilerplate from implementations.
- Changed `macro-reactive` to use `System.Reactive` and `IObservable<T>`/`Subject<T>` in the sample runtime shape instead of a custom in-sample observable type.
- Fixed sequence-point emission for macro-generated zero-width spans so generic introduced-member initializers no longer crash emit.
- Changed: compiler-emitted documentation now writes symbol-addressable outputs.
  Markdown uses assembly-adjacent `.docs/` sidecars with an `invariant/`
  locale root, and XML uses standard `<doc><members>` symbol IDs instead of the
  old file/line dump format. This aligns emitted docs with metadata lookup in
  the IDE/compiler and leaves room for RavenDoc/localization integration later.
- Changed: Raven's workspace/MSBuild project model now preserves
  `GenerateDocumentationFile`, `GenerateMarkdownDocumentationFile`,
  `DocumentationFile`, and `MarkdownDocumentationOutputPath` on open/save so
  documentation emission settings round-trip cleanly through project editing.
## Unreleased

### Added
- Added a separate context-driven code refactoring provider pipeline so editor suggestions can appear without requiring a backing diagnostic. The workspace and language server now surface diagnostic-backed quick fixes and diagnostic-free refactorings as distinct code action sources.
- RavenDoc now publishes Raven API-reference sites from `.rvnproj` projects,
  individual source files, source directories, or compiled libraries with
  adjacent Markdown `.docs` sidecars. Its built-in responsive presentation now
  uses Raven-specific navigation, branding, light/dark styling, and offline
  syntax highlighting for fenced Raven code.
- RavenDoc and the browser Playground now share a consolidated Raven visual
  foundation across light and dark modes, including brand, color, typography,
  surface, and code-presentation tokens. The Playground's Monaco editor follows
  the active system color scheme with matching Raven syntax themes, and its
  persisted theme selector can explicitly choose System, Light, or Dark.

### Changed
- Removed the legacy `new Foo(...)` object-creation syntax. Raven object construction now uses direct type invocation (`Foo(...)`) consistently across parsing, samples, and tests.
- Top-level type declarations are now hoisted for binding, so console-app file-scope code can be interleaved with `class`, `struct`, `record`, `enum`, `union`, `interface`, and `delegate` declarations without triggering ordering diagnostics.
- Parenthesized unions now support nominal deconstruction patterns over their declared member types, so matches like `Cash(val amount)` and `Card(val reference)` lower through the same `TryGetValue` carrier extraction path as `Cash cash` and `Card card`.
- Function expressions can now be iterator generators both with declared iterator return types and with inferred iterator return types. Raven now lowers `yield` inside lambda/function-expression bodies to the same synthesized iterator state machines used for ordinary functions, including `IEnumerable<T>` and `IAsyncEnumerable<T>` shapes.

Impact:
- Higher-order Raven APIs can now keep generator logic inline in function expressions instead of forcing local helper functions just to use `yield`.
