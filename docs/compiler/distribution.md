# Raven Distribution

Raven ships platform-specific SDK archives, a platform-independent VS Code
extension, and a lockstep family of compiler libraries as NuGet packages. The
SDK archive is the canonical installation layout used by direct downloads and
future package-manager manifests.

The MVP distribution is a .NET 11-hosted toolchain and requires a compatible
.NET 11 SDK. All processes and plugins loaded by the distributed compiler use
that host line. This does not restrict application targets: target-specific
Raven.Core and Raven.Macros package assets allow the .NET 11 toolchain to build
both net10.0 and net11.0 projects when their targeting packs are installed.

## SDK layout

An installed SDK has the following stable structure:

```text
raven-sdk-<version>-<rid>/
  VERSION
  bin/
    rvn
    rvnc
    raven-language-server
  tools/
    rvn/
    rvnc/
    language-server/
  sdk/
    Raven.Core.dll
    build/
      Raven.Language.targets
      Raven.MSBuild.props
      Raven.nanoFramework.props
      Raven.nanoFramework.targets
```

The launchers require a compatible .NET SDK on `PATH`. Raven project builds
also use that SDK for MSBuild, reference assemblies, and targeting packs.

After extracting an archive, add its `bin` directory to `PATH`. The active SDK
can then be queried without parsing launcher paths:

```bash
rvn sdk path
rvn doctor
```

`rvn doctor` verifies that a compatible .NET SDK is available and that the
active Raven SDK contains the compiler, language server, core library, and
MSBuild targets.

Set `RAVEN_SDK_ROOT` to select an SDK explicitly. The directory must contain
both `VERSION` and `sdk/build/Raven.Language.targets`.

Release builds can be installed directly with the platform installer:

```bash
curl -fsSL https://github.com/marinasundstrom/raven/releases/download/v0.1.12/install-raven.sh \
  | sh -s -- 0.1.12
```

```powershell
$version = "0.1.12"
Invoke-WebRequest "https://github.com/marinasundstrom/raven/releases/download/v$version/install-raven.ps1" -OutFile install-raven.ps1
./install-raven.ps1 -Version $version
```

Both installers verify the archive against the release's `SHA256SUMS` file and
install versioned SDK files under `~/.raven` by default.
Set `RAVEN_INSTALL_ROOT` to choose another installation directory.
Add `~/.raven/bin` to PATH after installation, then run `rvn doctor`.

## Building an SDK archive

Run the package script with a .NET runtime identifier and version:

```bash
scripts/package-sdk.sh osx-arm64 0.1.12
scripts/package-sdk.sh linux-x64 0.1.12
scripts/package-sdk.sh win-x64 0.1.12
```

Artifacts are written to `artifacts/distribution` by default. The distributable
toolchain is fixed to net11.0 for this MVP. Override the output directory with
`RAVEN_PACKAGE_OUTPUT`.

The packaging scripts regenerate compiler sources before building, so they are
safe to run from a clean checkout without relying on ignored build outputs.

Validate a staged SDK before publishing it:

```bash
scripts/test-distribution.sh artifacts/distribution/raven-sdk-0.1.12-osx-arm64
scripts/test-distribution.sh --structure-only artifacts/distribution/raven-sdk-0.1.12-win-arm64
```

Use `--structure-only` when inspecting an archive that cannot execute on the
current operating system or architecture. Release automation structurally
validates every archive and executes an additional smoke test for its native
Linux x64 artifact.

Release automation should build these runtime identifiers:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

The `Distribution` GitHub Actions workflow builds all six archives and the
VSIX together with the NuGet package family. Distribution is deliberately a
manual process: neither a branch push nor a tag push starts the workflow. Start
`Distribution` from the GitHub Actions UI, select the commit or tag to build,
and provide the version explicitly. By default the run only produces retained
workflow artifacts and does not publish anything externally.

## Versioning unreleased local builds

An unreleased build belongs to the **next** development line, even before that
preview is prepared or tagged. After a stable release, increment the patch
version and start a new preview counter before appending local provenance. For
example, after `0.1.0` has been published, a build from later source uses
a version such as:

