# Staged bootstrap procedure

Raven will bootstrap in explicit versions. The immediate objective is not to
rewrite the compiler in Raven; it is to qualify a dependable C# compiler and a
Raven-authored foundation that later versions can trust.

The bootstrap labels are separate from Raven's public SemVer release numbers:

1. **Bootstrap v1** — the full C# compiler builds the first trusted
   `Raven.Core`.
2. **Bootstrap v2** — the full compiler remains implemented in C#, but uses the
   frozen v1 `Raven.Core` types in its compiler APIs.
3. **Bootstrap v3** — the full compiler is implemented in Raven.

The public release immediately before bootstrap v1 is the stable `0.1.0`
release, not another preview and not `1.0.0`. It marks a qualified foundation
for Raven's staged bootstrap while retaining the versioning latitude appropriate
for an experimental language. Bootstrap tags remain separate from this SemVer
identity.

Each version is built by an exact earlier version. A version is accepted only after
its compiler behavior, foundational libraries, samples, targets, and artifact
provenance pass their gates. Self-compilation by itself is not sufficient.

This procedure implements [ADR-0002](decisions/0002-qualify-bootstrap-foundation-after-stabilization.md).
The detailed language-correctness work remains tracked in [Syntactic and
semantic stabilization](syntactic-and-semantic-stabilization.md), and the
executable release checks are listed in [Release and bootstrap compatibility
gates](../../testing/release-and-bootstrap-gates.md).

## Bootstrap v1: stabilize and freeze the foundation

The current compiler remains implemented in C# and is the behavioral oracle.
Before freezing it, exercise Raven as a language for compiler-shaped code and
remove defects that would otherwise become part of every later version.

The full product release remains a lockstep artifact family, but the minimal
bootstrap-v1 dependency closure that must be frozen is smaller:

- the first trusted Raven-authored `Raven.Core` assemblies for supported
  targets, checked into the repository;
- a manifest containing the exact source commit, toolchain, assembly identities,
  and checksums; and
- an immutable source tag and published v1 compiler artifact capable of
  reproducing those Core binaries when that exceptional rebuild is necessary.

`Raven.Macros`, the SDK, language server, editor extension, packages, archives,
and templates remain release-matched products and foundation validation
workloads. They are not inputs required to build bootstrap v2, and therefore do
not belong in the minimal frozen bootstrap closure. In particular,
`Raven.CodeAnalysis` must not acquire a dependency on `Raven.Macros`.

Do not choose the foundation version or tag until the stabilization and
foundation gates pass against the exact clean commit. Once selected, retain the
artifacts and their checksums; do not move the tag or reconstruct an
approximation from later source.

### Checked-in Core boundary

The normal bootstrap-v2 build reads the frozen v1 `Raven.Core` assemblies from
the repository. It does not rebuild them and does not invoke the v1 compiler.
The intended repository shape is:

```text
eng/bootstrap/v1/
  manifest.json
  net10.0/Raven.Core.dll
  net11.0/Raven.Core.dll
```

The manifest records the Raven public release version, source tag and commit,
.NET SDK, target frameworks, compiler artifact identity, Core assembly identity,
and SHA-256 digest of every checked-in binary. Normal builds verify these
digests before using the assemblies and fail directly when a file is absent or
does not match. They must not fall back to a repository build, NuGet cache,
global SDK, or output-directory copy.

Checking in Core makes the dependency visible and removes the historical v1
compiler from ordinary development. The cost is deliberate binary history and
an explicit review whenever the seed changes.

### Exceptional v1 Core rebuild

Rebuild bootstrap-v1 itself only when reproducing or intentionally correcting
the first Core binaries:

1. create a clean worktree at the exact immutable v1 source tag;
2. restore the recorded .NET SDK and v1 compiler inputs;
3. run `scripts/rebuild-bootstrap-v1-core.sh <compiler-version>
   --core-version <core-version> --bootstrap-tag bootstrap-v1`;
