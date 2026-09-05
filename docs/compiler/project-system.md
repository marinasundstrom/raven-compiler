# Raven Project System

Raven supports compiling either individual `.rvn` files (with legacy `.rav` compatibility) or a project file (`.rvnproj`).

You can scaffold a project in the current directory with:

```bash
rvn init
```

## Project file format

`*.rvnproj` is now a real MSBuild project file. The primary format matches SDK-style `.csproj` structure and relies on evaluated MSBuild properties/items rather than Raven-specific XML attributes.

Primary MSBuild properties Raven currently consumes:

- `TargetFramework`
- `TargetFrameworks` (the active inner-build TFM is honored; standalone
  workspace evaluation uses the first TFM when no target is requested)
- `AssemblyName`
- `OutputType` (`Exe` or `Library`)
- `AllowUnsafeBlocks` or `AllowUnsafe`
- `AllowGlobalStatements` or `RavenAllowGlobalStatements`
- `DefineConstants` (conditional-compilation symbols separated by semicolons,
  commas, or whitespace)
- `FrameworkProjections` or `RavenFrameworkProjections` (`Standard` by default,
  or `None` for the ordinary .NET API surface)
- `EnableIsNotNullNarrowing` (`false` by default; enables direct
  `value is not null` true-branch narrowing as a compatibility feature)
- `IntermediateOutputPath`
- `Configuration`
- `RavenGenerateDocumentation` (`true` by default for libraries)
- `GenerateDocumentationFile`
- `GenerateMarkdownDocumentationFile`
- `GenerateXmlDocumentationFromMarkdownComments`
- `DocumentationFile`
- `MarkdownDocumentationOutputPath`

Library projects emit both Raven Markdown sidecars and compatible .NET XML
documentation by default. Raven-authored comments are Markdown unless the XML
format is explicitly selected. Set `RavenGenerateDocumentation` to `false` to
disable the default bundle, or override the individual properties to select one
projection. When consuming metadata, Raven prefers the Markdown sidecar and
falls back to adjacent XML documentation.