```text
0.1.12-preview.1-local.<sha>
```

Do not use `0.1.0-local.<sha>` for later source. That spelling makes
new work appear to be a rebuild of the already published stable release and
can mix incompatible compiler, macro, SDK, and editor artifacts under a
misleading version family.

Use the same complete version for every artifact built from one commit:

```bash
version="0.1.12-preview.1-local.$(git rev-parse --short HEAD)"
scripts/package-sdk.sh osx-arm64 "$version"
scripts/package-nuget.sh "$version"
scripts/package-vscode.sh "$version"
```

The SDK archive, `Raven.Sdk`, `Raven.Sdk.Web`, `Raven.Core`, `Raven.Macros`,
`Raven.CodeAnalysis`, templates, analyzers, and VSIX form one lockstep version
family. Do not combine a locally built compiler with Core or Macros restored
from the preceding published release. When validating a newly packed family,
use a fresh NuGet package cache or clear only that exact unpublished local
version so a previous package with the same identity cannot be reused.

The `local.<sha>` suffix identifies an unpublished build; it does not create a
release tag and must not be used for publication. Formal release preparation
selects the stable `0.1.0` pre-bootstrap release and updates the tracked release
references together.

## Release procedure

A release is one immutable Git commit. That commit must contain the code,
generated files, package version, changelog entry, installation examples, and
all other versioned documentation. Prepare those references from a clean
worktree:

```bash
scripts/prepare-release.sh
```

The qualified pre-bootstrap foundation is Raven's first non-preview release,
`0.1.0`. The project remains experimental, so this milestone does not claim
the compatibility and maturity implied by `1.0.0`. Release preparation accepts
the current `0.1.0-preview.N` line and selects exactly `0.1.0`; an optional
`scripts/prepare-release.sh 0.1.0` argument asserts that decision but cannot
override it. Moving to `1.0.0` or another release line remains a separate,
explicit project decision.

The script replaces the previously selected release version in tracked files,
creates the dated changelog section, and validates the known release references.
Review its complete diff and finish the changelog before committing. Do not tag
an earlier code commit and then add release references in a later commit.

Review the diff, finish the changelog, and commit every intended release change.
Then run the release checks against that clean commit:

```bash
scripts/validate-release.sh VERSION --require-clean
scripts/package-nuget.sh VERSION
scripts/test-target-framework-matrix.sh
scripts/build-project-samples.sh
scripts/run-project-samples.sh
```

Pack the candidate family first so projects that select the new SDK version can
resolve it from the repository-local feed during the remaining checks.

For the stable pre-bootstrap release and every release that establishes or
advances a bootstrap stage, also retain the full baseline, isolated runtime,
standalone-sample, and project-sample build-and-run results described in the
internal `docs/testing/release-and-bootstrap-gates.md` checklist, including the
active SDK and repository-versus-installed toolchain provenance.

Every push to `main` runs `scripts/test-ci.sh`: the repository's ordered
generator/compiler build, a bounded compiler-contract filter, Core tests, and
language-server unit tests. Main CI is the required integration gate; broad
baseline, isolated runtime, sample, language-server integration, and performance
checks remain separate qualification runs. During development, run the smallest
affected suites before integration and retain the release checks listed above.

If a check requires a fix, commit the fix, repeat the affected checks, and push
it. Wait for Main CI to pass, then tag that exact commit and push the tag:

```bash
git tag vVERSION
git push origin vVERSION
```

Dispatch `Distribution` against `vVERSION`, never against the branch name, when
either publication switch is enabled. The workflow verifies that the tag points
to the checked-out commit, requires a successful Main CI push run for that exact
commit, checks that the NuGet version is still unused, and then runs only the
release-specific archive, VSIX, NuGet package, and installation smoke checks.
Package validation also checks that `Raven.Sdk` and `Raven.Sdk.Web` record the
same Git commit. NuGet publication deliberately fails on duplicate versions;
published versions are immutable and must never be silently skipped.

