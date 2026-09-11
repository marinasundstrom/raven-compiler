# .NET conformance, Raven divergences, and emitted IL

Raven is a .NET language with its own source model. Compatibility therefore
does not mean reproducing C# source behavior everywhere, and language identity
does not justify inventing a private runtime contract where .NET already has a
standard one.

Raven also owns its semantic type system. It is compatible with the CLR type
system and standard .NET metadata, but it is not defined as a copy or subset of
C#'s source type model. Interop types are projected into Raven concepts, used
according to Raven's language conventions, and projected back to their standard
ABI forms. Unified nullability across reference and value types is a deliberate
Raven semantic rule; the distinct runtime encodings remain adapter concerns at
the metadata and code-generation boundary.

This document records the decision boundary used during stabilization. It is
based on current compiler, lowering, metadata, and code-generation behavior,
not only on the language specification. It should be updated when an intentional
divergence is added, removed, or materially reinterpreted.

## Decision rule

Use this order when reviewing a Raven behavior:

1. **Preserve the .NET contract.** Public metadata, calling conventions, type
   identity, accessibility, generic constraints, nullability annotations,
   layout, exception behavior, and other observable ABI details must be
   understandable by ordinary .NET consumers.
2. **Prefer familiar foundational behavior.** When C# expresses an established
   .NET convention and Raven has no stronger reason to differ, matching it
   reduces surprise and improves interoperability.
3. **Keep a divergence when it earns its place.** A different rule is justified
   when it enables Raven's semantic model, removes a legacy constraint without
   harming interop, or improves consistency and language identity.
4. **Project rather than corrupt.** Raven may present standard metadata through
   a more idiomatic source model, but the raw .NET contract must remain
   recoverable and round-trip correctly.
5. **Make divergence explicit.** An unexplained mismatch is a stabilization gap,
   not an implicit language decision.

A proposed divergence should answer all of these questions:

- What can Raven express or explain better because of it?
- Is the benefit semantic, syntactic, or only cosmetic?
- Does a C#, F#, Visual Basic, reflection, or tooling consumer still see a
  conventional .NET contract?
- Can Raven import the same contract from another .NET language?
- Does the rule make Raven more internally consistent, or add another special
  case?
- Are diagnostics, semantic APIs, debugging, and decompilation still coherent?

Freedom from C#'s legacy is a real design opportunity, but not a complete
rationale by itself. The replacement must provide a user benefit and a rule
that Raven can apply consistently.

## Tracking unfinished C# and .NET proposals

Raven already implements ideas that overlap work still moving through C# and
.NET previews. Current examples include union metadata, closed-hierarchy
metadata, extension blocks, and runtime-supported async. Raven does not need to
wait for those features, and an upstream proposal is not automatically the best
Raven source design.

Treat a moving proposal as a compatibility signal:

- Raven's documented semantics remain authoritative until Raven deliberately
  changes them.
- Track the proposal's metadata and tooling contract separately from its C#
  syntax and lowering.
- Prefer capability detection from reference metadata over assumptions based
  only on an SDK version.
- Use a local well-known attribute or compatibility type when the runtime
  accepts assembly-local definitions and no stable framework type exists.
- Stop emitting a compatibility definition when the target framework provides
  the accepted well-known type.
- Add cross-language probes when a preview becomes usable: emit in Raven and
  consume in C#, then emit in C# and consume in Raven.
- Do not silently follow proposal churn. A changed upstream shape requires a
  recorded Raven decision, compatibility tests, and migration notes when it
  changes observable Raven behavior.

This permits Raven to keep its own syntax and semantics while converging early
on shared metadata. If a proposal is abandoned, Raven can retain a useful
feature, but it then owns the compatibility attribute or projection and must
document that fact.

## Conformance boundaries

### ABI and metadata: conformance is required

The following are platform contracts. Raven source may project them, but emitted
and consumed metadata should follow the established .NET representation:

- CLR type and member signatures, including `ref`, `out`, pointers, function
  pointers, and calling conventions;
- type and member accessibility, virtual dispatch, overrides, interface
  implementation, and generic variance and constraints;
