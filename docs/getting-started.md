# Getting Started

This walkthrough installs the current Raven preview SDK, runs a small Raven
program, and introduces a scaffolded `.rvnproj` application. Building Raven
itself from source remains available for compiler contributors, but is not
required to try the language.

If you want to change the compiler, SDK, or editor integration, use the
repository-isolated setup in the [contributor guide on
GitHub](https://github.com/marinasundstrom/raven/blob/main/CONTRIBUTING.md). It
keeps repository builds separate from an installed Raven SDK and VS Code
extension.

If you are coming from C#, read this as more than a command checklist. The
walkthrough uses familiar C# problem shapes and shows the Raven idioms for them:
plain top-level functions instead of class wrappers, unions instead of
enum-plus-state objects, `match` instead of scattered type/enum tests,
`Result<T, E>` instead of throwing for expected failure, and `Option<T>`
instead of nullable-heavy domain code. Raven also leans on declaration keywords
so a reader can scan source and immediately see what each declaration is.

Part of learning Raven is unlearning ceremony that C# can make feel inherent to
program structure. You are not unlearning object-oriented programming. You are
learning to distinguish a real object—with identity, state, lifecycle, or
polymorphic behavior—from a class that exists only to contain `Main` or a set of
utility functions.

For a broader collection of side-by-side translations, see [Raven for C#
developers](raven-for-csharp-developers.md).

## Prerequisites

- A .NET 11 SDK. The distributed Raven toolchain itself runs on .NET 11 and can
  also build `net10.0` applications when the .NET 10 targeting packs are
  installed.
- `curl` on macOS/Linux or PowerShell on Windows.
- VS Code and its `code` command if you want editor support.

Use a project-local `global.json` when a repository needs to pin the exact .NET
SDK feature band used by `dotnet` and MSBuild.

## 1. Install the preview SDK

On macOS or Linux:

```bash
curl -fsSL https://github.com/marinasundstrom/raven/releases/download/v0.1.10/install-raven.sh \
  | sh -s -- 0.1.10
export PATH="$HOME/.raven/bin:$PATH"
```

Add the `export` line to your shell profile to make `rvn` available in future
terminals.

On Windows PowerShell:

```powershell
$version = "0.1.10"
Invoke-WebRequest "https://github.com/marinasundstrom/raven/releases/download/v$version/install-raven.ps1" -OutFile install-raven.ps1
./install-raven.ps1 -Version $version
$env:PATH = "$HOME\.raven\bin;$env:PATH"
```

Both installers select the correct operating-system and CPU archive, verify its
SHA-256 checksum, and install it under `~/.raven/sdk/<version>`.

## 2. Verify the installation

Open a new terminal after making the PATH change, then run:

```bash
rvn sdk path
rvn doctor
```

`rvn doctor` checks the .NET SDK, compiler, language server, core library, macro
library, and MSBuild assets. `rvn` is the project and developer frontend;
`rvnc` is the lower-level compiler driver.

To install the VS Code extension from the same release:

```bash
curl -fLO https://github.com/marinasundstrom/raven/releases/download/v0.1.10/raven-vscode.vsix
code --install-extension raven-vscode.vsix --force
```

If a GUI-launched VS Code cannot find `rvn` on its PATH, set `raven.sdkPath` to
the absolute directory printed by `rvn sdk path`.

## 3. Run one file without a project

For a small program, learning exercise, or command-line helper, Raven can run a
single source file as a file-based application. From a Raven source checkout,
try the included sample:

```bash
rvn run samples/scripts/hello.rvn -- Raven
```

The source path itself is shorthand for `run`:

```bash
rvn samples/scripts/hello.rvn Raven
```

On macOS or Linux, the sample's `#!/usr/bin/env rvn` shebang and executable bit
provide the script-shaped flow directly:

```bash
./samples/scripts/hello.rvn Raven
```

The installed Raven SDK puts the launcher on `PATH`, so `/usr/bin/env` can find
`rvn` for executable scripts.

For `rvn run`, arguments after `--` are passed to `Main(args: string[])`; the
shorthand and shebang forms pass arguments following the source path directly.
The command compiles with ordinary Raven semantics, runs the resulting managed
application, returns its exit code, and removes its isolated temporary
artifacts afterward. A `.rvnproj` becomes useful when the application needs
project-level sources, dependencies, or build configuration.

## 4. Compile and run a known sample

From a Raven source checkout, start with a sample that exercises .NET interop,
LINQ-style extensions, `Option`, and `Result`:

```bash
rvnc samples/cases/quote-summary-linq-result-option.rav -o /tmp/raven-case.dll
dotnet /tmp/raven-case.dll
```

To analyze without emitting an assembly, add `--no-emit`:

```bash
rvnc samples/cases/quote-summary-linq-result-option.rav --no-emit
```

To get source-highlighted diagnostics from the compiler driver, add
`--highlight`.

## 5. What to notice if you write C#

The sample is intentionally shaped like a small C# service: load a request, find
a rate plan, apply optional discounts/surcharges, and return a decision. Raven's
approach is different in a few important places.

The first adjustment is conceptual: do not begin by asking which class should
contain the code. Begin with the values and operations in the problem, then add
a class when the domain gives you a reason for one.

| Common C# shape | Raven idiom |
| --- | --- |
| Class-based `Program.Main` entry point | Top-level statements or a plain `Main` function |
| Static helper classes used only to hold functions | Plain top-level functions |
| One-method service interface | A function parameter describing the required operation |
| Declaration shape inferred mostly from context | Keywords such as `func`, `let`, `var`, `event`, `class`, `union`, and `case` |
| `FirstOrDefault()` followed by `null` checks | `FirstOrNone()` returns `Option<T>` |
| Throwing for expected validation or lookup failure | Return `Result<T, E>` |
| `try`/`catch` around ordinary parsing or service calls | `try expr` produces a `Result` value |
| `enum` plus extra properties, or a small inheritance hierarchy | `union` cases with typed payloads |
| `switch` expressions mixed with null/type checks | `match` over values, options, results, and unions |
| Mutable locals unless marked `readonly` or avoided by convention | `let` by default; `var` when mutation is intentional |
| `void` | `()` (`unit`) |

### Parse strings and look up values

When a value might not be present, Raven uses `Option`. For example, use
`TryParse` when invalid text is an ordinary possibility:

```raven
import System.*
import System.Collections.Generic.*

let port = int.TryParse(portText) // Option<int>

port match {
    Some(let value) => Console.WriteLine("Port: $value")
    None => Console.WriteLine("Not a valid port")
}
```

Use `Parse` when you want details about why conversion failed:

```raven
let id = Guid.Parse(idText) // Result<Guid, FormatException>
```

Lookups follow the same absence convention:

```raven
let plan = plansByCode.TryGetValue(code) // Option<RatePlan>
```

Keep the meanings separate: a missing dictionary key is `None`; a present
nullable value is `Some(null)`; an expected parse failure is `Error`; and an
exception caused by forcing null through a non-null argument is a fault.

You do not need to invent a class just to write a function. Raven supports
top-level functions directly, so a small operation can stay at file or namespace
scope until it has a real reason to live on a type:

```raven
func NormalizeCarrier(name: string) -> string {
    return name.Trim().ToUpperInvariant()
}

func HasTag(tags: string[], tag: string) -> bool {
    match tags.FirstOrNone(t => t == tag) {
        Some(_) => true
        None => false
    }
}
```

Use types when they model data or behavior that belongs together. Use plain
functions when the operation is just a named transformation, lookup, validation,
or workflow step.

This is not a preference against classes. A device connection, stateful
aggregate, cache, UI component, or resource owner may naturally be a class.
Raven asks whether the object represents something, not whether code needs a
container.

For a dependency with one operation, a function parameter can state the needed
capability without inventing an interface:

```raven
func ReportTemperature(
    read: () -> Result<decimal, string>,
    publish: (decimal) -> ()) -> Result<decimal, string> {
    let temperature = read()?
    publish(temperature)
    return Ok(temperature)
}
```

Use an interface when several related operations form a real, open protocol.

Raven also makes declaration kinds visible. A function starts with `func`; an
immutable lexical binding starts with `let`; a read-only property starts with
`val`; mutable bindings and properties start with `var`; an event starts with
`event`; union alternatives start with `case`.

```raven
import System.*

class ConsoleLogger {
    event Logged: Action<string>?
    val Prefix: string = "log"
    var Count: int = 0

    func Log(message: string) -> () {
        Count = Count + 1
        Logged?.Invoke("$Prefix: $message")
    }
}
```

That consistency is intentional. Raven uses keywords to announce declarations
instead of making the reader infer too much from punctuation, modifiers, or
where a member happens to appear.

For example, a C# version of a shipment decision often starts as an enum plus
separate nullable detail fields:

```csharp
enum DecisionKind { Approve, ManualReview, Reject }

sealed record Decision(DecisionKind Kind, string? Reason);
```

Raven models the same idea as one union. Cases that need data carry that data;
cases that do not need data stay empty.

```raven
union Decision {
    case Approve
    case ManualReview(reason: string)
    case Reject(reason: string)
}
```

Consumers handle every visible shape in one `match` expression:

```raven
func FormatDecision(decision: Decision) -> string {
    match decision {
        .Approve => "Approved"
        .ManualReview(let reason) => "Review: $reason"
        .Reject(let reason) => "Rejected: $reason"
    }
}
```

Lookup and validation use `Result<T, E>` when failure is part of the domain,
not an exceptional crash path:

```raven
record class QuoteError(val Message: string)

func FindRatePlan(plans: IEnumerable<RatePlan>, carrier: string) -> Result<RatePlan, QuoteError> {
    return plans.FirstOrError(
        p => p.Carrier == carrier,
        () => QuoteError("No rate plan for carrier: $carrier"))
}
```

The caller can keep the happy path linear with `?`. If `FindRatePlan` returns
`Error`, the enclosing `Result` function returns that error immediately.

```raven
func QuoteShipment(request: ShipmentRequest, plans: IEnumerable<RatePlan>) -> Result<Quote, QuoteError> {
    let plan = FindRatePlan(plans, request.Carrier)?
    let total = plan.BaseCents + (request.WeightKg * plan.PerKgCents)
    return Ok(Quote(request.Id, request.Carrier, total))
}
```

Optional values are explicit too. Instead of using `string?` throughout domain
logic, use `Option<string>` and match it where the decision matters:

```raven
func PromoCents(code: Option<string>) -> Option<int> {
    let raw = code?
    let normalized = raw.Trim().ToUpperInvariant()

    match normalized {
        "SAVE5" => Some(500)
        "FREESHIP" => Some(200)
        _ => None
    }
}
```

This is still ordinary .NET code. The sample imports `System.Linq.*`, uses
`IEnumerable<T>`, calls string APIs, and emits IL. Raven changes the source
model for domain flow; it does not ask you to leave the .NET ecosystem.

## 6. Inspect syntax and binding

The `rvn dev` commands are useful when learning the language or debugging the
compiler.

Print the parsed syntax tree:

```bash
rvn dev syntax samples/cases/quote-summary-linq-result-option.rav
```

Print the bound tree:

```bash
rvn dev bound-tree samples/cases/quote-summary-linq-result-option.rav
```

Other useful views include:

- `rvn dev dump` - pretty syntax dump.
- `rvn dev symbols` - symbol information for a file or project.
- `rvn dev quote` - Raven syntax factory quote output.

Creating a `.debug/` directory in the current or a parent folder also causes
`rvnc` to write debug dumps while compiling.

## 7. Write a first Raven file

Create `hello.rvn` in the repository root or another scratch directory:

```raven
import System.Console.*

func Main() -> () {
    let message = BuildGreeting("Raven")
    WriteLine(message)
}

func BuildGreeting(name: string) -> string {
    return "Hello, $name"
}
```

Compile and run it:

```bash
rvnc hello.rvn -o /tmp/hello.dll
dotnet /tmp/hello.dll
```

The example uses `()` as the empty result type. Raven does not use `void`.

## 8. Read current Raven style

Current documentation and samples follow these rules:

- Use `let` for immutable lexical bindings and `var` for mutable lexical bindings.
- Prefer plain top-level functions for standalone operations; do not create a
  class only to hold methods.
- Use classes and interfaces when identity, lifecycle, encapsulated state, or
  open polymorphism are part of the model.
- Consider a function parameter for a dependency that consists of one
  operation.
- Let declaration keywords carry meaning: `func` declares behavior, `let` and
  `var` declare lexical bindings, `event` declares events, and `case` declares
  union alternatives.
- Members are public by default; use access modifiers to narrow visibility.
- Use `match` when branching should stay visible.
- Use `Option<T>` for absence in Raven domain code.
- Use `Result<T, E>` for expected failure and `?` to propagate it.
- Prefer explicit pattern bindings: `Some(let value)`, `let (a, b) = pair`,
  `if let Some(item) = maybe`, and `let Some(item) = maybe else { return }`.
- Prefer `let ... else` over a null-coalescing early-exit block when a pattern
  should establish a binding for the rest of the scope.
- Use function type arrows, such as `let op: (int, int) -> int`.
- Use explicit constructors such as `ShipmentRequest(...)` unless the target
  type is already obvious.
- Use target-typed shorthand such as `.Active` and `.(...)` only when the
  surrounding type is clear.

Example:

```raven
import System.Linq.*

record class ShipmentRequest(val Id: string, val Carrier: string, val WeightKg: int)

func Resolve(requests: ShipmentRequest[]) -> Result<ShipmentRequest, string> {
    let request = requests.FirstOrError(
        r => r.Id == "REQ-1002",
        () => "Request not found")?

    return Ok(request)
}
```

## 9. Create a project

Project scaffolding lives behind the `rvn init` command:

```bash
mkdir hello-raven
cd hello-raven
rvn init --type console --name HelloRaven
rvn build HelloRaven.rvnproj
rvn run HelloRaven.rvnproj
```

The console scaffold creates `src/Main.rvn` with an explicit `func Main()`
entry point. Additional source files may contain declarations, but executable
top-level statements may only occur in one file.

Create a class library scaffold instead:

```bash
rvn init --type classlib --name MyLibrary
```

Web and .NET nanoFramework starting points are also available:

```bash
rvn init web --name RavenWeb
rvn init browser --name RavenBrowser
rvn init nano --name RavenBlinky
```

Run `rvn init --list` to see every built-in scaffold. The browser scaffold is a
framework-free .NET WebAssembly app; install `wasm-tools` before building it.
The nanoFramework template targets `netnano1.0`; adjust its GPIO pin for the
selected board.

The same projects can be created through the normal .NET CLI after installing
the versioned template package:

```bash
dotnet new install Raven.Templates@VERSION
dotnet new raven-console -n HelloRaven
cd HelloRaven
dotnet run
```

Replace `VERSION` with the Raven release version to install.

The generated `.rvnproj` pins the matching Raven SDK version; Web projects use
`Raven.Sdk.Web` and the other desktop templates use `Raven.Sdk`. The .NET CLI
restores the Raven compiler, Core, and standard macros automatically; no
`RavenSdkRoot`, `LanguageTargets`, or compiler path needs to be configured.

Use `raven-classlib`, `raven-web`, `raven-browser`, or `raven-nano` for the
other variants. Console, class-library, and ASP.NET Core templates default to
`net11.0` in this release. The browser template targets the stable `net10.0`
WebAssembly toolchain, and the Nano template remains on `netnano1.0`.

The SDK name selects the application model: general projects use `Raven.Sdk`,
while ASP.NET Core projects must use `Raven.Sdk.Web`. `TargetFramework` is a
separate choice that selects the runtime/platform.

The installed `rvn` commands select Raven's packaged MSBuild targets before
delegating to the .NET SDK. A source checkout configures those targets through
its `Directory.Build.props`, so repository contributors can also invoke
`dotnet build` and `dotnet run` directly there.

## 10. Where to go next

- [Raven for absolute beginners](raven-for-absolute-beginners.md) if you are new
  to programming itself.
- [Language introduction](introduction.md) for a guided feature overview.
- [Raven for C# developers](raven-for-csharp-developers.md) for side-by-side
  migration guidance.
- [Domain modeling](lang/domain-modeling.md) for choosing among functions,
  records, unions, classes, and interfaces.
- [Language philosophy](lang/philosophy.md) for design principles.
- [Language feature guides](lang/features/index.md) for concise explanations
  and examples.
- [Style guide](lang/style-guide.md) for source layout conventions.
- [Compiler project system](compiler/project-system.md) for `.rvnproj` details.
- [Contributor guide on
  GitHub](https://github.com/marinasundstrom/raven/blob/main/CONTRIBUTING.md)
  for building and testing Raven itself in isolated terminal and VS Code
  environments.
- [Sample projects on GitHub](https://github.com/marinasundstrom/raven/tree/main/samples/projects)
  for complete, runnable examples.