`allow_missing_main_ci` is an exceptional, opt-in escape hatch for a release
whose remaining commit changes only repair CI or release infrastructure and
whose affected checks have been rerun directly. It defaults to `false` and
requires a non-empty `main_ci_exception_reason`, which is recorded in the
workflow run. Do not use it for compiler, runtime, SDK behavior, or untested
product changes.

The repository's `NuGet.Config` puts `artifacts/packages` before NuGet.org for
source-build testing. NuGet's global package cache is keyed only by package ID
and version, so rebuilding a local package with the same version does not update
an already restored cache entry. Use a fresh `NUGET_PACKAGES` directory when
validating repacked local artifacts or the public release, and inspect
`.nupkg.metadata` when package provenance is uncertain. A local restore is not
evidence of what NuGet.org contains.

The dispatch form has separate opt-in switches for publishing a GitHub release
and publishing the NuGet package family. Either publishing operation requires
the workflow to be dispatched against the matching `v<version>` tag. This keeps
building, creating a GitHub release, and pushing to NuGet.org explicit release
operator decisions while reusing one validated artifact set. Versions with a
SemVer prerelease suffix, such as `0.1.0-preview.3`, create a GitHub prerelease.

### Post-publication checklist

Treat publication, package propagation, installation verification, and website
deployment as separate stages. A successful earlier stage enables the next one;
it does not prove that the next stage is ready.

1. Confirm that `Distribution` completed successfully and that the GitHub
   prerelease contains the installers, checksums, VSIX, NuGet packages, and all
   platform SDK archives. Keep the release tag immutable after publication;
   later documentation or workflow corrections belong to `main` and the next
   unreleased preview line.
2. Wait for the complete lockstep package family to propagate through
   NuGet.org. Completing the NuGet push does not guarantee that normal restore
   clients can fetch the new version yet. The GitHub release assets and NuGet
   packages become usable through independent publication paths.
3. Do not treat appearance in NuGet's flat-container version index alone as
   sufficient. From outside the repository and its local package feed, use an
   empty package cache to install the published `Raven.Templates`, create a
   Raven project, and build it. Continue only after that public-only probe
   restores both the template and its selected Raven application-model SDK
   successfully.
4. Run `Installation verification` with the published version. It downloads the
   public release rather than workflow artifacts, exercises the SDK on Windows,
   Linux, and macOS, and installs the checksum-verified VSIX into clean portable
   VS Code instances. Require every matrix job to pass.
5. Dispatch `Raven website` from `main` with `publish_site` enabled. Website
   publication is independent of the package/tag release; leave
   `documented_version` empty to use `global.json`, or select the Raven version
   whose behavior the updated documentation describes. The
   `github-pages` environment currently permits `main`, not a release tag; a
   tag-triggered run can build a valid Pages artifact but its deployment will be
   rejected by environment protection. Verify the deployed footer's documented
   Raven version and source commit. A docs-only commit after a release remains
   associated with that released version even though it is not the tagged source
   commit. The workflow fetches release tags to determine whether the documented
   version is released.

## NuGet packages

Raven's initial NuGet family contains:

- `Raven.Core`: core types and runtime support for compiled Raven programs.
- `Raven.Macros`: Raven's standard compiler macros.
- `Raven.CodeAnalysis`: public syntax, semantic, workspace, diagnostic, and
  emission APIs.
- `Raven.Analyzers`: recommended naming and style analyzers and code fixes,
  delivered through NuGet's `analyzers/dotnet` asset convention.
- `Raven.Sdk`: the NuGet-resolved MSBuild Project SDK containing the Raven
  compiler host and language targets. It builds on `Microsoft.NET.Sdk` and
  adds exact-version implicit references to `Raven.Core` and `Raven.Macros`.
- `Raven.Sdk.Web`: the Web project SDK. It builds on
  `Microsoft.NET.Sdk.Web`, carries the same Raven compiler contract, and adds
  the ASP.NET Core implicit imports and Raven-compatible Web generated-source
  integration.
- `Raven.Templates`: project templates for the standard `dotnet new` CLI,
  with console, class-library, ASP.NET Core, and .NET nanoFramework variants.