- properties, events, delegates, attributes, optional/default parameters, and
  extension metadata;
- tuple element names, required/init-only members, ref-safety markers, and
  nullable transform and flow attributes;
- exception regions, async and iterator state-machine contracts, entry-point
  signatures, and verifiable managed IL;
- layout and marshalling information at native and unmanaged boundaries.

The compiler already has focused metadata coverage for many of these surfaces,
including nullable transform flags, tuples, ref structs, attributes, namespace
members, and generic variance. Raven now emits and imports the conventional
non-null nullable context for source types. Missing nullable flow attributes
remain ABI work, not optional Raven semantics.

### Source semantics: familiarity is the default, not the ceiling

Conversions, numeric behavior, overload resolution, generic construction,
inheritance, dispatch, definite assignment at .NET boundaries, and exception
behavior should normally feel familiar to C# users. These areas expose the same
runtime model, and accidental differences are expensive.

Raven can still adopt a clearer rule. When it does, the rule belongs in the
language specification and in the divergence inventory below, with tests that
prove both Raven behavior and the external metadata contract.

### Syntax: Raven owns the model

Syntax does not need to resemble C# when Raven's spelling is more coherent.
`func`, `let`, `val`, wildcard imports, target-typed member syntax, expression
blocks, and structural patterns are Raven decisions. They should be judged by
readability, composability, recovery, and tooling rather than textual similarity
to C#.

## Current intentional divergences