4. build Core for every recorded target using that compiler host;
5. run `dotnet-ilverify` over each exact target-specific Core assembly and fail
   the candidate build on any invalid IL;
6. run Core source, metadata, C#-consumer, Raven-consumer, serialization, and
   runtime tests;
7. compare the generated assemblies and manifest with the checked-in seed; and
8. if a correction is essential, publish a new immutable v1 foundation
   revision and review the binary and manifest update as an exceptional
   backport.

The rebuild script produces a candidate under `artifacts/bootstrap`; it does
not overwrite `eng/bootstrap/v1`. Updating the checked-in seed is a separate,
reviewable operation. Ordinary bootstrap-v2 builds run
`scripts/verify-bootstrap-v1-core.sh` before consuming the checked-in seed and
never regenerate it.

### Commit and tag identity

Tag the exact qualified commit for every completed bootstrap version. Bootstrap
tags identify architectural role and remain distinct from public SemVer release
tags, although both tags may point to the same commit:

- `bootstrap-v1` identifies the full C# compiler commit and checked-in Core seed;
- `bootstrap-v2` identifies the full C# compiler after Raven.Core compiler API
  adoption is complete; and
- `bootstrap-v3` identifies the first fully Raven-authored compiler that passes
  the self-hosting and compatibility gates.

Use annotated tags and record the tag object, peeled commit, public release tag,
and artifact checksums in the bootstrap manifest. Never move or reuse a
bootstrap tag. If an exceptional v1 correction is required, create a new
revision tag such as `bootstrap-v1.1` and retain `bootstrap-v1` and its original
checked-in artifacts in history.

The v1 tag is created only after the generated Core binaries have been reviewed,
checked into `eng/bootstrap/v1`, and verified from the candidate commit. The v2
and v3 tags likewise follow their complete version-transition gates; a branch
name or untagged successful local build is not a bootstrap identity.

### Compiler and Core version identities

Track these identities independently even while public releases normally ship
them in lockstep:

| Identity | Purpose |
| --- | --- |
| Compiler product version | Identifies `Raven.CodeAnalysis`, compiler host, SDK, and diagnostics behavior |
| Raven.Core package/file version | Identifies the concrete Core implementation and public library release |
| Raven.Core CLR assembly version | Defines the CLR binding/API compatibility epoch; currently stable across preview packages |
| Bootstrap tag and revision | Identifies which compiler/Core dependency model produced the next compiler version |
| Source commit and binary hash | Proves the exact source and artifact rather than relying on a friendly version string |

The bootstrap manifest records every identity. A lockstep public release may
give the compiler and Core the same package version, but build logic must not
infer one from the other. Bootstrap v2 pins the checked-in Core hash and CLR
identity it was compiled against.

Later Core releases may add behavior or APIs without changing the checked-in v1
seed. The compiler can distribute or consume a newer compatible Core only after
metadata and runtime compatibility gates prove that its CLR identity and the
compiler API types remain valid. A breaking Core ABI change requires an
explicit compatibility epoch and bootstrap decision; it must not appear as an
ordinary package rebuild under the same assembly identity.

### Foundation correctness gates

`Raven.Core` is a bootstrap input, not an incidental build output.
`Raven.Macros` is a high-value dogfooding and compatibility artifact. Validate
each of these boundaries independently:

1. **Source generation** — the C# compiler builds their Raven source cleanly
   and deterministically for every supported target framework.
2. **Assembly contract** — emitted public types, generic parameters,
   constraints, nullability, union cases, extension members, macro providers,
   and assembly identities have the intended metadata shape.
3. **Raven consumption** — external Raven projects resolve, compile, and use
   the contracts through the packaged SDK rather than repository-only paths.
4. **C# consumption** — .NET consumers can load and use public Raven carriers
   and macro/compiler contracts without hidden Raven-only assumptions.
5. **Runtime behavior** — `Option`, `Result`, unions, propagation, parsing,
   serialization, and standard macro expansions behave as specified after
   emit and reload.