All packages in a release are built from the same commit and receive the same
version. `Raven.Macros` carries a package dependency on the matching
`Raven.CodeAnalysis` version. `Raven.Analyzers` intentionally carries no
runtime dependency: it binds to the compiler host's matching
`Raven.CodeAnalysis` assembly when the analyzer asset is loaded.
`Raven.Sdk` and its workload-specific siblings are the .NET CLI entry points
for `.rvnproj` projects. Standalone projects generated by `Raven.Templates` and
`rvn init` pin the matching SDK and version; Web projects use
`<Project Sdk="Raven.Sdk.Web/VERSION">`. Repositories can instead use the
shorter SDK names and select their versions once under the `msbuild-sdks`
section of `global.json`. Neither form requires users to
configure `LanguageTargets`, compiler paths, or SDK installation roots. The
SDK, templates, compiler, Core, and Macros versions move together.

Build and validate the packages locally with:

```bash
scripts/package-nuget.sh 0.1.0-preview.1
```

Packages and symbol packages are written to `artifacts/packages`. Set
`RAVEN_NUGET_OUTPUT` to use another directory. Validation checks package
contents and metadata, then restores and builds isolated consumer projects
from the local package directory. The Raven consumer resolves Core and Macros
implicitly through `Raven.Sdk`, executes a packaged macro, and asserts that a
packaged analyzer reports its expected diagnostic.
It also installs `Raven.Templates` into an isolated .NET CLI home and
materializes all five template variants without changing the operator's
machine-wide template registrations. Console, class-library, ASP.NET Core, and
browser WebAssembly projects are built; the console result is also executed,
and the browser build must contain the generated runtime bundle. The nanoFramework template is
validated structurally but is not compiled as part of package validation,
because its metadata processor requires a separate Mono toolchain on Unix.
The class-library check creates a second Raven application with a normal
`ProjectReference`, runs and publishes that application, and verifies the
library's public API from a C# project as well.

After publication, install and use the templates with:

```bash
dotnet new install Raven.Templates@VERSION
dotnet new raven-console -n HelloRaven
cd HelloRaven
dotnet run
dotnet new raven-classlib -n MyLibrary
dotnet new raven-web -n RavenWeb
dotnet new raven-browser -n RavenBrowser
dotnet new raven-nano -n RavenBlinky
```

To use the generated class library from another project, add an ordinary .NET
project reference:

```bash
dotnet add HelloRaven/HelloRaven.rvnproj reference MyLibrary/MyLibrary.rvnproj
```

Public top-level Raven functions are imported as namespace members by Raven
consumers. Other .NET languages can call their generated static container;
for the global namespace used by the template, `Greet()` is emitted as
`NamespaceMembers.Greet()`.

### Package a Raven class library

A Raven class library uses the standard .NET packing workflow. Add the normal
NuGet metadata to the project and run `dotnet pack`:

```xml
<Project Sdk="Raven.Sdk/VERSION">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <OutputType>Library</OutputType>
    <PackageId>Contoso.RavenUtilities</PackageId>
    <Version>1.0.0</Version>
    <Authors>Contoso</Authors>
    <Description>Reusable Raven utilities.</Description>
  </PropertyGroup>
</Project>
```

```bash
dotnet pack --configuration Release --output artifacts/packages
```

Raven libraries generate both documentation formats by default. `dotnet pack`
automatically includes the assembly, .NET-compatible XML documentation, and
the complete Raven Markdown sidecar tree:

```text
lib/net11.0/Contoso.RavenUtilities.dll
lib/net11.0/Contoso.RavenUtilities.xml
lib/net11.0/Contoso.RavenUtilities.docs/
  manifest.json
  invariant/
    symbols/
      ...
```

Keep the `.xml` file and `.docs` directory adjacent to the assembly. Raven
tooling prefers a matching Markdown symbol file and falls back to XML. C# and
other conventional .NET tooling can consume the XML file. Consumers do not
need to configure documentation paths: restore and build copy available
sidecars from copy-local package references beside the referenced DLL.

The SDK handles its own generated sidecar automatically. To package a manually
maintained Markdown tree from another location, append a target to the standard
NuGet pack hook. Preserve the directory name, manifest, and all relative paths:

