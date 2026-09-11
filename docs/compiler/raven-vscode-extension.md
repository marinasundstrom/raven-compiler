# Raven VS Code Extension

The Raven VS Code extension wires the editor to the `Raven.LanguageServer` LSP process so `.rvn` files, with legacy `.rav` compatibility, can surface diagnostics, completions, and inlay hints. The language server publishes syntax diagnostics immediately after edits and keeps previous semantic diagnostics and inlays visible for unchanged ranges while newer snapshot results are pending. It auto-discovers the language server build output and starts it with `dotnet` when the extension activates.

Accepting identifier completion preserves surrounding indentation and comments.
When editing inside an identifier, suggestions use the prefix before the caret
and acceptance replaces the entire identifier. Method completion adds parentheses
and places the caret inside them when starting a new call. If an argument list
already exists, completion preserves it and leaves the caret after the method name.
On an indented blank line, completion offers visible values and statement keywords.
A completed member expression on the previous line does not restrict these
suggestions; an unfinished access such as `Console.` still offers members when
continued on the next line.
Ordinary code suggestions are suppressed inside comments and literal text,
including unfinished strings. Embedded expressions such as `${Console.}` retain
code completion. Parentheses, quotes, and newlines are not automatic completion
trigger characters.

The Explorer also contains an opt-in **Raven Syntax Tree** debugging view. It
renders nodes, tokens, trivia, syntax property roles, raw kinds, spans, missing
elements, and diagnostics from the machine-readable `rvn dev syntax json`
output. The toolbar switches between the authored syntax tree and the fully
macro-expanded tree, and can open the complete expanded source in a read-only
virtual Raven document. Tree selections reveal the corresponding authored or
expanded source range.

The view is always available from the Explorer's Views menu. Run
**Raven: Show Authored Syntax Tree** or **Raven: Show Expanded Syntax Tree**
from the Command Palette to open Explorer, focus the view, and select its mode.

## Embedded macro language tooling

When a token-tree macro exposes an `IMacroEmbeddedLanguageProvider` projection,
the extension can reuse an installed VS Code language provider without copying
that language's catalog into Raven. For `markup!`, the language server returns
the position-preserving `html` projection that owns the cursor. The extension
opens it as an invisible virtual HTML document, invokes VS Code's standard HTML
completion and hover providers, and maps their ranges back to the Raven body.
Completion items are merged after Raven's semantic completions; hover content
is merged after Raven's semantic hover when both providers contribute.

| Cursor location | Raven contribution | Projected HTML contribution |
| --- | --- | --- |
| Standard element or attribute | Macro classifications and structural diagnostics | Catalog completion and HTML documentation hover |
| Blazor component tag or parameter | Symbol hover, definition, and compiler-backed component completion | HTML results may supplement but do not replace Raven symbols |
| Embedded `{ RavenExpression }` | Native Raven diagnostics, completion, hover, definition, and inlays | None; reported Raven fragments take precedence over the projection |
| Nested `markup!` in `component!` | Component parameters and surrounding lexical scope remain visible | The nested position-preserving HTML document supplies completion and hover |

This gives standard HTML elements, attributes, and closing-tag suggestions in
`markup!`, including a nested invocation inside a `component!` block. Blazor
component tags and `[Parameter]` properties still come from Raven's compiler
completion provider. Embedded Raven expressions are masked in the HTML view and
continue to use native Raven tooling. The bridge currently covers completion
and hover. HTML formatting, linked editing, and diagnostics are later slices.

## Prerequisites
- .NET SDK available on your `PATH` so the client can start the language server.
- The Raven SDK for build, run, and debug commands. The packaged extension can
  provide editor features from its bundled language server without the SDK.
- Node.js 18+ only when building the extension from source.

## Installing the preview

Download and install the VSIX from the matching GitHub release:

```bash
curl -fLO https://github.com/marinasundstrom/raven/releases/download/v0.1.11/raven-vscode.vsix
code --install-extension raven-vscode.vsix --force
```

Restart VS Code after installing. If VS Code cannot discover `rvn`, set
`raven.sdkPath` to the versioned directory printed by `rvn sdk path`.

## Building the extension
Install dependencies and compile the client bundle from the repository root:

```bash
cd src/Raven.VSCode
npm install
npm run compile
```

The production JavaScript bundle emits to `dist/extension.js` and is referenced
by the extension manifest.

## Running inside VS Code

The repository provides three deliberately separate development environments:

```bash
# Child terminal using the repository SDK and commands.
scripts/development-shell.sh

# Installed extension using repository SDK/compiler/language-server paths.
scripts/code-development.sh .

# Build and run the repository extension in an isolated Extension Development Host.
scripts/code-extension-development.sh .
```

The extension-development launcher runs
`scripts/build-development-environment.sh` first, so it does not depend on a
previous terminal or VS Code development session. Pass `--no-build` only when
the repository outputs are already current. From an existing repository
window, select **Raven: Test Repository Extension** in Run and Debug. That
configuration performs the same complete build and supplies the repository SDK,
compiler, and language-server paths to its Extension Development Host.

