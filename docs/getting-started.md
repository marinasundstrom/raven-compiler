# Install and run Raven

Go from an installed SDK to your first running program. You do not need to
clone Raven or build the compiler from source.

Want to try it first? [Open the Playground](https://marinasundstrom.github.io/raven/playground/?example=hello).

## Prerequisites

- A .NET 11 SDK. The distributed Raven toolchain itself runs on .NET 11 and can
  also build `net10.0` applications when the .NET 10 targeting packs are
  installed.
- `curl` on macOS/Linux or PowerShell on Windows.
- VS Code and its `code` command if you want editor support.

Use a project-local `global.json` when a repository needs to pin the exact .NET
SDK feature band used by `dotnet` and MSBuild.

## 1. Install Raven

<div class="raven-install-tabs" data-raven-tabs>
<div role="tablist" aria-label="Installation platform">
<button type="button" role="tab" id="install-unix-tab" aria-controls="install-unix" aria-selected="true">macOS / Linux</button>
<button type="button" role="tab" id="install-windows-tab" aria-controls="install-windows" aria-selected="false" tabindex="-1">Windows</button>
</div>
<section id="install-unix" role="tabpanel" aria-labelledby="install-unix-tab" tabindex="0">

```bash
curl -fsSL https://github.com/marinasundstrom/raven/releases/download/v0.1.12/install-raven.sh \
  | sh -s -- 0.1.12
export PATH="$HOME/.raven/bin:$PATH"
```

Add the `export` line to your shell profile to make `rvn` available in future
terminals.

</section>
<section id="install-windows" role="tabpanel" aria-labelledby="install-windows-tab" tabindex="0" hidden>

```powershell
$version = "0.1.12"
Invoke-WebRequest "https://github.com/marinasundstrom/raven/releases/download/v$version/install-raven.ps1" -OutFile install-raven.ps1
./install-raven.ps1 -Version $version
$env:PATH = "$HOME\.raven\bin;$env:PATH"
```

</section>
</div>

Both installers select the correct operating-system and CPU archive, verify its
SHA-256 checksum, and install it under `~/.raven/sdk/<version>`.

### Verify the installation

Open a new terminal after making the PATH change, then run:

```bash
rvn sdk path
rvn doctor
```

`rvn doctor` checks the .NET SDK, compiler, language server, core library, macro
library, and MSBuild assets. `rvn` is the project and developer frontend;
`rvnc` is the lower-level compiler driver.

## 2. Create your first file

Create a file named `hello.rvn` in any working directory:

```raven
import System.Console.*

func greeting(name: string) -> string => "Hello, $name!"

WriteLine(greeting("Raven"))
```

Run it from that directory:

```bash
rvn run hello.rvn
```

You should see:

```text
Hello, Raven!
```

`func` declares a function, and imports
make .NET types and members available. A single file is enough for a small
program; create a project when you need several files or package dependencies.

## 3. Create a project

```bash
mkdir hello-raven
cd hello-raven
rvn init --type console --name HelloRaven
rvn run HelloRaven.rvnproj
```

The scaffold creates `src/Main.rvn` and a project file. Edit `Main.rvn` and run
the same command to compile and execute your changes. Use `rvn build` when you
only want to build.

Console projects default to `net11.0`. To target .NET 10, set
`<TargetFramework>net10.0</TargetFramework>` in the project and install the .NET
10 targeting packs. The Raven toolchain itself still requires .NET 11.

See [Project system](compiler/project-system.md) for dependencies and build
configuration, or run `rvn init --list` for the available scaffolds.

## 4. Open in VS Code

Download the [VS Code extension for 0.1.12](https://github.com/marinasundstrom/raven/releases/download/v0.1.12/raven-vscode.vsix).
From the directory containing the downloaded file, run:

```text
code --install-extension raven-vscode.vsix --force
```

You can also use **Extensions: Install from VSIX…** in the VS Code Command Palette.

If a GUI-launched VS Code cannot find `rvn` on its PATH, set `raven.sdkPath` to
the absolute directory printed by `rvn sdk path`.

Open the project directory:

```bash
code .
```

The extension provides syntax highlighting, diagnostics, completion, hover,
navigation, and refactorings. See the [VS Code guide](compiler/raven-vscode-extension.md)
for configuration and troubleshooting.

## Next steps

- [Raven in 60 seconds](raven-in-60-seconds.md): read one complete example.
- [Language tour](introduction.md): learn functions, models, patterns, and errors.
- [Raven for C# developers](raven-for-csharp-developers.md): compare familiar idioms.
- [Build an ASP.NET Core API](workloads/web-api.md): use a complete application.
- [Compiler tools](compiler/raven-compiler.md): inspect syntax, symbols, and binding.

To work on Raven itself, follow the [contributor guide](https://github.com/marinasundstrom/raven/blob/main/CONTRIBUTING.md).
It covers source builds and isolated development environments.