6. **IL validity** — `dotnet-ilverify` accepts the exact checked-in
   target-specific `Raven.Core` assemblies with no skipped verification gate.
7. **Macro compatibility** — provider discovery, application shape, typed expression
   contracts, diagnostics, fragment services, source mapping, and generated
   runtime behavior work from the version-matched `Raven.Macros` assembly,
   without making that assembly a compiler bootstrap dependency.
8. **Target compatibility** — the .NET 11 compiler host builds and runs both
   .NET 10 and .NET 11 consumers with the matching Core and macro assemblies.
9. **Artifact provenance** — every consumer reports whether it used repository,
   installed, or packaged artifacts; no gate passes because of a stale global
   SDK, local package cache, or output-directory copy.
10. **Sample execution** — every standalone sample and executable project
    sample builds and runs successfully. Unsafe, non-terminating, browser,
    server, hardware, and library-only cases require an explicit reviewed
    classification and the closest meaningful unattended smoke check.

### Current stabilization findings

The broad semantic and isolated runtime baselines pass on the current
development line, but green totals hide skipped runtime cases that must be
resolved or explicitly excluded from the compiler-writing subset:

| Area | Current evidence | Required disposition |
| --- | --- | --- |
| Async resource lifetime across `await` | Legacy test is skipped pending replacement | Add current semantic/runtime coverage before relying on `use` in async compiler code |

The C# compiler source also identifies the following high-value translation
families. Each family needs both a direct interop-shaped probe and, where the
meaning differs, an idiomatic Raven probe:

| C# compiler pattern | Likely Raven form | Stabilization pressure |
| --- | --- | --- |
| `Try*` plus `out` and nullable lookup helpers | `Option<T>`, `Result<T, E>`, or a recovery union | generic cases, pattern extraction, overloads, metadata round trips |
| nullable lazy caches and `??`/`??=` | explicit `Option<T>` or encapsulated mutable state | initialization, mutation, concurrency, public type information |
| nested switch/type/property patterns | unions and exhaustive `match` | parsing, binding, exhaustiveness, narrowing, lowering |
| immutable arrays, dictionaries, builders, and LINQ | Raven collection expressions and .NET immutable collections | generic inference, extensions, lambdas, iteration, allocation behavior |
| syntax/bound visitors and rewriters | interfaces/classes plus generated dispatch or union matching | inheritance, overrides, generic return types, recursive traversal |
| async services and cancellation | async functions with .NET `Task`/`ValueTask` and `CancellationToken` | captures, state machines, exception/cancellation boundaries |
| iterator helpers using `yield` | iterator support or an explicit collection-building alternative | compiler-writing subset decision and runtime code generation |
| `ref`, `out`, spans, and metadata readers | explicit .NET interop boundary | by-reference binding, overload resolution, escape/lifetime safety |
| locks and concurrent caches | .NET concurrency primitives behind Raven APIs | structured cleanup, exception safety, thread-safe initialization |
| exceptions converted to diagnostics/recovery | diagnostics plus `Result` or a purpose-built recovery union | preserve recovered values and multiple diagnostics together |

The inventory should grow from representative compiler source, not from
synthetic language-feature permutations alone. Prefer small workloads modeled
after parser, binder, symbol, lowering, diagnostic, macro, and workspace code.

## Change allocation by bootstrap version

Use the earliest version that can make a change safely and prove it independently,
but do not pull later-version structural work into foundation stabilization. The
following allocation is the default:

