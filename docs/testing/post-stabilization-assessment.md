# Post-stabilization assessment

Assessment date: 2026-09-10. Starting revision: `643258c07`.

## Conclusion and scope

The next milestone is dependable behavior and release qualification for the
existing language, following the [MVP roadmap](../roadmap.md). More syntax is
not the immediate priority. The completed test stabilization establishes a
useful regression gate, but does not establish release or bootstrap readiness.

This is a targeted review of the roadmap, language specification, compiler API
status, analyzer limitations, and selected implementation paths. It is not an
exhaustive conformance audit. A source-code exception alone is not evidence of
a reachable compiler defect; the constructor issue below was reproduced.

## Prioritized work

| Priority | Slice | Evidence and completion criterion |
| --- | --- | --- |
| P0 — completed | Diagnose invalid implicit base constructor calls before emission | At the starting revision, `open class Base { init(value: int) {} } class Derived : Base { init() {} }` succeeds with `rvnc --output-type classlib --no-emit`, then crashes during emission with `Base type requires a parameterless constructor`. Add compiler diagnostics and focused coverage for explicit, primary, and synthesized derived constructors; keep valid base calls working. |
| P1 — completed | Keep the supported-feature statement accurate | The analyzer guide still described macro-fragment usage as unsupported, although `UnusedVariableAnalyzer` resolves fragment references and `UnusedLocalAnalyzerTests` covers them. The operations status also listed an intentionally unwrapped nullable helper as a missing public operation. Correct these stale descriptions in this assessment slice. |
| P1 — headless tiers completed | Qualify editor behavior on project-backed workloads | The completed baseline/runtime passes do not qualify the opt-in LSP integration and performance tiers. Select representative project-backed hover, completion, diagnostics, and repeated-edit scenarios from the test impact map. Fix any compiler semantic discrepancies at the compiler API boundary. |
| P1 | Execute candidate release gates with recorded provenance | Run the sample build/run workflows, target-framework matrix, and IL checks in the release gate document. Record exact revision, SDK, target frameworks, outcomes, and reviewed exemptions. Existing project sample compilation is not proof that every sample runs correctly. |
| P2 | Select and validate a small set of end-to-end workloads | Use the roadmap's CLI, domain, web, and mixed Raven/C# candidates. Demonstrate clean setup, build, run, and test with a concise support/limitations statement. This supplies evidence for release qualification rather than expanding language scope. |
| P2 | Freeze bootstrap artifacts only after qualification | Follow ADR 0002: qualify the compiler foundation first, then record artifacts, checksums, provenance, and the immutable foundation tag. No foundation version is assumed qualified by these test results. |

## Explicit boundaries, not newly discovered defects

The [macro specification](../lang/spec/macros.md) reserves bracket-delimited
invocations and states that quotation currently covers expressions, not
statement/member/token/list quotation. These are documented scope boundaries.
Expanding them requires a workload and a separate feature proposal.

The Operations API intentionally unwraps internal nullable-value and
required-result helpers. Adding public wrapper kinds is not a completeness
requirement by itself.

## Validation and follow-through

The preceding stabilization reported 5,452 baseline check executions and 851
runtime/integration check executions across bounded batches and focused retries.
These are execution totals from that work, not an assertion that every release
gate passed in one uninterrupted run at the starting revision.

For each implementation slice, use the smallest relevant pre-change baseline,
add behavior-focused regressions, validate the affected path, and commit it
separately. Preserve the distinction between a verified bug, a documented
boundary, and an unexecuted qualification gate.

References:

- [Test impact map](test-impact-map.md)
- [Release and bootstrap gates](release-and-bootstrap-gates.md)
- [Bootstrap foundation decision](../compiler/architecture/decisions/0002-qualify-bootstrap-foundation-after-stabilization.md)
- [Operations status](../compiler/api/operations-implementation-status.md)
- [Built-in analyzers](../compiler/analyzers/built-in.md)

## Completed follow-through

The first implementation slice adds `RAV1501` during semantic diagnostics when
an implicit base call has no parameterless constructor. Regression tests cover
explicit, primary, and synthesized default constructors and prove emission
fails normally without writing an assembly. An invalid explicit primary base
call retains its own diagnostic rather than also receiving an implicit-call
error. Existing valid constructor and record inheritance coverage remains green.

Validation: compiler and driver builds, 86 focused checks, 30 neighboring record
and partial-class checks, and the original CLI repro now exiting with a normal
compiler error. The headless editor qualification is recorded in the [editor qualification report](editor-qualification-2026-09-10.md): 345 integration cases and 18 performance cases pass after two further slices. Live client validation and the release qualification rows remain open.
