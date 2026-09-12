# Release status and compatibility

Raven is a working language and toolchain in preview. Releases include an SDK,
project templates, a VS Code extension, and compiler libraries. Syntax and
compiler APIs can still change between releases.

## Install a release

[Release 0.1.12](https://github.com/marinasundstrom/raven/releases/tag/v0.1.12)
provides installers and release notes. Follow [Install and run Raven](getting-started.md)
for the first-run path.

| Component | Current release |
| --- | --- |
| SDK host | Requires the .NET 11 SDK |
| Application targets | .NET 11 and .NET 10, with the matching targeting packs |
| Editor | VS Code extension distributed with the release |
| Libraries and projects | NuGet packages, MSBuild SDKs, and project templates |
| Browser projects | .NET 10 WebAssembly toolchain; requires `wasm-tools` |

See [Target platforms](compiler/target-platforms.md) for platform-specific requirements.
Supporting a target does not imply every .NET workload has the same level of validation.

## Release documentation and upcoming changes

The page footer identifies the documentation build and source revision. Match
examples to the compiler version you are using. The
[release notes](https://github.com/marinasundstrom/raven/releases) describe
published versions; the [changelog on main](https://github.com/marinasundstrom/raven/blob/main/CHANGELOG.md)
also includes unreleased work.

These pages target Raven **0.1.12**, including the SDK and browser playground.
The build footer marks this version as unreleased until its release tag exists.
Use the matching SDK when running the tour examples locally: 0.1.12 fixes
compound match guards and comparison patterns with variable operands.

Raven 0.1.12 removes `try?`. Use `(try expression)?`, which composes exception
capture with ordinary propagation. Each postfix `?` propagates one carrier
layer. The older spelling remains accepted by the 0.1.11 compiler.

## Areas to evaluate separately

- **Component macros:** Blazor component and markup macros remain experimental.
  See the [component showcase](showcases/html-components.md) for scope and examples.
- **Embedded targets:** .NET nanoFramework has its own runtime and API surface.
  See [embedded IoT](workloads/embedded-iot.md) before choosing a board or workload.
- **C# interoperability:** Raven uses ordinary CLR types and metadata, but some
  source semantics intentionally differ. See [Raven for C# developers](raven-for-csharp-developers.md)
  and [nullability](lang/nullability.md).

Use the [roadmap](roadmap.md) for development priorities and
[GitHub issues](https://github.com/marinasundstrom/raven/issues) to report a
reproducible compiler or tooling problem.
