# Macro source shapes: existing and future candidates

This developer inventory records macro application shapes and separates current
carrier support from future design candidates. It is not a commitment to add
the proposed forms. The numbering follows the design discussion; “declaration
form” in Form 1 is distinguished from the structured declaration carriers below.

See the [carrier syntax design](../../lang/proposals/macros/carrier-syntax-extensions.md)
for parsing and carrier details, and the
[macro authoring guide](../../macro-authoring.md) for implementation contracts.
Examples use illustrative macro names, not necessarily available library macros.

## Status overview

| Form | Source shape | Status |
| --- | --- | --- |
| 1. Declaration form | `<macro>! <expr>` | Existing expression-header syntax; a dedicated declaration interpretation is a future candidate. |
| 2. Statement/block form | `<macro>! <expr> { <content> }` | Existing expression-header carrier with a token body. |
| 3. Invocation form | `<macro>!(<parameter>)` | Existing parenthesized invocation carrier. |
| 4. Binding form | `<macro>! <identifier> = <expr>` | Future candidate for an explicit binding carrier and binding semantics. |
| 5. Clause-like macro | `func Test(value: int) requires! <expr> { ... }` | Future candidate for a clause attached to a function declaration. |
| Function-like declaration | `<macro>! <identifier>(<parameters>) -> <type> { <content> }` | Existing structured declaration carrier. |
| Type-like declaration | `<macro>! <identifier> : <base-types> { <content> }` | Existing structured declaration carrier. |

“Existing” describes the source carrier. The resolved macro must accept that
carrier and produce syntax valid at the application’s grammar position. A
surface shape alone does not define expansion behavior or introduce symbols.

## Valid and candidate positions

Position describes where the complete application occurs, independently of its
input shape. **Existing** means the carrier supports that position;
**candidate** means a placement to evaluate, not accepted macro syntax;
**—** means no placement is proposed here.

| Form | Expression | Statement | Member/declaration | Type | Pattern | Function-header clause |
| --- | --- | --- | --- | --- | --- | --- |
| 1. `<macro>! <expr>` | Existing | Existing | Candidate for a new declaration meaning; see ambiguity below | — | — | — |
| 2. `<macro>! <expr> { ... }` | Existing | Existing | — | — | — | — |
| 3. `<macro>!(<parameter>)` | Existing | Existing | Existing | Candidate | Candidate | — |
| 4. `<macro>! <identifier> = <expr>` | — | Candidate for local binding | Candidate for field/property-like binding | — | — | — |
| 5. `requires! <expr>` after a function signature | — | — | — | — | — | Candidate |
| Function-like declaration | — | — | Existing | — | — | — |
| Type-like declaration | — | — | Existing | — | — | — |

An **expression position** includes an initializer, argument, or returned
expression. A **statement position** includes a function/block body and global
statement positions where Raven permits them. A **member/declaration position**
is a compilation-unit, namespace, or type-member boundary, subject to the
expanded declaration being legal in that container. Local function-like or
type-like macro declarations are not implied by member support.

A **type position** is a type annotation or type argument; a **pattern
position** is a pattern within matching syntax. A type-like declaration
introduces a declaration rather than occupying a type position. Type and pattern placement for Form 3 is proposed in the carrier syntax
design, but is not implemented.

A **function-header clause position** lies after the signature and before the
function body. It needs an explicit extension to the containing declaration
grammar; ordinary statement support does not grant access to that position.
Whether other callable declarations should accept such clauses remains open.

At a declaration boundary, `<macro>! <identifier>` (with or without a body)
can already select a structured declaration carrier when the remaining tokens
fit a declaration header. This is not expression-header support in member
position. For example, `marker! GeneratedMember` is declaration-shaped there;
use `process!(operation)` to express an invocation with that input explicitly.

## Existing shapes

### Form 1: declaration form / expression header

```text
<macro>! <expr>
```

The compiler already supports this spelling as an expression-header carrier:

```raven
func Process(value: int) -> int => transform! value + 1
```

It supplies one ordinary Raven expression without a parenthesized argument
list. Calling it a “declaration form” does not make it a declaration: current
expression-header support is for expression and statement positions. Any new
declaration meaning or placement for this shape needs a separate design.

### Form 2: statement/block form

```text
<macro>! <expr> {
    <content>
}
```

```raven
guard! value > 0 {
    report failure
}
```

The header is an ordinary Raven expression. The braces carry a lossless macro
token body, which the macro may interpret as Raven fragments or a private DSL.
They do not automatically establish ordinary Raven block semantics. This
carrier can also appear in an expression position when the macro supports it.

### Form 3: invocation form

```text
<macro>!(<parameter>)
```

```raven
let result = transform!(value)
```

This existing carrier supplies parenthesized arguments to an explicitly marked
macro invocation. Here `<parameter>` denotes the caller-supplied argument;
multiple positional or named arguments are supported according to the macro's
input contract. Expression, statement, and member/declaration placement depend
on the macro's permitted expansion target. Type and pattern positions remain
future candidates in the carrier syntax design.

### Function-like declaration

```raven
consumer! Handle(message: Message) -> Result {
    handle message
}
```

The identifier, declaration parameters, and optional return type form a
structured declaration header. These parameters belong to the declaration
being introduced; they are not arguments passed to the macro implementation.

### Type-like declaration

```raven
service! Repository<T> : IRepository<T> {
    repository members
}
```

The structured header can carry type parameters, a parameter list, base types,
and constraints. Function-like and type-like declarations share the declaration
carrier model and must expand into compatible members at their declaration
boundary.

Raw token bodies such as `Name! { content }` and the combination
`Name!(arguments) { content }` also exist.

## Future candidates

### Form 4: binding form

```text
<macro>! <identifier> = <expr>
```

```raven
resource! connection = OpenConnection()
```

This candidate gives the macro an explicit binding name and initializer.
Decisions remain open about declaration versus assignment, mutability, scope,
type inference, initializer evaluation, and any cleanup or lifetime behavior.
An assignment-shaped expression accepted as an expression header would not,
by itself, establish this binding contract.

### Form 5: clause-like macro

```raven
func Test(value: int)
    requires! value > 1 && value < 10 {
}
```

The candidate places a macro clause between a function signature and its body.
In this example, the braces belong to `Test`; the proposed clause payload is
`value > 1 && value < 10`. The macro’s name suggests a precondition, but its
checking and expansion behavior are not specified by this shape.

The design must define clause boundaries, access to function parameters,
composition and ordering of multiple clauses, and how a clause contributes to
the containing declaration. This extends ordinary function declaration syntax;
it is distinct from a standalone statement macro inside the function body and
from proposed custom clauses on a macro’s own declaration-shaped carrier.

## Implementation follow-up

Before promoting a candidate to existing support, define its grammar position,
carrier and input contract, expansion target, symbol and scope behavior, and
diagnostics for incomplete or ambiguous input. Evaluate semantic APIs, editor
support, and TextMate coverage together with focused parser and semantic tests.

Current expression-header and structured declaration coverage is in
[`FreestandingMacroParsingTests`](../../../test/Raven.CodeAnalysis.Tests/Syntax/FreestandingMacroParsingTests.cs);
expression-header input binding is covered by
[`FreestandingMacroSemanticTests`](../../../test/Raven.CodeAnalysis.Tests/Semantics/Macros/FreestandingMacroSemanticTests.cs).