| Area | Implemented Raven behavior | Why it is legitimate | .NET boundary and verdict |
| --- | --- | --- | --- |
| Namespace functions and constants | Source declarations belong to namespaces and are imported as namespace members. The emitter places them in a synthesized static `NamespaceMembers` container marked with `TopLevelAttribute`. | Removes utility-container ceremony and gives standalone behavior a first-class source identity. | Consumers still receive ordinary static CLR members. **Keep**, while ensuring the projected source symbol and metadata owner both remain discoverable. |
| Accessibility defaults | Namespace declarations default to `internal`; members inside types default to `public`. | Makes the assembly export boundary explicit while removing repetitive modifiers from the body of a type. | Emitted accessibility is standard CLR metadata. **Keep**; this is a source default, not a new accessibility kind. |
| Sealed-by-default classes and closed hierarchies | An ordinary class is closed to inheritance unless marked `open` or `abstract`. Raven's `sealed` hierarchy form describes an abstract closed root with a known permitted subtype set, rather than C#'s concrete “cannot derive further” spelling; `final override` seals an override. | Makes extensibility explicit and lets exhaustive matching work over object hierarchies. The vocabulary is consistent with Raven's distinction between open polymorphism and a closed family. | Ordinary closed classes use the CLR sealed flag. Closed roots and permitted cases currently use an assembly-local `ClosedHierarchyAttribute` until a well-known platform contract stabilizes. **Keep the semantic model, but scrutinize the keyword presentation and track the emerging metadata convention**. |
| `unit` | `unit` is a real one-value source type. Unobserved callable results project to CLR `void`; observed and generic uses retain a value representation. Calls to metadata `void` can produce `Unit.Value` inside Raven expressions. | Gives “no meaningful result” a composable value and makes discards and expression blocks consistent. | Public `void` contracts must remain `void`, while genuine value positions may use the runtime `Unit` type. **Keep with strict round-trip tests**; do not leak `Unit` merely because the emitter is convenient. |
| Expression-oriented control flow | Blocks, `if`, `match`, and `try` can produce values. Callable tails can return implicitly. `return` and `throw` have abrupt expression forms, while `break` and `continue` remain statement-only. | Enables concise composition without forcing effects and loop control into an artificial expression model. The split is internally coherent and toolable. | Lowering uses ordinary branches, returns, and exception instructions. **Keep**. Diagnostics must distinguish a discarded value from an intended implicit result. |
| Immutable lexical bindings | `let` is the canonical immutable local binding and `var` makes mutation explicit; `val` primarily denotes read-only members/signatures. | Makes mutability locally visible and keeps declaration vocabulary consistent. | Locals and fields use normal CLR storage. **Keep**. |
| Immutable collection defaults | A targetless bare collection expression infers `ImmutableList<T>` or `ImmutableDictionary<TKey, TValue>`; `![...]` requests the mutable default and `[|...|]` requests an array. Collection literals also support ranges and comprehensions. | Makes the inexpensive source spelling choose the safer data-processing default while keeping mutability and CLR arrays explicit. | Explicit target typing still constructs ordinary .NET collection types through their normal builder surface. **Keep**, with allocation and builder behavior measured rather than assumed. |
| Generic type construction inference | Calling a generic type without an authored type-argument list can infer its type arguments from constructor arguments. An explicit list always selects its exact arity, including for qualified and nested types; when generic and non-generic types share a name, an applicable non-generic constructor is preferred before generic inference. | Preserves concise, expression-oriented construction while keeping explicit source unambiguous and aligned with .NET arity identity. | The selected type is emitted as an ordinary constructed CLR generic type. **Keep the Raven inference rule and exact explicit-arity behavior**, with source/metadata and semantic-query parity. |
| Properties and initialization | Type-body `val`/`var` declarations are property-first; explicit `field` provides storage. Type-header parameters and `init` blocks form Raven's initialization model. | Avoids treating raw storage as the default abstraction and removes constructor boilerplate. | Emit conventional properties, fields, and constructors. **Keep**, subject to metadata-shape and initialization-order conformance. |
| Local type declarations | Function and block bodies may declare helper classes, structs, records, and enums. They are source-local and emitted as compiler-mangled nested types. | Allows implementation types to live at their narrowest meaningful scope without publishing a nominal container. | The emitted type is an ordinary nested CLR type with no stable public source name. **Keep**, with capture, accessibility, generic ownership, and debugger-name coverage. |
| Unions and closed matching | Raven has union declarations, case payload syntax, structural patterns, sealed-hierarchy exhaustiveness, and an explicit inactive/default state for struct carriers. | Gives closed data shapes and exhaustive decisions a direct model that C# historically lacked and Raven uses consistently. | The carrier must expose the standard .NET/C# union recognition and extraction surface. Raven-only case syntax may project that surface. **Keep the source model; continue ABI alignment**. |
| Unified nullability and pattern-first absence | `T?` is one static nullable shape for reference and value types. Raven recommends binding patterns for handling nullable values and `Option<T>` when absence belongs to the domain. Direct null checks do not refine storage by default. The opt-in `EnableIsNotNullNarrowing` mode narrows an immutable local or parameter in the true branch of a direct `is not null` check; it does not enable general flow analysis. Postfix `!` suppresses reference nullability without a runtime check, but extracts `Nullable<T>.Value` for value types, unlike C#'s `!`. | Gives nullability one source and semantic meaning without implicit smart-cast-like promises about mutable storage. It keeps domain absence explicit and exhaustive while allowing ordinary .NET interop and gradual adoption. | Emit and consume standard .NET nullable annotations and static contracts. Reference `T?` and value `Nullable<T>` remain conventional at the ABI even though Raven projects one model. Conditional flow attributes do not refine Raven expressions. **Keep the unified static model and explicit pattern guidance**. |
| `Option`/`Result` framework projections | Selected framework methods can appear as `Option` or `Result` operations instead of raw `bool`/`out` or throwing forms. The project can disable projections and use the original CLR API. | Makes expected absence and failure explicit and composes with Raven patterns and propagation. | The underlying framework signatures remain unchanged and accessible. **Keep as an explicit, versioned projection**, never a name-based rewrite. |
| Carrier propagation and exception capture | `?`, `try`, and `(try expression)?` provide typed short-circuit and exception-capture forms. | Removes repetitive control-flow scaffolding while keeping failure in the type and syntax. | Lower to standard calls, branches, returns, and exception handling. **Keep**; exception filters, disposal, and async behavior must match CLR guarantees. |
| Extension declarations | Raven groups extension methods and computed properties in `extension` declarations and presents them through instance-style lookup. | Gives related external behavior a namespace without forcing inheritance or wrappers and extends the same model to properties. | Emit ordinary static extension methods/accessors plus the marker surface recognized by current C# extension blocks. **Keep Raven syntax; track the C#/.NET metadata contract as it evolves**. |
| Macros | Macro declarations execute at compile time and expand to syntax; ordinary program metadata need not expose macro execution machinery. | Raven syntax can provide compile-time abstraction without a runtime language feature or legacy preprocessor model. | Macro libraries are versioned compiler plugins, while expanded application output remains ordinary .NET. **Keep**, with declaration isolation and normal language-service behavior while authoring macros. |
| Permissive semantics plus built-in analyzers | Some idioms, such as making a discarded tail value explicit, are warnings that projects may promote, lower, or disable. | Separates deterministic language meaning from team policy and supports procedural and expression-oriented styles without two languages. | Standard diagnostic configuration controls policy; runtime behavior is unaffected. **Keep**. |
| Result-returning entry points | Raven accepts selected `Result` and async `Result` entry shapes and synthesizes a conventional host entry-point bridge. | Makes script and application failure handling fit the same typed-flow model as other Raven code. | The PE entry point must still have a host-supported CLR signature and stable exit/error behavior. **Keep**, with bridge metadata and runtime tests. |