| Change | Bootstrap v1: stabilize foundation | Bootstrap v2: native APIs in C# compiler | Bootstrap v3: Raven source port | After parity |
| --- | --- | --- | --- | --- |
| Parser, binder, lowering, emit, runtime, Core, or macro correctness defect | **Do now** with a reduced test | Carry the fixed behavior forward | Match the fixed oracle | — |
| Unclear syntax or semantic rule needed by compiler code | **Decide and document now**; breaking changes are allowed before freeze | Consume the stable rule | Match it differentially | — |
| Missing compiler-writing capability | Add now only when essential and sufficiently specified; otherwise record an explicit subset exclusion | Re-evaluate before API adoption depends on it | Implement only after the rule is stable | Expand convenience surface later |
| Core public contract correction | **Do now** when bootstrap v2 will depend on it | Consume the frozen contract | Preserve ABI/semantics | Evolve through normal versioning later |
| Macro public contract correction | Do now when needed for reliable validation, but do not add it to the bootstrap closure | Exercise migrated compiler APIs from the version-matched library | Preserve behavior as a higher layer | Evolve through normal versioning later |
| `Raven.CodeAnalysis` API returning `Option`, `Result`, or unions | Prove the carriers and version boundary; do not create an implicit build cycle | **Primary version for this work** | Consume the established contract | Refine only with an explicit API decision |
| Optional macro API inputs using `Option<SyntaxType>` | Ensure Core and macro authoring can express and consume the shape | **Migrate coherent API families here** | Use the migrated API | — |
| Internal C# nullable/out/exception cleanup with no public effect | Do only when required for correctness or a reliable oracle | Small cleanup may accompany an owned API migration | Prefer idiomatic Raven in the port | Broad cleanup belongs here |
| Port a compiler component from C# to Raven | Do not start | Do not start merely to exercise a new contract | **Primary version for this work**, one boundary at a time | Remove retired C# implementation after sustained parity |
| Broad compiler architecture redesign | Only when current structure prevents correctness | Only when required to make a stable API boundary | Avoid during initial parity | **Primary version for this work** |
| Performance/cache/source-text rewrite | Fix measured correctness, hangs, or release-blocking regressions now | Make bounded improvements with invariant coverage | Preserve behavior during parity | Broader redesign after measurement |
| New convenience syntax, unrelated standard macros, or showcase features | Defer unless they directly exercise or unblock the compiler-writing subset | Defer if they complicate API migration | Defer during parity | Consider after bootstrap milestones |
| Packaging, target selection, artifact identity, provenance | **Do now**; these are foundation correctness | Enforce exact bootstrap-v1 inputs | Record the version that built each component | Continue as release infrastructure |
| Tests, compiler-shaped Raven fixtures, sample gates, and differential harnesses | **Do now and retain** | Expand for each migrated API | Reuse against both implementations | Keep as compatibility coverage |

### Version-allocation questions

Before accepting a change, answer in order:

1. Does current behavior make the C# compiler or `Raven.Core` an unreliable
   bootstrap foundation, or make `Raven.Macros` an unreliable validation
   workload? If yes, fix it before the freeze.
2. Is the behavior required by the explicit compiler-writing subset? If not,
   decide whether a documented exclusion is safer than expanding the language
   during stabilization.
3. Does the change require the compiler to consume types produced by the
   foundation it is currently building? If yes, it belongs after the explicit
   version boundary, not in a cyclic bootstrap-v1 graph.
4. Does the change alter a public contract while leaving the C# implementation
   intact? It normally belongs in bootstrap v2.
5. Does the change primarily translate or restructure implementation code? It
   belongs in bootstrap v3 or the post-parity cleanup.
6. Can the change be verified through diagnostics, symbols, metadata, runtime
   behavior, or differential output without relying on internal lowering
   shape? If not, define the stable boundary before proceeding.

### Foundation freeze policy

Before the foundation tag is selected, intentional breaking changes are
acceptable when they remove ambiguity, correct Raven semantics, or make the
compiler-writing subset sound. They must update documentation, samples,
language services, Core/Macros, and focused coverage together.

After the foundation is frozen, its tag and artifacts remain immutable. A
later defect does not automatically reopen bootstrap v1. Most compiler
corrections and improvements discovered while building bootstrap v2 remain
forward-only on the v2 line.

Backport to the maintained v1 line only when a reduced reproduction proves one
of these conditions:

- the frozen v1 compiler miscompiles or cannot rebuild the trusted v1
  `Raven.Core`;
- the v1 compiler or Core cannot correctly build, load, or support a required
  bootstrap-v2 contract;
