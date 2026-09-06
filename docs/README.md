# Documentation

Raven keeps user documentation and development records in the same repository,
but only an explicit user-facing subset is published by DocFX. The publication
list is defined in [`docfx.json`](docfx.json); adding a Markdown file under
`docs/` does not publish it automatically.

## User-facing documentation

Public documentation should help someone learn or use Raven. It should explain
what a feature does, when to use it, and show representative Raven examples.

### Learn Raven

* [Choose your learning path](learn.md)
* [Raven in 60 seconds](raven-in-60-seconds.md)
* [Install and run Raven](getting-started.md)
* [Introduction](introduction.md)
* [Raven for absolute beginners](raven-for-absolute-beginners.md)
* [Raven for C# developers](raven-for-csharp-developers.md)
* [Metaprogramming in Raven](metaprogramming.md)
* [Authoring Raven macros](macro-authoring.md)
* [Language docs](lang/README.md)
* [Domain modeling](lang/domain-modeling.md)

### Language reference

* [Language reference](lang/spec/index.md)
* [Type system](lang/spec/type-system.md)
* [Grammar](lang/spec/grammar.ebnf)

### Tools

* [Compiler and command line](compiler/raven-compiler.md)
* [Target platforms](compiler/target-platforms.md)
* [Project system](compiler/project-system.md)
* [Extend a Raven project](compiler/extending-projects.md)
* [Metaprogramming in Raven](metaprogramming.md)
* [VS Code extension](compiler/raven-vscode-extension.md)

### Compiler API

The compiler API is published for analyzer, generator, refactoring, and tooling
authors, but it is kept separate from the language-feature documentation.

* [Compiler API overview](compiler/api/README.md)
* [Syntax tree API](compiler/api/syntax-tree.md)
* [Semantic analysis API](compiler/api/semantic-analysis.md)
* [Generated .NET API reference](api/)

## Development documentation

Compiler architecture, implementation designs, investigations, test guidance,
and language proposals are retained for contributors but are not part of the
published user manual or the compiler API section. If compiler-development
documentation is published later, it should have its own top-level section.
These documents live primarily under:

* `docs/compiler/architecture/`
* `docs/compiler/design/`
* `docs/compiler/development/`
* `docs/design/`
* `docs/investigations/`
* `docs/lang/proposals/`
* `docs/testing/`

The [Playground architecture](design/playground.md) document describes the
browser editor, compiler worker, sample-link contract, security boundaries,
and build and test workflow.

The [macro source shapes inventory](compiler/development/macro-shapes.md)
categorizes existing macro carriers and future syntax candidates.

Do not link to these areas from published pages. When a proposal becomes part
of the language, move its user-relevant behavior into the specification and
learning material rather than publishing the proposal as the feature guide.
