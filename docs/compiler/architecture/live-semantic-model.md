# Live semantic model

This document describes the target architecture for Raven's compiler, workspace,
and language server when code is being edited. It is the shared direction for
incremental compilation, binder-owned state, analyzer execution, and VS Code
responsiveness.

The goal is a Roslyn-like editor experience: foreground semantic requests should
stay responsive, diagnostics should be correct and eventually consistent, and
the language server should present compiler-owned answers rather than owning
semantic truth.

For VS Code this should feel like the C# extension: saving a document, adding a
source file, or deleting a source file should update the live project snapshot
without a full workspace reset when the affected project can be identified.
Broader project-system changes such as references, package restore inputs, and
target-framework changes may refresh more state, but should still invalidate an
affected project graph rather than rebuilding unrelated projects by default.

## Ownership

`Raven.CodeAnalysis` owns semantic truth.

- `Compilation` snapshots define the semantic generation for a project.
- `SemanticModel` answers public semantic API questions for a document snapshot.
- Binders are execution units. A method binder owns method parameters; a block
  binder owns immediate locals, statement and expression binding state, and
  binder-produced diagnostics.
- The workspace owns project/document snapshots, analyzer scheduling, and
  diagnostic publication coordination.
- The language server owns LSP scheduling, cancellation, and presentation only.

The language server may cache rendered hover text, inlay labels, completion
presentation, and semantic-token results per document version. It must not own
symbol identity, type inference, overload selection, diagnostic truth, or binder
invalidation policy.

That cache is allowed to preserve presentation while a newer snapshot is being
computed. Diagnostics and inlays may remain visible across unrelated edits when
their ranges can be translated from the previous source text to the current
source text. They must be dropped when the edit intersects the diagnostic or
hint range, because that is the point where the cached presentation may describe
text that no longer exists.

## Semantic Queries

Public semantic APIs should stay Roslyn-shaped:

- `SemanticModel.GetDeclaredSymbol`
- `SemanticModel.GetSymbolInfo`
- `SemanticModel.GetTypeInfo`
- `SemanticModel.GetOperation`
- diagnostics APIs

These APIs choose internally whether to answer from declaration state, transferred
incremental descriptors, binder-owned caches, targeted binding, or a complete
bind. Consumers should not need cache-specific helper APIs to get correct
answers.

Narrow semantic queries are not diagnostic-production APIs. They may bind the
state needed to answer the requested symbol, type, constant, or operation, but
they should not collect full binder diagnostics as a side effect. Full diagnostic
collection belongs to diagnostic APIs and the diagnostic lanes.

Hover presents nullability from `SemanticModel.GetTypeInfo` at the referenced
expression. It keeps the symbol's declared nullable annotation in the Raven
signature and, for nullable locals and parameters, adds the contextual flow
state (`maybe null` or `not null`) for that exact program point. The language
server does not reproduce branch analysis, and local-reference hover does not
take a syntax-only shortcut that could hide a compiler-owned flow change.

## Binding And Incremental State

Binders are cache-derived from syntax and semantic context. They can keep state
that they are responsible for because their lifetime is controlled by the
compilation/semantic-model layer.

Binder reuse is valid only when both the syntax and semantic context are still
equivalent:

- syntax identity or an accepted incremental match is preserved;
- parent binder and owner context are equivalent;
- containing member signatures, imports, options, and referenced assemblies
  still define the same semantic environment.

When a syntax owner changes, stale binders and their owned diagnostics should be
discarded. A new binder recreates symbols and diagnostics lazily as queries ask
for them.

The preferred query path is:

1. declaration tables and member-signature state;
2. transferred owner-relative semantic descriptors;
3. targeted binding of the relevant owner, block, statement, expression, or
   pattern;
4. complete binding only when the API explicitly requires full semantic state.

Hot incremental paths should operate on immutable `SourceText` spans instead of
rendering syntax nodes to temporary strings. When a derived string is genuinely
needed repeatedly, cache it at the immutable syntax, symbol, binder, or snapshot
boundary that owns its validity; do not add cache-specific responsibilities to
language-service callers.

## Diagnostics

