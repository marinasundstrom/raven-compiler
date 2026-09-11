# Design notes

This document is explanatory and non-normative. The language rules remain in
the main specification files.

## Unions and carrier types

Raven uses the term *carrier* for the declared union type that stores one of a
fixed set of variants. The carrier model is intentional:

* Variant values are distinct values that convert to the carrier when required.
* Variant types, including types synthesized by `case` declarations, are not an
  inheritance hierarchy under the carrier type.
* Extraction is described in the spec in terms of pattern matching,
  `TryGetValue(out CaseType)`, and explicit casts for assertion-style code.

Interop shape and metadata details may evolve over time. Those choices do not
change the normative language rules for variant construction, pattern matching, or
carrier conversion.

## Carrier operators

The spec groups `try`, `(try expression)?`, `?`, and carrier `?.` together because they all
describe how Raven moves values through `Result<T, E>` and `Option<T>`:

* `try` captures exceptions into `Result<T, Exception>`.
* `(try expression)?` captures and then propagates.
* `?` unwraps a success value or propagates the non-success case.
* Carrier `?.` maps a member access over the success case without unwrapping the
  carrier itself.

Only member conditional access is lifted across carriers. Invocation and
indexing stay explicit so the spec keeps carrier flow predictable.

## Closed-shape modeling

Raven supports both unions and sealed hierarchies for closed sets of
alternatives. The spec treats them separately because they solve different
problems:

* Use unions for algebraic data modeling and case-oriented matching.
* Use sealed hierarchies for subtype-oriented object modeling and shared
  behavior.

## `System.Unit` distribution

The language spec treats `unit` as a concrete type with the single value `()`.
How that runtime type is distributed across assemblies is an implementation and
interop concern rather than a surface-language rule.

Possible implementation strategies include:

* shipping a shared reference assembly for `System.Unit`
* mapping `unit` to an existing runtime concept where that remains semantically
  correct

Those choices do not change the spec rules for `unit` in type checking,
generics, tuples, or carrier payloads.

## Outstanding design questions

The following items are implementation and language-design follow-ups, not
normative spec rules:

* `if` expressions without `else` in value contexts. The implementation should
  either require `else` when the value is consumed or define a complete rule for
  the missing branch.
* `await for` cancellation-token flow. Cancellation propagation needs explicit
  source-level rules rather than relying on lowering defaults for optional
  parameters.