- the frozen Core behavior or metadata contract is wrong for an API that v2
  must consume; or
- a release-critical security or integrity defect affects the frozen bootstrap
  artifacts themselves.

Prefer a minimal, behavior-focused correction. Release it as a clearly
identified v1 foundation revision, rebuild only the affected frozen artifacts,
and retain the original tag, checked-in binaries, and checksums in history for
provenance. Do not
silently rebuild an existing version, synchronize unrelated v2 improvements
backward, or backport merely to keep branches visually similar.

## Bootstrap v2: Raven-native compiler contracts in the C# implementation

After bootstrap v1 is frozen, build the next compiler line against the
hash-verified v1 Core assemblies checked into the repository. The compiler
implementation remains C#,
but coherent public and cross-component API families may use types authored in
`Raven.Core`.

Adopt contracts by meaning:

- expected absence becomes `Option<T>`;
- expected success/failure becomes `Result<T, E>`;
- closed compiler states become named or ad hoc unions;
- recovered syntax plus diagnostics uses a purpose-built recovery result;
- cancellation, violated invariants, and host failure remain exceptional;
- nullable signatures remain where Raven faithfully projects a .NET ABI.

Migrate one API family at a time. Before removing its transitional contract,
test Raven and C# consumers, standard macros, language services, analyzers,
incremental queries, emitted metadata, packaging, and clean external projects.
The existing C# implementation remains the semantic oracle.

`Raven.CodeAnalysis` may depend on the frozen v1 `Raven.Core` contract assembly
in bootstrap v2. It must not depend on `Raven.Macros`; standard macro
providers remain a higher, replaceable layer that consumes compiler APIs.

## Bootstrap v3: port the full compiler to Raven

Begin the source port only after the compiler-writing subset and bootstrap-v2 API
boundaries are stable. Port one coherent component at a time behind an explicit
interface or data contract. Candidate seams include syntax utilities,
diagnostics, analyzers, generators, visitors/rewriters, metadata helpers,
parsing, binding, lowering, and emission, but their order is determined by
dependency and differential-test boundaries rather than source-file size.

For every component:

1. preserve shared input fixtures and expected public results;
2. run the C# and Raven implementations over the same inputs;
3. compare diagnostics, syntax, symbols, operations, metadata, and observable
   behavior as applicable;
4. keep the C# implementation available until the Raven component reaches
   parity and the replacement boundary is reversible;
5. classify every mismatch before deciding which implementation changes; and
6. rerun the version-transition gates before making the Raven implementation the
   default.

Direct, reviewable translation is preferred during parity. Idiomatic Raven is
appropriate where `Option`, `Result`, unions, records, functions, or patterns
are already established contracts and clearly reduce ambiguity. Broad
architectural cleanup waits until the component agrees with the C# oracle.

The final self-hosting gate compiles the Raven compiler with the prior trusted
bootstrap v2 and then uses the resulting v3 compiler to rebuild the same compiler and
foundation. Compare the public artifacts and behavior; byte-for-byte equality
is desirable where deterministic emission promises it, but semantic and ABI
equivalence remain the required contract.

## Dogfooding ladder

Dogfooding should increase before the compiler source port so the port is not
the first realistic Raven workload:

1. `Raven.Core` and `Raven.Macros` remain authored in Raven.
2. Standard macros use the ordinary macro declaration authoring shape whenever
   that shape can express the contract; class-based providers remain for
   exceptional interop needs rather than as the default.
3. Samples and tests consume packaged Core and macro APIs through public
   project boundaries.
4. New language-facing tools, generators, analyzers, and compiler-shaped test
   fixtures are authored in Raven when the current subset can express them
   reliably.
5. Migrated compiler APIs are exercised first from Raven-authored standard
   macros and tools, while retaining C# interoperability coverage.
6. Raven compiler components replace C# components only after differential
   parity.

A dogfooding failure is evidence. Classify it as a compiler defect, unclear
language/API rule, missing compiler-writing capability, runtime/library defect,
macro-authoring defect, tooling defect, or unsuitable use of Raven. Do not hide
it with repository-only paths or a C# rewrite merely to make a gate green.