```xml
<PropertyGroup>
  <GenerateMarkdownDocumentationFile>false</GenerateMarkdownDocumentationFile>
  <TargetsForTfmSpecificContentInPackage>
    $(TargetsForTfmSpecificContentInPackage);IncludeCustomRavenDocumentation
  </TargetsForTfmSpecificContentInPackage>
</PropertyGroup>

<Target Name="IncludeCustomRavenDocumentation">
  <ItemGroup>
    <TfmSpecificPackageFile Include="docs/$(AssemblyName).docs/**/*">
      <PackagePath>
        lib/$(TargetFramework)/$(AssemblyName).docs/%(RecursiveDir)%(Filename)%(Extension)
      </PackagePath>
    </TfmSpecificPackageFile>
  </ItemGroup>
</Target>
```

Do not flatten the Markdown directory or omit `manifest.json`; symbol lookup
depends on that structure. See the
[External Documentation Sidecars](https://github.com/marinasundstrom/raven/blob/main/docs/compiler/design/external-documentation-sidecars.md)
design note for the format contract and the [project system](project-system.md)
for documentation-related properties.

Replace `VERSION` with the release version being installed.

Minimal ASP.NET Core applications use `Raven.Sdk.Web`, which composes the
normal .NET Web SDK and supplies Raven-specific generated-source integration.
Razor syntax and Raven-native Razor code generation remain future work.
The nanoFramework template remains on its specialized target and does not take
the desktop `Raven.Core` or `Raven.Macros` packages, which currently target
`net10.0` and `net11.0`.

### MVP boundary and follow-up

The first installable SDK release is complete when the manually dispatched
release publishes one lockstep version of the NuGet family, standalone SDK
archives/installers, and VSIX; the separate installation workflow must then
create, restore, build, run, and publish generated projects through the public
distribution channels on the supported operating systems.

The following improvements are intentionally outside that MVP:

- optional workload-specific analyzer presets supplied by Raven SDKs without
  hard-coding workload knowledge into the compiler;
- a nanoFramework-compatible Raven.Core and standard-macro surface selected by
  the `netnano1.0` target framework;
- a .NET global tool and package-manager manifests for installing `rvn`;
- VS Code Marketplace publication (the VSIX remains a GitHub release asset);
- a .NET workload manifest, which is unnecessary while the compiler toolchain
  can be restored as a normal NuGet Project SDK; and
- side-by-side installation and selection of Raven SDK versions. The intended
  default is the latest compatible installed Raven SDK unless a project pins a
  version; the resolver policy is a post-MVP concern; and
- signing/notarization and additional supply-chain provenance beyond release
  checksums and NuGet Trusted Publishing.

NuGet.org publication only runs when an operator manually dispatches the
workflow against the matching `v<version>` tag and enables `publish_nuget`.
The publishing job uses NuGet Trusted Publishing for package owner `marna.li`,
repository `marinasundstrom/raven`, and workflow file `distribution.yml`; it
does not use a stored, long-lived API key. Leave the trusted publisher's
environment field empty because this workflow does not declare a GitHub
environment.

## VS Code extension

The extension contains a framework-dependent copy of the Raven language
server so editor features work without a platform-specific VSIX. Build it with:

```bash
scripts/package-vscode.sh 0.1.12
```

Install the published release directly from GitHub Releases:

```bash
curl -fLO https://github.com/marinasundstrom/raven/releases/download/v0.1.12/raven-vscode.vsix
code --install-extension raven-vscode.vsix --force
```

The extension resolves the compiler SDK from `raven.sdkPath` first and then by
running `rvn sdk path`. Build, run, and debug commands require the SDK, while
the bundled server can provide editor features independently.
For GUI sessions that do not inherit the shell PATH, set `raven.sdkPath` to the
versioned directory reported by `rvn sdk path`.

The TypeScript extension host and its runtime dependencies are bundled into a
single production JavaScript entry point. The VSIX excludes `node_modules`,
source files, source maps, and language-server symbols while retaining the
framework-dependent server binaries, grammar, configuration, README, and MIT
license.