These are not permissions to diverge in adjacent foundational behavior. For
example, Raven's union syntax does not justify nonstandard generic variance, and
expression blocks do not justify a different integer overflow or virtual-call
contract.

## Divergences and gaps still requiring a decision

The current implementation also contains differences that are not yet language
identity and should not be defended as such:

- Generic method groups whose type arguments are inferable from target delegate
  parameters now participate in outer generic inference and final delegate
  conversion. Other generic overload-resolution forms and coarse conversion
  ranking still need a conformance matrix before any mismatch can be called
  intentional.
- Nullable transform flags and the non-null source context now round-trip, but
  nullable flow attributes are incomplete.
- Raven does not currently define separate strict and compatibility nullability
  modes. A future project profile may bundle diagnostic severities for new or
  gradually migrated code, but it must not change declared symbol identity,
  metadata, or pattern meaning. Whether such a profile is useful remains a
  policy and tooling question rather than a semantic gap.
- Raven unions are moving toward the standard C#/.NET recognition surface. Any
  remaining Raven-only public metadata needed for basic construction or
  extraction is an interop gap; Raven-specific metadata is acceptable only for
  information the standard contract cannot represent.
- Declared union case types currently receive flattened metadata names such as
  `Union_Case`. This avoids the current emitter and symbol-model constraints
  around nesting a case type in a generic union, but it is not a friendly or
  final surface for C# consumers. Stabilization must evaluate the emitted
  ownership, generic-parameter forwarding, metadata name, and Raven source
  projection together. The result should give other .NET languages a natural,
  collision-resistant way to name case types without compromising Raven's
  `Union.Case` and target-bound `.Case` conventions.
  The preferred candidate is to nest cases directly in non-generic unions. For
  a generic `Union<T...>`, emit an attributed, arity-zero `Union` companion whose
  nested generic case types forward the carrier's type parameters and derive
  from `Union<T...>`. Raven should use explicit companion/case metadata to
  project these back under the generic union; it must not infer ownership from
  the spelling alone. The design still needs collision, accessibility,
  documentation, type-inference, and future standard-union compatibility tests.
  Generic carriers should additionally expose case factories on the closed
  carrier, for example `Result<T, E>.Ok(T value) -> Result<T, E>`. This works
  with current C# inference because the carrier supplies every union type
  argument, while the publicly nameable case remains available through the
  companion for matching, for example `Result.Ok<T, E>`. Companion-level
  generic factories cannot infer carrier parameters that do not occur in their
  arguments because current C# inference does not use the assignment target or
  return type. Raven may project or hide the carrier factories behind its case
  syntax, but the shared recognition, tag, and extraction contract should stay
  stable and independently versioned from this presentation layer.