Diagnostics are produced by distinct owners:

- syntax diagnostics are produced by the parser and syntax tree;
- compiler diagnostics are produced by binders and compiler validation;
- analyzer diagnostics are produced by analyzers through the workspace analyzer
  driver.

The language server publishes diagnostics through lanes:

- syntax diagnostics are fast and foreground-safe;
- document compiler diagnostics are background semantic work for the active
  document;
- project compiler diagnostics are broad compiler work;
- document/project analyzer diagnostics are broad analyzer work.

Diagnostic results must be tied to a document/project snapshot and editor
document version. Stale results are dropped. Skipped, canceled, or failed
background semantic/analyzer work must not publish an empty diagnostic set that
erases the last valid analyzer diagnostics. The previous result remains visible
until a newer successful lane result replaces it.

For active editor feedback, syntax diagnostics may publish before semantic
diagnostics. That syntax publish should merge with the last successful semantic
and analyzer lanes where possible instead of clearing them. The merge is
presentation-only: ranges are translated across non-intersecting edits, and any
diagnostic whose previous span intersects the edit is removed immediately.

The compiler diagnostic lane must also be deterministic for a single snapshot.
Diagnostic traversal may use incremental binders, transferred owner diagnostics,
or cached declaration state, but it must not derive diagnostics from an
incomplete owner context. Attribute binding is a representative case: an
attribute written on a type declaration must be validated against that declared
type even if the declaration also synthesizes members such as union helpers,
accessors, or `ToString`. If an incremental binder for the declaration is missing
or stale, the semantic model should recover the declaration owner from compiler
declaration state rather than falling back to the parent binder's containing
symbol. False diagnostics from an invalid cached owner are worse than delayed
diagnostics because they make the editor disagree with a one-shot compilation.

This mirrors the user experience expected from C# tooling: analyzer diagnostics
may arrive later than syntax/compiler diagnostics, but the Problems list should
not flicker or permanently lose diagnostics because a background pass was
canceled or preempted.

## Analyzer Execution

Analyzer execution should remain Roslyn-like:

- analyzers register syntax, symbol, operation, syntax-tree, or compilation actions;
- analyzer instances are stateless executors;
- the workspace/analyzer driver owns traversal, caching, cancellation, and
  invalidation;
- analyzers use public semantic APIs and `SymbolEqualityComparer.Default`;
- analyzer callbacks avoid broad diagnostic APIs.

A cold analysis may walk a document once and dispatch all applicable registered
actions. After an edit, the target shape is to rerun only actions whose syntax or
symbol scopes were invalidated. Broad analyzers such as unused-member or
unused-variable checks should be modeled with symbol/operation actions and
shared cores instead of forcing each callback to rescan a full document.

Analyzer diagnostics should be deterministic for the same snapshot. Concurrent
execution is allowed when an analyzer opts in and follows the stateless contract.
The driver merges results in deterministic order before publication.

Symbol-action analyzers may require a semantic model to enumerate the declared
symbols in a document. The workspace/analyzer driver materializes the applicable
symbol set first, releases semantic access, and then dispatches symbol analyzer
callbacks. This keeps foreground semantic requests from waiting behind
long-running symbol analyzers while preserving compiler-owned symbol identity
for the snapshot.

Operation-action analyzers require the operation tree for executable syntax. The
workspace/analyzer driver should create operation roots through the public
semantic model, traverse operations once per document snapshot, then dispatch
matching operation actions in deterministic order. This keeps returned-value,
assignment, member-reference, and invocation analyzers from each issuing their
own broad syntax walk plus `GetOperation` query.

Compilation, syntax-tree, and syntax-node analyzers may call public semantic APIs
from callbacks. The driver uses short callback-scoped semantic leases for those
actions, and busy background runs may skip/requeue when the semantic gate is
already held. Semantic access is reentrant within a semantic model so callbacks
can safely use public APIs while their callback-scoped lease is active. The
driver should avoid holding semantic access while it is only walking syntax,
because syntax traversal itself is not semantic truth.