## Configuration
The extension exposes settings to control language-server resolution and debug compilation:
- `raven.sdkPath` (string): Override the Raven SDK root directory. An explicit path selects that SDK's language server and command-line tools. When this is unset, the extension runs `rvn sdk path` to discover an installed SDK for build, run, and debug commands, but keeps using its matching bundled language server for editor features.
- `raven.languageServerPath` (string): Override the resolved `Raven.LanguageServer.dll` path. Use this when working with custom build outputs or packaged bits.
- `raven.autoBuildLanguageServerOnActivate` (boolean): Opt-in source-development setting that builds `src/Raven.LanguageServer/Raven.LanguageServer.csproj` before activation if it can find the project in the current workspace or extension ancestors. It can substantially delay startup and is ignored when `raven.languageServerPath` is set.
- `raven.compilerProjectPath` (string): Override the path used to locate a prebuilt `rvnc.dll` under `src/Raven.Compiler/bin/Debug/<tfm>` when no bundled compiler driver is available.
- `raven.targetFramework` (string): Optional target framework (for example, `net10.0`) passed to debug compile invocations.
- `raven.inlayHints.enabled` (boolean): Master switch for Raven inlay hints.
- `raven.inlayHints.inferredTypes.enabled` (boolean): Show inferred type annotation hints when Raven inlay hints are enabled.
- `raven.inlayHints.names.enabled` (boolean): Show name hints for positional invocation arguments and deconstruction elements when Raven inlay hints are enabled.
- `raven.inlayHints.requestDebounceMilliseconds` (number): Delay inlay requests after document edits so typing can settle before semantic inlay work runs.

The repository launchers set `RAVEN_SDK_ROOT` and
`RAVEN_LANGUAGE_SERVER_PATH` for the child VS Code process. The extension uses
those paths when the corresponding explicit setting is absent. This keeps the
development selection scoped to that process; it does not modify user settings
or the installed Raven SDK.

When the extension discovers a workspace-built language server, it stages that build into an extension-owned directory before launch. The staged copy runs with the repository root as its working directory so repo-relative assets like `Raven.Core.dll` continue to resolve while the workspace build outputs remain free of language-server file locks.

### Toolchain provenance

Use **Raven: Show Toolchain Information** to make the active toolchain explicit.
The command opens the Raven output channel and reports:

- whether VS Code loaded a repository development extension or an installed
  extension, including its version and path;
- whether the language server came from an explicit override, an explicit SDK,
  a repository build, the installed extension bundle, or the discovered SDK
  fallback, including its exact path;
- the discovered or explicitly configured SDK version and path used by Raven
  commands; and
- the `Raven.Sdk` version selected by the nearest `global.json` for every
  workspace folder.

These identities need not be the same. In the normal installed configuration,
editor features come from the server bundled with the installed VSIX, Raven
commands use the installed SDK, and `dotnet build` resolves the project SDK
selected by `global.json`. A repository extension host instead prefers the
repository-built language server so local compiler and language-service changes
are visible.

## F5 compile + debug
The extension contributes a `Raven` debug type:
- `Raven: Compile and Debug` compiles the active `.rvn` file or `.rvnproj` target using the `rvnc` compiler driver. Legacy `.rav` source files remain supported for compatibility.
- Build artifacts are emitted to `${workspaceFolder}/.raven-debug`.
- After compile succeeds, the extension starts a `coreclr` debug session with `dotnet <compiled-output.dll>`.

You can start it by pressing F5 in a Raven file, or by running `Raven: Compile and Debug Active File` from the command palette.

## Running a file or project

**Raven: Run Active File/Project** uses the `rvn` frontend. Selecting a `.rvn`
or legacy `.rav` source that belongs to a Raven project runs its owning project
through `rvn run`; selecting a `.rvnproj` file also runs that project. A source
without an owning project runs as an isolated file-based application. The
command opens an interactive terminal rooted beside the resolved project or
source file so application input and output behave like an ordinary script or
console application.

Language features follow evaluated project membership. A source included by a
project receives that project's semantic context; a source outside project
items receives an isolated file-application context even when a project file
exists elsewhere in the workspace.

## Packaging
`scripts/package-vscode.sh` publishes a framework-dependent language server into a `server/` directory next to `package.json` and creates the VSIX. The compiler remains in the separately installed Raven SDK. A direct `raven.languageServerPath` override has highest precedence, followed by an explicit `raven.sdkPath`, workspace development builds, and the server packaged with the extension. A server from an automatically discovered SDK is only a final fallback when no matching packaged or workspace server is available. This prevents a newly installed extension from silently running an older SDK's language server.

When no SDK is discovered, the bundled server still provides editor features
and the extension offers a link to the SDK installation instructions. The
prompt can be permanently dismissed for syntax-only installations.
