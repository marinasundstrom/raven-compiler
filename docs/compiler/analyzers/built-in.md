# Built-in analyzers

Raven ships a core analyzer set with the compiler and publishes recommended
naming and style rules in `Raven.Analyzers`. Analyzer diagnostics do not
change whether a program is valid Raven. They form a policy layer over the
language: keep the defaults, promote selected rules to errors, lower their
severity, or disable them to suit the project.

This differs from a compiler error. A compiler error means the program has no
valid meaning or cannot be emitted. An analyzer warning means Raven understands
the program but has identified a convention, likely mistake, or maintainability
concern.

## Configure a rule

Set severity by diagnostic ID in the nearest `.editorconfig`:

```ini
[*.rvn]
# Require explicit handling of a discarded tail value.
dotnet_diagnostic.RAV9034.severity = error

# Do not enforce the preference for Result over throw.
dotnet_diagnostic.RAV9013.severity = none
```

For an opt-in core rule, first add its analyzer type to
`RavenEnabledAnalyzers` in the project file. For a disabled-by-default rule in
`Raven.Analyzers`, an explicit non-`default` severity activates that rule.

Use another section for legacy `.rav` files when a project contains them:

```ini
[*.rav]
dotnet_diagnostic.RAV9034.severity = none
```

Common values are `error`, `warning`, `info`, `hidden`, and `none`. `default`
restores the level in the table below. See
[Analyzer configuration](configuration.md) for project-wide analyzer
participation, source suppression, and the opt-in full returned-value mode.

## Analyzer reference

“Severity” means the descriptor severity before any `.editorconfig`, project,
or command-line override. Analyzers are grouped by kind, and each analyzer has
its own participation default. Raven enables correctness and safety checks in
the core set by default. Install `Raven.Analyzers` to add the recommended
policy layer. The full returned-value mode extends `RAV9034` to bare calls and
member accesses.

### Core compiler analyzers

| Kind | ID | Participation | Severity | Rule |
| --- | --- | --- | --- | --- |
| Typing | `RAV9001` | Opt-in | Info | Add an inferred return type annotation. |
| Typing | `RAV9003` | Default | Warning | Make an event delegate nullable when the event can be empty. |
| Typing | `RAV9004` | Opt-in | Warning | Use `let` when a local declared with `var` is never reassigned. |
| Initialization | `RAV9006` | Default | Warning | Initialize a property in storage or a constructor. |
| Typing | `RAV9012` | Opt-in | Info | Prefer `Option<T>` or `Result<T, E>` over nullable domain flow. A scoped code fix can rewrite simple local null-guarded flow to an `Option` pattern. |
| Error handling | `RAV9013` | Opt-in | Warning | Prefer `Result<T, E>` over `throw` for expected failure. |
| Error handling | `RAV9014` | Opt-in | Warning | Prefer Raven's `Option`/`Result` LINQ alternatives where applicable. |
| Typing | `RAV9015` | Default | Warning | Replace `== null` or `!= null`, which may invoke user-defined equality, with an identity-based `is null` or `is not null` check. Neither form refines the checked storage. This is a safety transformation, not a preference over pattern bindings or `Option<T>`. |
| Design | `RAV9016` | Opt-in | Info | Make an unexposed member private. |
| Design | `RAV9017` | Opt-in | Info | Make a method static when it does not use instance data. |
| Usage | `RAV9018` | Opt-in | Warning | Remove or use a property that is never referenced. |
| Usage | `RAV9019` | Opt-in | Warning | Remove or invoke a method that is never referenced. |
| Immutability | `RAV9026` | Default | Warning | Use the new value returned by an immutable collection operation. |
| Usage | `RAV9027` | Default | Warning | Remove or use an unused local value. |
| Usage | `RAV9030` | Opt-in | Warning | Remove or use an unused parameter. |
| Usage | `RAV9031` | Default | Hidden | Remove an unused import directive. |
| Initialization | `RAV9032` | Default | Warning | Initialize a field in storage or a constructor. |
| Usage | `RAV9033` | Default | Warning | Dispose a disposable value before leaving its scope. |
| Usage | `RAV9034` | Default | Warning | Make an unused expression result explicit. This includes value-forming expressions and a non-`unit` tail value in a `unit` callable; full mode also checks bare calls and member accesses. |
| Error handling | `RAV9037` | Default | Warning | Avoid partial `Option` and `Result` extraction with `Unwrap`, `UnwrapOrThrow`, `UnwrapError`, or `Expect`; handle both cases explicitly. |
| Usage | `RAV9038` | Default | Warning | Await an ignored task-returning call, handle its task, or explicitly discard it with `_ =`. |

### Recommended analyzer package

Add a `PackageReference` to `Raven.Analyzers`. NuGet exposes its assembly
through `analyzers/dotnet`, which Raven's project system loads automatically.

| Kind | ID | Participation | Severity | Rule |
| --- | --- | --- | --- | --- |
| Naming | `RAV9023` | Opt-in | Warning | Follow Raven's constructor-parameter naming convention. |
| Style | `RAV9028` | Opt-in | Warning | Remove an unnecessary trailing separator. |
| Style | `RAV9035` | Opt-in | Info | Prefer `let` over `val` for immutable lexical bindings. |
| Style | `RAV9036` | Default | Info | Prefer `loop` over `while true` for an unconditional loop. |
| Style | `RAV1051` | Opt-in | Warning | Prefer a newline between declarations. |

### Macro-fragment references

The unused-variable analyzers count caller locals and parameters referenced by
macro-reported Raven fragments. They resolve those references through the
compiler's macro-fragment semantic model. This includes a local whose only use
is inside a fragment, such as a JSON macro interpolation.

The macro must report its embedded Raven regions; arbitrary token text is not
treated as a semantic reference. `UnusedLocalAnalyzerTests` covers a caller local
read through a reported expression fragment.

## Choosing a policy

Raven's defaults are recommendations, not a single mandatory programming
style. For example, all of the following are legitimate project choices:

- keep `RAV9034` as a warning to make ambiguous tail discards visible;
- promote it to an error in a codebase that requires explicit value flow;
- disable it in a codebase that primarily uses explicit returns or intentionally
  permits discarded expression results;
- enable full returned-value handling only where every returned member value
  must be handled.

Prefer committing `.editorconfig` with the project so command-line builds and
the Raven language server present the same policy to every contributor.

For nullable code, `RAV9012` expresses Raven's configurable preference for
`Option<T>` or `Result<T, E>` in domain flow. `RAV9015` has a narrower purpose:
it makes an existing equality-based null check strict so user-defined equality
is not involved. It does not make the checked value non-null in either branch.
The language still recommends explicit pattern bindings and matches as the
first teaching model. See
[Nullability and absence](../../lang/nullability.md).