Before source declarations are complete, semantic leases coordinate through a
compilation-owned initialization gate. This establishes one lock order for
cross-document declaration binding and attached or freestanding macro expansion.
A request cannot hold one document's semantic gate while another declaration
pass needs it. After declaration initialization, documents retain independent
semantic gates. Async callers establish ambient initialization ownership alongside
their semantic lease; background try-acquire paths remain non-blocking.

## Language Server Scheduling

Foreground requests are:

- hover;
- completion;
- signature help;
- definition;
- references and rename where applicable.

Foreground requests may preempt background semantic work and should not wait
behind diagnostics, analyzers, full-document inlays, or semantic tokens unless
they require the exact same compiler-owned state currently being produced.

Background requests are:

- compiler diagnostics;
- analyzer diagnostics;
- semantic tokens;
- broad/full-document inlay hints;
- project-wide symbol refreshes and outline refreshes.

Background work should use cancellable or try-acquire paths, verify snapshot
versions before publishing, and requeue skipped work without spinning. Semantic
tokens and broad inlays may degrade to syntax-only or cached presentation when
semantic access is busy. Hover and completion should always ask the compiler for
authoritative semantic answers.

Full-document inlay hints follow the same presentation rule as diagnostics.
Cached hints may be reused or translated for unchanged ranges while a debounced
or background inlay request waits for semantic access. Focused inlay requests for
the visible range or cursor-adjacent area may bind authoritatively and replace
the cached presentation sooner.

## Lookup Boundaries

Compiler internals should prefer context-aware lookup services instead of
walking the public merged `GlobalNamespace` view on hot paths.

The merged global namespace is primarily a presentation/API view. Internal
lookup should normally query source declarations and referenced metadata through
specialized helpers that understand precedence, imports, namespace-member
exposure, and metadata loading costs.

Source declarations normally take precedence over metadata declarations in the
same namespace and with the same identifier. Lookup services should make that
precedence explicit instead of relying on merged tree traversal.

## Current Gaps

The following areas still need cleanup before the editor loop is fully live and
cheap:

- Project compiler diagnostics still use broad compilation diagnostics. Keep
  that lane background-only and cancellable; foreground editor requests should
  not wait behind it.
- Document compiler diagnostics can still force declaration completion or a
  wider bind when owner state is missing. That is valid for correctness, but
  owner recovery should keep moving toward declaration-scoped binders and
  binder-owned diagnostics.
- Hover over metadata-heavy or generic symbols can still spend most of its time
  in direct invocation or generic symbol resolution. Headless runs should track
  these slow paths separately from correctness fixes so the compiler API remains
  complete while hot paths become cheaper.
- Watched-file handling should invalidate selectively. Saving an open source
  document must not reset the workspace because the text-document snapshot is
  already authoritative. Closed source-file changes and deletes should update
  known documents in the current solution without reopening projects. Source
  creates should re-evaluate only candidate project membership and add the
  document to the affected project when possible. Project files, project
  references, NuGet/package references, assembly references, `.editorconfig`,
  and unmatched source-file create events may require broader project-graph or
  option invalidation, but those paths should update the affected project state
  instead of rebuilding every semantic cache by default.
- Declaration-binder recovery is still explicit for several declaration syntax
  forms. Over time this should converge on one declaration-owner recovery path
  instead of per-declaration fallbacks.

## Validation

Important validation scenarios:

- one-shot compilation still binds and emits correctly;
- body-only edits preserve unrelated semantic state;
- wrapping top-level statements in `func Main` recovers deterministically;
- cross-file additions update the active project snapshot;
- lambda and extension-method chains bind in receiver order;
- analyzer diagnostics remain visible while newer analyzer work is pending;
- compiler diagnostics remain visible across unrelated edits while newer
  snapshot diagnostics are pending;
- diagnostics disappear immediately when the edit intersects the diagnostic
  source span;
- full-document inlays remain stable across unrelated edits while newer inlay
  results are pending;
- foreground hovers remain responsive while diagnostics/analyzers/semantic tokens
  are running;
- repeated hovers after warm-up reuse compiler-owned state.

Use headless language-server scenarios for editor workflows and focused compiler
tests for binder/semantic-model behavior. Runtime/sample validation remains the
authority for emitted program behavior.
