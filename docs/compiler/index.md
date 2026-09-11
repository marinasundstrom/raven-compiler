# Raven tools

This section explains the tools needed to create, compile, run, and edit Raven
programs. The `rvn` command manages projects and developer workflows, while the
`rvnc` compiler driver compiles `.rav` source files for .NET. The toolchain also
ships the [Raven Core Library](raven-core-library.md), which is referenced by
default.

Start with:

- [Compiler and command-line tools](raven-compiler.md)
- [Target platforms](target-platforms.md)
- [Project system](project-system.md)
- [Extend a project](extending-projects.md)
- [VS Code extension](raven-vscode-extension.md)
- [Diagnostics](diagnostics.md)
- [Built-in analyzers](analyzers/built-in.md)
- [Analyzer configuration](analyzers/configuration.md)
- [JSON serialization](json-serialization.md)

## Compiler services

`Raven.CodeAnalysis` exposes syntax trees, symbols, semantic models, and
compilation APIs. These services power diagnostics and editor features, and
can also be used by tools that inspect Raven programs.

- [Built-in analyzers](analyzers/built-in.md) and [analyzer configuration](analyzers/configuration.md)
  explain the analysis available in ordinary projects.
- [Source generators](source-generators.md) and [project extensions](extending-projects.md)
  cover extending compilation.
- [Compiler architecture](https://github.com/marinasundstrom/raven/blob/main/docs/compiler/architecture/live-semantic-model.md)
  describes syntax and semantic services for tool authors.
- [Compiler source and APIs](https://github.com/marinasundstrom/raven/tree/main/src/Raven.CodeAnalysis)
  provide the implementation and public API definitions.

Compiler APIs are in preview and may change between releases. For source
builds and contributions, use the
[contributor guide](https://github.com/marinasundstrom/raven/blob/main/CONTRIBUTING.md).
