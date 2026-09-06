# Raven Programming Language

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Website](https://img.shields.io/badge/website-Raven-brightgreen.svg)](https://marinasundstrom.github.io/raven/)
[![GitHub Release](https://img.shields.io/github/v/release/marinasundstrom/raven?display_name=tag&sort=semver)](https://github.com/marinasundstrom/raven/releases/latest)
[![Raven.CodeAnalysis](https://img.shields.io/nuget/v/Raven.CodeAnalysis.svg?label=Raven.CodeAnalysis)](https://www.nuget.org/packages/Raven.CodeAnalysis)

Raven is a pragmatic, typed, general-purpose programming language for .NET. It
makes functional composition, algebraic modeling, procedural code, and
object-oriented design complementary parts of one toolset, with direct access
to the .NET runtime and ecosystem.

Raven is under active development. Its language and compiler combine an
expression-oriented surface, explicit mutability, structural pattern matching,
`Option`/`Result`-based flow, and direct interoperability with existing .NET
libraries.

Raven is not defined by a single programming paradigm. It provides syntax,
types, and libraries for functional programming patterns where transformations
and data flow benefit from them, while retaining first-class procedural,
object-oriented, and systems-oriented programming tools.

The implementation is also a compiler-as-a-service playground. The compiler
core follows a Roslyn-like shape with immutable syntax trees, semantic models,
diagnostics, and services that can support command-line compilation, editor
features, analyzers, and language experiments.

Releases provide platform-specific SDKs containing the `rvn` frontend,
`rvnc` compiler driver, language server, build assets, core library, and macro
library, plus a VS Code extension. See [Getting Started](docs/getting-started.md)
for checksum-verified installation instructions. Building from source remains
the contributor workflow for compiler development.

## Start Here

Visit the official [Raven language website](https://marinasundstrom.github.io/raven/),
or browse the Markdown sources under [`docs/`](docs/).

- [Getting Started](docs/getting-started.md) - install the SDK and VS
  Code extension, run a sample, and create a small project.
- [Contributing](CONTRIBUTING.md) - build and test repository artifacts in
  isolated terminal and VS Code development environments.
- [MVP Roadmap](docs/roadmap.md) - outcome-based milestones for turning the
  current language, tooling, and workloads into an evaluatable release.
- [Raven for Absolute Beginners](docs/raven-for-absolute-beginners.md) - learn
  programming from the beginning with Raven.
- [Language Introduction](docs/introduction.md) - guided language overview.
- [Raven for C# Developers](docs/raven-for-csharp-developers.md) - common C#
  shapes translated into Raven idioms.
- [Language Philosophy](docs/lang/philosophy.md) - design principles for Raven
  language changes.
- [Meaning of Raven Features](docs/lang/feature-meaning.md) - semantic guidance
  for choosing language constructs in application code.
- [Domain Modeling](docs/lang/domain-modeling.md) - patterns for values, states,
  behavior, dependencies, and object-oriented models.
- [Nullability and Absence](docs/lang/nullability.md) - unified nullable types,
  explicit pattern binding, and `Option<T>` guidance.
- [Language reference](docs/lang/spec/index.md) - current language specification and feature reference
  normative language docs and grammar links.
- [Compiler Docs](docs/compiler/index.md) - architecture, APIs, diagnostics,
  language server, and development notes.

## Language Snapshot

Raven favors:

- expression-oriented code, while keeping statements for effects and early exits
- plain top-level functions for standalone operations and workflows
- signposted declarations: `func`, `let`, `var`, `event`, `class`, `union`,
  `case`, and related keywords say what is being declared
- explicit immutable and mutable lexical bindings with `let` and `var`
- explicit pattern bindings in `match`, `if`, `while`, `for`, and deconstruction
- `Option<T>` for absence and `Result<T, E>` for expected failure
- records, primary constructors, unions, and target-typed shorthand
- direct use of .NET libraries, async APIs, collections, and IL tooling

These are defaults, not restrictions. Start with values and functions when they
describe the problem directly. Use records and unions to make domain states
explicit. Use classes, interfaces, methods, and mutable state when identity,
lifecycle, encapsulation, or polymorphism are part of the domain.

Raven does not make objects the mandatory container for code. A program can
begin with top-level statements and plain functions—there is no required
`Program` class, and utility functions do not need to be wrapped in a class.
Introduce classes where they improve the model, not merely to satisfy a
structural convention.

Raven has no `void`; the empty result type is `unit`, written as `()`.

```raven
import System.Console.*
import System.Linq.*

func Main() -> () {
    let requests = [
        ShipmentRequest("REQ-1001", "NorthStar", 10),
        ShipmentRequest("REQ-1002", "Oceanic", 3)
    ]

    let message = FindRequest(requests, "REQ-1002") match {
        Ok(let request) => "Ready: ${request.Id} via ${request.Carrier}"
        Error(let err) => "Cannot quote shipment: $err"
    }

    WriteLine(message)
}

func FindRequest(requests: ShipmentRequest[], id: string) -> Result<ShipmentRequest, string> {
    return requests.FirstOrError(r => r.Id == id, () => "request not found")
}

record class ShipmentRequest(val Id: string, val Carrier: string, val WeightKg: int)
```

The example shows ordinary .NET interop (`System.Console`, LINQ-style extension
methods), explicit immutable bindings, records with promoted constructor
parameters, and `Result`-driven recoverable flow.

## If You Are Coming From C#

Raven is not trying to replace the .NET ecosystem around C#. It is exploring a
different source model for common application and compiler problems while still
emitting regular .NET assemblies.

Learning Raven from C# therefore involves some deliberate unlearning. The goal
is not to unlearn object-oriented design; it is to stop treating a class as the
required home for every entry point, helper, operation, or dependency. First ask
what the concept is. Introduce an object when identity, state, lifecycle, or
polymorphism is actually part of the answer.

| In many C# codebases | Raven's preferred shape |
| --- | --- |
| Static helper classes used only to hold methods | Plain top-level functions |
| Context-dependent declarations where shape is inferred from placement | Declaration keywords such as `func`, `let`, `var`, `event`, and `union` |
| `null` as absence in domain data | `Option<T>` with `Some(...)` and `None` |
| Exceptions for expected lookup or validation failure | `Result<T, E>` with `Ok(...)` and `Error(...)` |
| `enum` plus nullable detail fields | `union` cases with typed payloads |
| `switch` plus type/null checks spread across methods | `match` expressions over values and patterns |
| Mutable locals by convention unless avoided | `let` by default, `var` when mutation is intended |
| `void` methods | `()` (`unit`) return values |

The [Raven for C# Developers](docs/raven-for-csharp-developers.md) guide develops
these comparisons with side-by-side examples. The [Getting
Started](docs/getting-started.md) walkthrough uses a C#-style shipment quote
problem to show the differences in running Raven code.

## Repository Layout

```text
src/
  Raven.CodeAnalysis/         Compiler core: syntax, binding, semantic model, emit
  Raven.Compiler/             rvnc compiler driver
  Raven/                      rvn developer/project command frontend
  Raven.Core/                 Raven core library
  Raven.LanguageServer/       Language server implementation

test/
  Raven.CodeAnalysis.Tests/   Compiler unit tests
  Raven.Core.Tests/           Core library tests

samples/                      Runnable Raven files and project samples
tools/                        Syntax, bound node, operation, and diagnostic generators
docs/                         Language, compiler, and contributor documentation
```

`test/Raven.CodeAnalysis.Samples.Tests` is a legacy sample-test project and is
not part of the normal test focus.

## Prerequisites

- A .NET 11 SDK. The distributed toolchain is hosted on .NET 11; it can still
  target net10.0 when the corresponding targeting packs are installed.
- A shell that can run the repository scripts.

The documentation build restores its pinned DocFX tool automatically; see
[`docs/docfx.md`](docs/docfx.md).

Use a project-local `global.json` to pin the exact .NET SDK feature band for a
repository when reproducible SDK selection is required.

The Raven sample projects use the bare `Sdk="Raven.Sdk"` form. The repository
root selects `Raven.Sdk` version `0.1.10` centrally through
`global.json`. A fresh checkout therefore needs no globally installed Raven
SDK: restore uses the local `artifacts/packages` feed when populated and falls
back to NuGet.org. Before that version is public, populate the local feed with:

```bash
scripts/package-nuget.sh 0.1.10
```

## Quick Start

Build the compiler and generated sources:

```bash
scripts/codex-build.sh
```

Compile and run a Raven sample:

```bash
dotnet run -f net10.0 --project src/Raven.Compiler --property WarningLevel=0 -- \
  samples/cases/quote-summary-linq-result-option.rav -o /tmp/raven-sample.dll
dotnet /tmp/raven-sample.dll
```

Inspect compiler views for the same file:

```bash
dotnet run -f net10.0 --project src/Raven --property WarningLevel=0 -- \
  dev syntax samples/cases/quote-summary-linq-result-option.rav

dotnet run -f net10.0 --project src/Raven --property WarningLevel=0 -- \
  dev bound-tree samples/cases/quote-summary-linq-result-option.rav
```

For a guided walkthrough, including a first `hello.rav` file and project
scaffolding, see [Getting Started](docs/getting-started.md).

## NuGet Packages

Raven publishes its compiler, libraries, project SDK, and templates as one
versioned package family.

| Package | Current version | Purpose |
| --- | --- | --- |
| [Raven.CodeAnalysis](https://www.nuget.org/packages/Raven.CodeAnalysis) | [![NuGet](https://img.shields.io/nuget/v/Raven.CodeAnalysis.svg)](https://www.nuget.org/packages/Raven.CodeAnalysis) | Compiler services and public analysis APIs |
| [Raven.Analyzers](https://www.nuget.org/packages/Raven.Analyzers) | [![NuGet](https://img.shields.io/nuget/v/Raven.Analyzers.svg)](https://www.nuget.org/packages/Raven.Analyzers) | Raven compiler analyzers |
| [Raven.Core](https://www.nuget.org/packages/Raven.Core) | [![NuGet](https://img.shields.io/nuget/v/Raven.Core.svg)](https://www.nuget.org/packages/Raven.Core) | Core Raven library |
| [Raven.Macros](https://www.nuget.org/packages/Raven.Macros) | [![NuGet](https://img.shields.io/nuget/v/Raven.Macros.svg)](https://www.nuget.org/packages/Raven.Macros) | Standard macro library |
| [Raven.Sdk](https://www.nuget.org/packages/Raven.Sdk) | [![NuGet](https://img.shields.io/nuget/v/Raven.Sdk.svg)](https://www.nuget.org/packages/Raven.Sdk) | MSBuild project SDK |
| [Raven.Templates](https://www.nuget.org/packages/Raven.Templates) | [![NuGet](https://img.shields.io/nuget/v/Raven.Templates.svg)](https://www.nuget.org/packages/Raven.Templates) | `dotnet new` project templates |

## Using `rvn` and `rvnc`

You can run tools explicitly through `dotnet run`:

```bash
dotnet run -f net10.0 --project src/Raven -- dev syntax path/to/file.rav
dotnet run -f net10.0 --project src/Raven.Compiler -- path/to/file.rav -o /tmp/app.dll
```

Build the complete repository development toolchain once, then open a child
terminal that uses it instead of the SDK installed under `~/.raven`:

```bash
scripts/build-development-environment.sh
scripts/development-shell.sh

rvn dev syntax path/to/file.rav
rvnc path/to/file.rav -o /tmp/app.dll
```

Exiting the child shell restores the ordinary installed environment. Advanced
shell workflows can source `scripts/raven-env.sh` directly and later run
`deactivate-raven`.

Two VS Code launchers use the same repository environment:

```bash
# Installed extension, repository SDK/compiler/language server.
scripts/code-development.sh .

# Repository extension build in an isolated Extension Development Host.
scripts/code-extension-development.sh .
```

Project commands use the SDK workflow:

```bash
dotnet new install Raven.Templates@VERSION
dotnet new raven-console --name HelloRaven
cd HelloRaven
dotnet run
```

The template pins the matching NuGet-resolved Raven application-model SDK; no
compiler or MSBuild paths need to be configured. General applications use
`Raven.Sdk`, while projects created with `raven-web` use `Raven.Sdk.Web` to get
the normal .NET Web SDK presets through the Raven compiler. The installed
`rvn init`, `rvn build`, and `rvn run` commands remain available for the
standalone SDK workflow.

For a framework-free browser app compiled to .NET WebAssembly, install the
standard `wasm-tools` workload and use `dotnet new raven-browser` or
`rvn init browser`. See [Browser WebAssembly applications](docs/compiler/browser-wasm.md)
and the [runnable sample](samples/projects/browser-wasm/README.md).

For a small program or learning exercise, run one source file without creating
a project:

```bash
rvn run samples/scripts/hello.rvn -- Raven
rvn samples/scripts/hello.rvn Raven
```

Arguments after `--` are passed to the application.

Equivalent .NET SDK commands work for `.rvnproj` applications:

```bash
dotnet build path/to/App.rvnproj
dotnet run --project path/to/App.rvnproj
```

## Compiler Driver

Direct compiler invocation:

```bash
dotnet run --project src/Raven.Compiler -- <path-to-file> -o <output-file-path>
```

Common options:

- `--framework <tfm>` - target framework.
- `--refs <path>` - additional metadata reference; repeatable.
- `--raven-core <path>` - reference a specific `Raven.Core.dll`.
- `--emit-core-types-only` - embed Raven core shims instead of referencing
  `Raven.Core.dll`.
- `--no-emit` - analyze only.
- `--highlight` - print diagnostics with highlighted source snippets.
- `-o <path>` - output assembly path.
- `-h`, `--help` - show help.

Creating a `.debug/` directory in the current or a parent folder causes the
compiler to emit per-file dumps such as syntax tree, highlighted syntax, raw
source, bound tree, and binder tree into that directory.

`rvn dev` provides console debug views including `syntax`, `dump`, `bound-tree`,
`symbols`, and `quote`.

## Editor Support

The Raven VS Code extension supports F5 compile-and-debug for active `.rav`
files and `.rvnproj` projects. Repository launch presets live in
[.vscode/launch.json](.vscode/launch.json):

- `Raven: Compile and Debug (active file)`
- `Raven: Compile and Debug (project)`

The debug flow compiles through `Raven.Compiler` into `.raven-debug`, then
launches `dotnet <output.dll>` under the debugger. See
[Raven VS Code extension docs](docs/compiler/raven-vscode-extension.md) for
settings such as `raven.sdkPath`, `raven.compilerProjectPath`,
`raven.languageServerPath`, and `raven.targetFramework`.

## Development Notes

- Generated syntax files live under `Syntax/generated/` and
  `Syntax/InternalSyntax/generated/`; do not edit them by hand.
- Generator-affecting changes require `scripts/codex-build.sh`.
- For focused compiler work, use
  [docs/testing/test-impact-map.md](docs/testing/test-impact-map.md) to choose a
  targeted build and test baseline.
- Format touched code files with `dotnet format whitespace ... --include ...`.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for coding
standards, git conventions, and workflow details.