Implementation details are available in the repository's
[Raven Documentation Model](https://github.com/marinasundstrom/raven/blob/main/docs/compiler/design/raven-documentation-model.md)
and
[External Documentation Sidecars](https://github.com/marinasundstrom/raven/blob/main/docs/compiler/design/external-documentation-sidecars.md)
design notes.

Primary MSBuild items Raven currently consumes:

- `<Compile Include="..."/>` when default compile items are disabled or sources
  live outside the project directory
- `<ProjectReference Include="..."/>`
- `<Reference Include="...">` with `HintPath`
- `<PackageReference Include="Package.Id" Version="x.y.z"/>`
- `<FrameworkReference Include="Framework.Name"/>`

When the compiler builds a referenced Raven compiler-plugin project, it passes
the same compiler-support references used by the top-level compilation into the
nested project build. This lets a freshly restored macro project use
`Raven.CodeAnalysis` and `Raven.Macros` without copying compiler installation
paths into its project file. These references are compiler-provided build inputs;
ordinary application and library dependencies continue to come from standard
`PackageReference`, `ProjectReference`, and `Reference` items.

## `.editorconfig` diagnostic severity support

Raven reads `.editorconfig` files when compiling project and source files and applies
diagnostic severity overrides from:

- `dotnet_diagnostic.<ID>.severity`
- `dotnet_diagnostic.*.severity`
- `dotnet_analyzer_diagnostic.severity`

Supported severity values:

- `none`/`suppress` -> suppressed
- `silent`/`hidden` -> hidden
- `suggestion`/`info` -> info
- `warning`/`warn` -> warning
- `error` -> error
- `default` -> default severity

Example:

```ini
root = true

[*.rvn]
dotnet_diagnostic.RAV9012.severity = none
dotnet_diagnostic.RAV9013.severity = none
dotnet_diagnostic.RAV9014.severity = none
```

## `.editorconfig` generated-code support

Raven also reads the standard per-file `generated_code` key:

```ini
[generated/**/*.rvn]
generated_code = true

[generated/Editable.g.rvn]
generated_code = false
```

An explicit value overrides generated filename and header conventions for the
matching file. Normal `.editorconfig` precedence applies: files closer to the
source file override parent files, and later matching sections override earlier
sections. Invalid values are ignored. The language server watches `.editorconfig`
and invalidates analyzer results when this classification changes without
reloading the project.

## Raven source inclusion

Raven projects implicitly include `**/*.rvn`, excluding the SDK's normal
default-item exclusions such as `bin`, `obj`, and hidden directories. Like C#,
ordinary source files do not need to be listed in the project file.

Minimal example:

```xml
<Project Sdk="Raven.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>App</AssemblyName>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
```

Custom MSBuild SDK versions can be selected once for a repository in
`global.json`, keeping individual Raven projects concise:

```json
{
  "msbuild-sdks": {
    "Raven.Sdk": "VERSION"
  }
}
```

The NuGet-based MSBuild SDK resolver restores that version from the configured
package sources. A standalone project can instead pin the version directly as
`Sdk="Raven.Sdk/VERSION"`; use one form consistently within a repository.

Set the standard `EnableDefaultCompileItems` property to `false` when the
project needs an explicit source list:

```xml
<PropertyGroup>
  <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
</PropertyGroup>

<ItemGroup>
  <Compile Include="src/Main.rvn" />
</ItemGroup>
```

Legacy `.rav` files are not implicitly included and must remain explicit while
that extension is supported.

Conditional-compilation symbols use the standard MSBuild property:

```xml
<PropertyGroup>
  <DefineConstants>DEBUG;TRACE</DefineConstants>
</PropertyGroup>
```

The evaluated value is passed into every syntax tree in the project, including
trees used by the language server. Changing the value causes affected documents
to be reparsed so editor diagnostics and inactive-code highlighting remain
consistent with builds.

Raven project files use the `.rvnproj` extension and the MSBuild-backed project shape.

## Application-model SDKs

The project SDK selects the application's build model and its presets:

| Application model | Project SDK | Underlying .NET SDK |
| --- | --- | --- |
| Console, library, and general-purpose applications | `Raven.Sdk` | `Microsoft.NET.Sdk` |
| ASP.NET Core and Web applications | `Raven.Sdk.Web` | `Microsoft.NET.Sdk.Web` |

ASP.NET Core projects must select `Raven.Sdk.Web`; it supplies the Web SDK's
framework, build and publish behavior, static-web-assets integration, and
implicit imports through the Raven compiler pipeline. The `TargetFramework`
property independently selects the runtime/platform. For example,
`netnano1.0` remains a target-framework choice under `Raven.Sdk`, not another
application-model SDK.

## Generated prelude imports

Raven projects generate a `<ProjectName>.Prelude.g.rvn` source file by default.
`Raven.Sdk` supplies the common `System` namespaces plus `System.Result.*` and
`System.Option.*` as evaluated MSBuild `Import` items. Other SDKs selected by
the project's `Sdk="..."` attribute can contribute additional items, so the
generated prelude follows the selected application model without compiler-side
SDK-name checks. `Raven.Sdk.Web` composes `Microsoft.NET.Sdk.Web` and contributes
the same ASP.NET Core and `Microsoft.Extensions` namespaces that the .NET Web
SDK makes implicit for C#.

Global imports are hoisted across the compilation, but they still use ordinary
import binding rules. Namespace imports are the most robust project-file import
shape because the namespace only has to exist after references and project
declarations are known. Type-scope imports such as `System.Result.*` and direct
nested-case imports such as `System.Result.Ok` require the imported type or
nested type to be available to the compilation. They are supported, but they are
less flexible than namespace imports and should normally be reserved for stable
library/prelude cases; user-defined union cases are usually clearer as qualified
or target-typed `.Case` references.

Set `ImplicitImports` to `disable` to disable SDK-supplied imports:

```xml
<PropertyGroup>
  <ImplicitImports>disable</ImplicitImports>
</PropertyGroup>
```

As with the .NET SDK's `ImplicitUsings` property, `true` and `enable` turn the
feature on, while `false` and `disable` turn it off. `ImplicitUsings`,
`GeneratePreludeImports`, and `RavenGeneratePreludeImports` remain compatibility
aliases when `ImplicitImports` is not set.

Projects and composed SDKs add prelude imports with `Import` items:

```xml
<ItemGroup>
  <Import Include="SuperheroApp.Models" />
  <Import Include="System.Console" Static="True" />
  <Import Include="System.DateTime" Alias="DT" />
</ItemGroup>
```

Non-aliased items generate global wildcard imports. `Static="True"` is intended
for type-scope imports such as `System.Console.*`. `Alias` generates a
project-wide alias in the prelude. Standard MSBuild item operations allow a
project to remove or update imports supplied earlier by its SDK:

```xml
<ItemGroup>
  <Import Remove="System.Net.Http" />
</ItemGroup>
```

Inside an `ItemGroup`, `Import` is an item name. It is distinct from MSBuild's
project-level `<Import Project="..." />` element. If a source file repeats an
import that is already supplied by a global import, the compiler reports a
hidden redundant import diagnostic and editors can offer a remove-import fix.

## NuGet package references

When a `.rvnproj` includes `<PackageReference>`:

1. Raven first resolves package assemblies from the global NuGet cache:
   - `$NUGET_PACKAGES` when set
   - otherwise `~/.nuget/packages`
2. If required assets are missing, Raven runs `dotnet restore` for a temporary SDK project.
3. Raven reads resolved compile assets and adds those assemblies as metadata references.

When a `.rvnproj` includes `<FrameworkReference>`:

1. Raven restores a temporary SDK project that contains those framework references.
2. Raven resolves the corresponding framework reference packs from installed .NET SDK `packs/`.
3. Pack reference assemblies are added as metadata references for compilation.

### Alternative managed runtimes

An SDK-style Raven project can target a managed runtime whose core library is
not installed as a host .NET targeting pack. The nanoFramework MVP uses
`netnano1.0`, normal `PackageReference` items, and these compiler-facing
properties:

- `RavenUseHostFrameworkReferences=false` prevents Raven from adding the host
  .NET reference closure;
- `RavenTargetCoreLibraryPath` selects the alternative core-library identity
  used during emission; and
- `RavenEmitCoreTypesOnly=true` prevents a desktop Raven.Core reference from
  entering the target closure.

MSBuild still owns restore and reference selection. Its evaluated
`ReferencePath` is passed to `rvnc`, and the workspace/language server reads the
same project properties and explicit package assets. `Raven.Sdk` recognizes
`netnano1.0` and imports
`Raven.nanoFramework.props`, which supplies the target identity, core-library
package, metadata processor, and reduced-runtime compiler defaults missing from
the stock SDK. Application projects remain standard SDK-style projects and
contain only their target framework, device package references, and application
settings. nanoFramework is a target-framework/runtime selection, not a separate
application-model SDK; its target-specific behavior remains driven by the TFM
and imported target profile.

See [Getting started with .NET nanoFramework](nanoframework.md) for the build outputs,
direct `nanoff` deployment commands, and current VS Code debugger integration.

## Project extensions

A Raven project can load compiled extension assemblies:

```xml
<ItemGroup>
  <Analyzer Include="extensions/MyProjectRules.dll" />
  <SourceGenerator Include="extensions/MyProjectGenerators.dll" />
</ItemGroup>
```

- `Analyzer` assemblies contribute custom diagnostics after generators and
  normal compiler binding.
- `SourceGenerator` assemblies contribute additional Raven syntax trees before
  binding and analyzer execution.

Both paths are resolved relative to the project file. An extension assembly may
contain multiple public, non-abstract extension types with parameterless
constructors.

A direct `PackageReference` can provide the same analyzer assembly through
NuGet's conventional `analyzers/dotnet` path. Raven reads those assets from the
existing `project.assets.json` during `dotnet build --no-project-restore`, so it
does not perform a nested restore. Analyzer assets from unrelated transitive
compiler dependencies are not loaded as Raven analyzers.

When the extension is built alongside the Raven project, use a
`ProjectReference` to establish build ordering without adding the extension as
an application metadata reference:

```xml
<ItemGroup>
  <ProjectReference
    Include="extension/MyExtensions.csproj"
    ReferenceOutputAssembly="false" />

  <Analyzer
    Include="extension/bin/$(Configuration)/$(TargetFramework)/MyExtensions.dll" />
</ItemGroup>
```

See [Extend a Raven project](extending-projects.md) for authoring guidance and
runnable analyzer and generator samples.

## Build vs publish outputs

Raven project builds use the standard .NET output layout:

- Normal build (`dotnet build App.rvnproj`)
  - default output directory: `<project-dir>/bin/<Configuration>`
  - emits apphost + `.dll` + `.runtimeconfig.json` for console apps
  - copies the dependency assemblies selected as copy-local by MSBuild
- Publish (`dotnet publish App.rvnproj`)
  - default output directory: `<project-dir>/bin/<Configuration>/publish`
  - copies runtime dependencies (NuGet/framework/local assemblies) to output
  - emits runtime artifacts (`.runtimeconfig.json`, apphost)

The standard SDK publish pipeline can also create an OCI container image
without a Dockerfile. See [Containerize a Raven
application](../workloads/containerization.md) for local-runtime, archive, and
registry workflows.

Dependency copy details:

- If a compile reference comes from `ref/`, Raven prefers the runtime assembly
  under `lib/`.
- For each copy-local assembly, adjacent `<AssemblyName>.xml` and
  `<AssemblyName>.docs/` sidecars are copied beside the assembly when present.
  The Markdown directory structure is preserved so compiler and editor symbol
  lookup can use its manifest.
- `dotnet pack` includes a Raven library's generated XML and Markdown sidecars
  under the same `lib/<tfm>/` directory as its assembly.

## Generated intermediate sources

For project builds, Raven can generate intermediate Raven source files under:

- `<project-dir>/obj/<Configuration>/<TargetFramework>/raven/generated/`

Current generated source:

- `<ProjectName>.TargetFrameworkAttribute.g.rvn` containing:

```rav
import System.Runtime.Versioning.*

[assembly: TargetFramework(".NETCoreApp,Version=vX.Y")]
```

Generation rules:

- Emitted when `TargetFramework` is set on `.rvnproj`.
- Skipped if user source already declares assembly-level `TargetFrameworkAttribute`.

## CLI usage

Compile a project file:

```bash
dotnet run --project src/Raven.Compiler --property WarningLevel=0 -- path/to/App.rvnproj
```

Use `dotnet build` and `dotnet run --project` for normal application build and
run workflows.

Use `-o` with `rvnc` to override the output directory:

```bash
dotnet run --project src/Raven.Compiler --property WarningLevel=0 -- path/to/App.rvnproj -o path/to/out
```

Sample:

- `samples/projects/nuget-demo/README.md`
- `samples/projects/raven-msbuild-integration/README.md`
- `samples/projects/runtime-async-net11/README.md`

## Runtime async for `net11.0`

If a `.rvnproj` sets `<TargetFramework>net11.0</TargetFramework>` (or newer), Raven enables runtime-async mode by default. Raven verifies support from the target framework's corlib, following Roslyn's capability-based model rather than inspecting the compiler host framework.

- Async methods emit with runtime async metadata.
- Await expressions emit `System.Runtime.CompilerServices.AsyncHelpers.Await(...)` calls when available.
- State-machine type synthesis is skipped.

The distributed compiler host targets .NET 11. When invoking the compiler driver from source through `dotnet run`, select its `net11.0` target:

```bash
dotnet run -f net11.0 --project src/Raven.Compiler --property WarningLevel=0 -- path/to/App.rvnproj
```

When invoking a `net11.0` `.rvnproj` through `dotnet build` or
`dotnet run --project`, the selected .NET SDK must also support `net11.0`. Use a
project-local `global.json` to pin SDK 11 when a machine has multiple SDK bands
installed.

You can still override behavior explicitly:

- `--runtime-async` to force on.
- `--no-runtime-async` to force off.

For `dotnet build`, use the corresponding project property:

```xml
<UseRuntimeAsync>false</UseRuntimeAsync>
```

Leaving the property unset preserves the default: enabled for a capable
`net11.0` target and classic state-machine lowering for `net10.0`. Explicitly
enabling it for a target without the runtime contract fails compilation.

## MSBuild build integration

`.rvnproj` files can build through the normal .NET SDK pipeline when MSBuild is
wired to Raven's language targets:

- `build/Raven.MSBuild.props` sets `.rvnproj` `LanguageTargets` to Raven's target file.
- `build/Raven.Language.targets` imports the common managed build targets and
  implements Raven's `CoreCompile`.
- The Raven compile writes the SDK intermediate assembly, copies it to the SDK
  reference-assembly slot when requested, and lets the normal SDK output pipeline
  copy files to `bin/<Configuration>/<TargetFramework>/`.
- MSBuild-resolved `ReferencePath` items are passed to `rvnc`; package restore
  and framework-reference resolution remain owned by the .NET SDK rather than
  the Raven compiler core.
- The active `Configuration` and inner-build `TargetFramework` are passed to
  `rvnc` so conditional properties and items, generated-source paths, project
  references, and compiler plugins use the same MSBuild context as the outer
  build.
- Release configurations select `OptimizationLevel.Release`; an explicitly
  evaluated MSBuild `Optimize` property overrides the configuration default.
  Both Debug and Release builds retain portable PDBs, while Release omits
  debug-only IL padding and enables conservative lowering peepholes.
- `CoreCompile` tracks source files, resolved reference files, project files,
  extensions, and compiler-observed dependencies. An unchanged second build is
  skipped by MSBuild rather than invoking `rvnc` again.
- `dotnet clean` removes the compiler-owned generated-source and documentation
  directories along with the tracked assemblies, symbols, and dependency
  manifests for the active build context.

Inside this repository, `Directory.Build.props` wires `.rvnproj` files
automatically, so sample projects build directly:

```bash
dotnet build samples/projects/hello-world/HelloWorld.rvnproj --property WarningLevel=0
```

### External constants

Projects supply defaults for typed `extern const` declarations with
`RavenConstant` items:

```xml
<ItemGroup>
  <RavenConstant Include="SampleRate" Value="500" />
  <RavenConstant Include="DeviceId" Value="sensor-42" />
</ItemGroup>
```

The evaluated item values flow into the same `CompilationOptions` facility as
direct compiler and frontend command-line values. `rvn build --constant
SampleRate=250` overrides the project item for that invocation; otherwise the
project value overrides the source initializer. A required declaration without
either provider value fails compilation.

Build tooling can pass several explicit overrides through the
`RavenExternalConstantOverrides` MSBuild property as a Base64-encoded JSON
object. These overrides have the highest precedence, independent of compiler
argument order. The complete precedence is therefore:

1. explicit compiler or build-tool override;
2. evaluated `RavenConstant` project item;
3. source initializer on the `extern const` declaration.

Projects whose repository selects the published Raven SDK in `global.json`
need no compiler paths:

```xml
<Project Sdk="Raven.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>RavenGreeter</AssemblyName>
    <OutputType>Library</OutputType>
  </PropertyGroup>

</Project>
```

C# and other SDK projects can reference a Raven project with normal
`ProjectReference` once the referenced `.rvnproj` has Raven language targets:

```xml
<ItemGroup>
  <ProjectReference Include="..\raven\RavenGreeter.rvnproj" />
</ItemGroup>
```

Raven projects use the same `ProjectReference` form and build-order semantics.
During `dotnet build`, MSBuild first builds referenced projects and the Raven
compiler consumes their output assemblies as metadata, just as C# compilation
does. In an editor workspace, those same references remain source project edges
so live analysis and navigation can cross project boundaries. Each project
therefore retains its own compilation options, assembly identity, and entry
point in both representations. A source or public-API change in a referenced
workspace project invalidates dependent compilations and their diagnostic
caches transitively.

## Remaining C# project-system parity work

The `.rvnproj` authoring model now uses the same standard properties and items
as an SDK-style C# project for sources, references, target frameworks, output
type, configuration, incremental build, clean, and project references. Raven's
language-specific behavior remains opt-in through Raven properties and items.

The main remaining gap is distribution of the build integration. Projects in
this repository work because `Directory.Build.props` assigns
`Raven.Language.targets` to `LanguageTargets`. A standalone project currently
needs the explicit `LanguageTargets`/`RavenCompilerHost` setup shown above. The
next project-system rewrite should package Raven as a resolvable MSBuild SDK
with conventional `Sdk.props` and `Sdk.targets`, so a project can select Raven
without machine-specific paths and build directly with `dotnet build`.

That SDK rewrite should also own these currently reduced or custom behaviors:

- design-time build targets and IDE capability metadata beyond the current
  language/project capability declarations;
- proper reference-assembly production instead of copying the implementation
  assembly into the reference-assembly slot;
- compiler invocation through a dedicated MSBuild task or tool contract rather
  than a monolithic `Exec` command line;
- standard SDK dependency and publish item flow, replacing Raven's custom
  runtime-dependency manifest reconciliation where the normal SDK items can
  represent the same information;
- a single evaluated-project snapshot contract shared by command-line builds
  and workspace/LSP loading, avoiding duplicate project evaluation and fallback
  restore logic.

These are targets/SDK implementation concerns. They should not add source lists
or Raven-specific replacements for standard MSBuild properties back into user
project files.

## Workspace and project-system services

`RavenWorkspace` now consumes project loading/saving through host services rather than hardcoding project-file persistence logic in workspace APIs.

- `PersistenceService` delegates project open/save to `IProjectSystemService`.
- `MsBuildProjectSystemService` opens Raven projects authored as MSBuild-backed `.rvnproj` files.
- `RavenWorkspace.Create(..., projectSystemService: ...)` still allows overriding the project-system implementation explicitly.

During one project-loading lifecycle, `MsBuildProjectSystemService` reuses an
evaluated project snapshot across reference discovery, graph traversal, and
nested Raven compiler-plugin loading. Cache entries are keyed by project,
target framework, and configuration and validate the project, imported MSBuild
files, and source inputs before reuse. The cache is cleared after the graph is
opened so a later reload starts a fresh lifecycle. Hosts can inspect
`PerformanceInstrumentation.CaptureSnapshot()` on the service for evaluation
request, execution, hit, invalidation, failure, and elapsed-time measurements.

When a source compiler-plugin project changes, the language server emits a
shadow macro assembly for its consumers. Shadow outputs are keyed by the full
compiler input snapshot—including source and generated trees, parse and
compilation options, referenced-project inputs, metadata file fingerprints,
and the compiler build identity. An unchanged snapshot reuses its existing DLL
and PDB without running emit again, including after restarting the language
server.

File-backed macro references also share the loaded assembly and its discovered
exports across projects and compilation snapshots when the plugin assembly and
explicit dependency contents match. Each reference creates fresh macro
instances, and the shared export entry is weak so an unused collectible plugin
load context can be reclaimed. Replacing the plugin or any explicit dependency
selects a new load context; a failed load is never retained as a successful
cache entry.

### MSBuild-backed Raven projects

The workspace loads `.rvnproj` projects and MSBuild projects whose evaluated
language or language-target properties identify Raven. Source documents come
from the standard evaluated `Compile` item list.

Example:

```xml
<Project Sdk="Raven.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Lib\Lib.csproj" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

Current behavior:

- `RavenWorkspace.OpenProject(...)` can open that project through `MsBuildProjectSystemService`.
- The language server uses standard `.sln` and XML `.slnx` entries to group
  workspace projects when at least one listed project is a Raven project, and
  otherwise falls back to recursive project discovery. MSBuild
  `ProjectReference` remains the source of compilation dependency edges.
- The language server reloads the evaluated project when watched MSBuild
  `.props` or `.targets` files change, including `Directory.Build.props` and
  `Directory.Build.targets`.
- `TargetFramework`, `AssemblyName`, `OutputType`, `AllowUnsafe` /
  `AllowUnsafeBlocks`, and `AllowGlobalStatements` are mapped into Raven
  project state.
- `ProjectReference` paths are surfaced through the project-system abstraction so callers such as the language server can recurse without knowing the concrete project-file format.
- Referenced Raven MSBuild projects become workspace project references when they are loaded.
- Referenced non-Raven MSBuild projects are consumed as metadata references when their evaluated `TargetPath` already exists on disk.

Current behavior also includes save support for mapped Raven properties and
on-disk Raven source files while preserving unrelated MSBuild items. Explicit
source lists are persisted as standard `Compile` items when
`EnableDefaultCompileItems` is `false`.

## Scaffolding with `rvn init`

`rvn init` creates a starter layout in the current directory:

- `<ProjectName>.rvnproj`
- `src/Main.rvn` (`src/Library.rvn` for class libraries)
- `bin/.gitkeep`

The generated standalone project currently pins the matching NuGet-resolved
Raven SDK:

```xml
<Project Sdk="Raven.Sdk/VERSION">
```

`Raven.Sdk` composes the standard `Microsoft.NET.Sdk` behavior with Raven's
compiler targets and implicit lockstep Core and standard-macro packages.
`Raven.Sdk.Web` provides the same Raven contract on top of
`Microsoft.NET.Sdk.Web`. A generated project therefore uses normal
`dotnet restore`, `dotnet build`, `dotnet run`, and `dotnet publish` commands
without machine-specific MSBuild properties.

Options:

- `--name <project-name>`: set explicit project/assembly name.
- `--framework <tfm>`: set `TargetFramework` in the generated `.rvnproj`.
- `console|classlib|web|browser|nano`: select the scaffold type (`console` default).
- `--type <template>`: compatibility alias for selecting the scaffold type.
- `--list`: list the available scaffold types.
- `--force`: overwrite scaffold files when they already exist.

The `web` scaffold selects `Raven.Sdk.Web`, which supplies the shared ASP.NET
Core framework and Web SDK presets. The `browser` scaffold composes `Raven.Sdk`
with `Microsoft.NET.Sdk.WebAssembly` and supplies
a framework-free browser host plus Raven's built-in `[JSImport]`/`[JSExport]`
source generator for calling JavaScript, receiving a managed callback, and
exposing named Raven methods to JavaScript.
See [Browser WebAssembly applications](browser-wasm.md). The `nano`
scaffold targets `netnano1.0`, references the nanoFramework GPIO package, and
contains a board-specific blinky starting point whose LED pin may need to be
changed.
Console, class-library, and ASP.NET Core templates default to `net11.0`; the
browser template targets the stable `net10.0` WebAssembly toolchain.
`--framework` can override the target explicitly.

The same canonical files are available to the standard .NET template engine
through the `Raven.Templates` NuGet package:

```bash
dotnet new install Raven.Templates@VERSION
dotnet new raven-console -n HelloRaven
cd HelloRaven
dotnet run
```

Replace `VERSION` with the Raven release version to install.

The other short names are `raven-classlib`, `raven-web`, `raven-browser`, and
`raven-nano`.