- `ClosedHierarchyAttribute` is intentionally assembly-local today because the
  runtime does not yet provide the well-known contract Raven needs. Its name,
  constructor, and permitted-type meaning must be revisited when the C#/.NET
  proposal stabilizes; the current shim is a compatibility bridge, not a claim
  that Raven owns the platform namespace.
- Runtime-supported async is opt-in while the .NET 11 surface remains a moving
  target. Its observable task, exception, cancellation, and debugger behavior
  must remain equivalent to the compiler-generated state-machine path before it
  can become a default.
- Reachable symbol APIs still contain incomplete members. A Roslyn-like public
  API may intentionally differ in naming or projection, but it must not fail
  because a normal query reaches `NotImplementedException`.
- Framework projections need complete raw-member escape hatches and exact
  versioned mappings so that convenience never changes the ABI being consumed.
- Executable emission currently retains direct `System.Private.CoreLib`
  references produced by the Reflection.Emit path, while library emission
  normalizes forwarded public identities to `System.Runtime`. The executable
  choice works on the tested runtime but should be reviewed for portability and
  consistency; it is not a Raven semantic feature.
- External PE methods that cannot be materialized as host reflection objects
  retain their compiler symbols through IL generation and are written as
  target `MemberRef` metadata before the final PE is serialized. This is the
  first metadata-native operand path; source definitions and host-loadable
  operands still use Reflection.Emit while the emitter is migrated
  incrementally.

When investigation finds another mismatch, add it here until it is either fixed
or promoted to the intentional inventory with a rationale and interop evidence.

## Emitted IL policy

Raven should emit valid, verifiable, understandable IL before it attempts broad
optimization. Readability here means recognizable control-flow and metadata
patterns that the CLR, JIT, debuggers, profilers, and decompilers handle well. It
does not mean formatting IL to look exactly like one version of the C# compiler.

Use this priority order:

1. correct runtime and exception behavior;
2. valid metadata and verifiable IL where the source feature is safe;
3. accurate debugging information and source mapping;
4. conventional control-flow, state-machine, closure, and metadata shapes;
5. removal of dead compiler artifacts and avoidable allocations;
6. measured Release optimization.

C# Debug and Release output are useful comparison points because .NET tools are
widely exercised against their shapes. They are evidence, not specifications.
ECMA-335, runtime behavior, the JIT, reflection, and consumer tooling remain the
real constraints.

### Current emission finding

`CompilationOptions` exposes explicit `Debug` and `Release` optimization
levels, and project evaluation selects `Release` for Release configurations or
when the evaluated `Optimize` property is true. The compiler driver continues
to emit a portable PDB in both modes. Release emission attaches sequence points
to meaningful instructions without adding sequence-point-only `nop`s, removes
other debug padding, and enables conservative bound-tree peepholes such as
trivial pattern algebra. Release match lowering also avoids emitting an
unreachable fallback after a final unguarded catch-all arm. Debug remains the
default and retains the existing lowering and emission flow.

This is the first optimization tier, not a claim that Raven now has a general
optimizing backend. Release optimization runs as an ordered post-lowering
pipeline of specialized bound-tree rewriters. `PatternOptimizer` owns only
mechanically equivalent pattern algebra, while `ControlFlowOptimizer` prunes
branches whose conditions are already Boolean literals. Before that branch
pass, `BooleanExpressionOptimizer` applies side-effect-preserving identities to
built-in Boolean expressions, normalizes comparisons with Boolean literals,
and recognizes constants through identity wrappers so more literal conditions
become visible.
Unreachable-statement deletion is deferred until reachability accounts for all
resumable state-machine entry paths. Branch removal and inversion are likewise
deferred until the analysis can preserve scope-exit disposal behavior carried
by lowered gotos. New passes should remain local and conservative until
profiling or emitted-shape evidence justifies broader data-flow or control-flow
optimization.

This policy is especially relevant to ahead-of-time targets: Release should
hand the AOT compiler canonical, verifiable IL with avoidable control-flow,
temporary, and metadata noise already removed. Raven should still measure AOT
output before adding aggressive transformations, because target-specific AOT
optimizers may already perform them more effectively.