## Regression isolation procedure

Every compiler defect found by Core, Macros, compiler-shaped probes, or samples
must retain a reduced regression. Preserve the original workload as integration
coverage, but do not rely on it alone: a large program can hide whether the
first divergence occurred during binding, lowering, emission, or execution.

Use the smallest applicable set of boundary tests:

1. **Syntax and binding** — assert diagnostics, declared and referenced
   symbols, inferred types, conversions, and operations. This establishes
   whether the compiler understood the source contract before lowering.
2. **Lowering** — cover an observable lowering invariant only when the defect
   first appears there. Prefer stable control-flow or operation behavior over
   exact lowered-node or instruction sequences.
3. **Emission** — assert public metadata shape and run `ILVerify` over the
   emitted assembly. Exact opcode assertions are temporary development aids,
   not release regressions.
4. **Reloaded runtime behavior** — load the emitted assembly through the normal
   runtime boundary and assert results, side effects, exceptions, and lifetime
   behavior.
5. **Integration workload** — keep the originating Core, macro, compiler-shaped
   probe, or sample in the appropriate aggregate gate.

Run the reduced case alone and with its feature/runtime suite. If it passes in
isolation but fails in the wider suite, investigate shared state, ordering,
incremental caches, artifact provenance, or a combined lowering/emission path
instead of weakening the assertion.

Cluster failures by their earliest divergent boundary and semantic shape. A
family of reduced failures that shares an owner is evidence for repairing or
refactoring that shared compiler component. A one-off patch is appropriate only
when the reduced case proves the behavior is genuinely local. Never work around
a compiler defect in `Raven.Core`, `Raven.Macros`, or a sample merely to make a
bootstrap gate pass.

## Defect and backport ledger

Maintain a ledger throughout stabilization and porting. Each entry records:

| Field | Meaning |
| --- | --- |
| Identifier and first failing commit/version | Stable reference and provenance |
| Reduced Raven program | Smallest compiler-owned reproduction |
| Source compiler pattern | The real compiler code shape the probe represents |
| Expected contract | Documented language, compiler API, or runtime behavior |
| Actual result | Diagnostic, crash, hang, wrong symbol/metadata, or runtime behavior |
| Classification | Bootstrap-v1 defect, unclear contract, port-only defect, bootstrap accommodation, or deferred capability |
| Owning layer | Parser, binder, semantic model, lowering, emit, Core, Macros, SDK, or tooling |
| Fix and regression coverage | Commit plus syntax/binding, lowering, emit/ILVerify, runtime, and integration boundaries that apply |
| Backport decision | Evidence for a corrected v1 revision, forward-only fix, or explicit version boundary |

The default decision after the v1 freeze is forward-only. A v1 backport requires
a reduced failure at the frozen compiler/Core dependency boundary and an
explanation of why bootstrap v2 cannot safely own the correction. When that
threshold is met, fix and cover the defect on the maintained v1 line, produce a
new immutable foundation revision, and carry the corrected behavior forward.
Unclear behavior is decided and documented before either compiler becomes the
oracle. Port-only defects are fixed forward with differential coverage.
Bootstrap-only accommodations remain explicit and do not become ordinary
language rules.

## Evidence recorded at every freeze

Record at least:

- exact source commit and immutable tag;
- annotated bootstrap tag and any corresponding public SemVer release tag;
- .NET SDK version and supported target frameworks;
- compiler, Core, macro, SDK, package, and editor artifact versions/checksums;
- bootstrap version and artifact used to build every foundation assembly;
- repository versus installed toolchain provenance;
- baseline, runtime, target-matrix, standalone-sample, project-sample, package
  consumer, and editor smoke-test results;
- known skipped tests and their accepted disposition;
- compiler-writing subset inventory and deferred capabilities; and
- defect-ledger state and backport decisions.

The next version starts from that recorded artifact family, never from an
unqualified moving checkout.