Portable-PDB ownership is reconciled after the final PE rewrite. The assembly
reference normalizer can renumber method-definition rows, so Raven matches raw
and final methods by containing-type identity, name, and overload occurrence,
then remaps method debug information, local scopes, state-machine records, and
method-owned custom debug information. Sequence points within one method are
also required to be non-overlapping; the first normalized authored span wins.
This keeps generated state-machine helpers and other later-emitted methods from
claiming an earlier method's source spans.

A representative probe containing a namespace function, value-producing `if`,
capturing lambda, and `Main` produced an assembly that:

- passed `ILVerify` for all five emitted types and seven methods;
- executed and decompiled to the intended high-level control flow;
- used initialized locals and ordinary delegate/closure metadata;
- contained pervasive sequence-point `nop`s;
- emitted redundant branches after terminal `ret` paths and a defensive throw
  after an already-complete non-void body;
- synthesized an unused display class in addition to the closure that actually
  stores the capture;
- emitted an empty `Program` type even though the namespace-level `Main` was the
  selected entry point.

For comparison, equivalent C# Debug output also preserved locals, branches, and
sequence-point `nop`s, while C# Release output reduced the classification method
to a compact conditional branch with direct returns and used one closure type.
This confirms both that debug-friendly IL has legitimate overhead and that
Raven's dead type and unreachable scaffolding are not required by the source
semantics.

The JIT can remove much of the redundant control flow, and ILSpy reconstructed
the intended method successfully. That makes these quality findings rather than
runtime failures. Dead synthesized types and provably unreachable branch
scaffolding should still be removed in every mode: they carry no debugging
benefit and make metadata and decompilation noisier.

### Debug and Release direction

Introduce an explicit optimization/debugging policy rather than accumulating
local “is release” checks throughout generators.

**Debug should prioritize:**

- accurate portable PDBs and stable stepping;
- named, initialized locals that remain inspectable;
- straightforward lowering with source-correlated sequence points;
- recognizable closure and state-machine shapes.

**Release should additionally provide:**

- suppression of sequence-point-only `nop`s and debug-only local preservation;
- control-flow canonicalization, including branch inversion and removal of
  unreachable joins/fallbacks;
- elimination of unused synthesized types, fields, locals, and helpers;
- direct stack use where preserving a local has no semantic or debugging role;
- standard caching of non-capturing delegates and avoidance of unnecessary
  closure allocation;
- conservative, measured optimizations that preserve exception regions,
  evaluation order, overflow behavior, and debugging metadata policy.

Initialized locals are not inherently a Debug-only defect. They aid
verifiability and are also present in common Release output. Change them only
for a measured benefit and with definite-assignment proof.

### Validation strategy

Do not make exact opcode streams a stable language contract. For emission work:

- prove behavior with runtime tests;
- prove public shape with reflection and metadata tests;
- run `ILVerify` for safe representative programs;
- decompile representative Debug and Release assemblies with ILSpy;
- compare C# output when it reveals an established tooling or JIT pattern;
- inspect JIT disassembly or benchmark results before claiming a performance
  improvement;
- keep temporary instruction-shape assertions under the development-only
  code-generation test area.

Decompilation is a compatibility signal, not a semantic oracle. If a standard
decompiler produces unnecessary `goto`s, exposes dead helpers, or cannot
recover a normal high-level construct, first determine whether Raven emitted a
needlessly unusual shape. Do not change correct semantics merely to satisfy one
decompiler heuristic.

## Review checklist

Before accepting a new divergence or lowering shape:

1. Identify the owning rule: Raven source semantics, .NET ABI, or compiler
   implementation.
2. Reduce and test the current compiler behavior.
3. Compare established .NET behavior where it matters, including metadata
   import from another language.
4. State the Raven benefit and why a projection is or is not sufficient.
5. Verify round-trip metadata and tooling behavior.
6. Add the decision to this document and the normative rule to the relevant
   specification chapter.
7. Test observable behavior and public shape; avoid freezing incidental IL.
