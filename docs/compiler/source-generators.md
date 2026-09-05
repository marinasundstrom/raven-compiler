# Source generators

Source generators produce additional Raven source files from a project
compilation. They are separate from macros: generators run as a workspace
or build-host compilation step, while macros expand an explicit invocation or
attached declaration inside the compiler.

For the user-facing overview of analyzers and source generators, see
[Extend a Raven project](extending-projects.md).

## When to use a generator instead of a macro

Choose a source generator when the output is derived from project-wide inputs
and naturally belongs in one or more generated documents. A registry assembled
from every annotated type is a typical generator task. Another is implementing
an authored partial declaration: the source declaration establishes the shape
or contract first, and a generated partial declaration supplies repetitive
members or implementation in a separate generated file. The generator receives
a compilation snapshot, contributes named source files, and is rerun by the
workspace or build host when its inputs change.

Choose a macro when the programmer explicitly asks for a local transformation
with `Name!(...)`, `Name! { ... }`, or an attached macro. Macro output occupies
the invocation or declaration's grammar position, and diagnostics, source
mapping, hover, navigation, and debugging can relate the expansion to that
authored site and its token body.

A generator should not search for invocations and emulate macro replacement;
a macro should not silently scan the whole compilation and emulate a generator.
See [Authoring Raven macros](../macro-authoring.md) for the corresponding macro
model and API.

The partial-declaration pattern is especially useful when consumers and editor
features must see the authored shape independently of generation. The generated
part augments that identity through Raven's normal partial-type merge; it does
not rewrite the authored declaration.

Implement `ISourceGenerator` and register the instance, type, or containing
assembly through a `GeneratorReference`. Generator references are project-level
compilation inputs, separate from diagnostic analyzer references:

```raven
import Raven.CodeAnalysis.*

class ModelGenerator : ISourceGenerator {
    func Initialize(context: GeneratorInitializationContext) {}

    func Execute(context: GeneratorExecutionContext) {
        context.AddSource("GeneratedModel", "class GeneratedModel {}")
    }
}

let updatedProject = project.AddGeneratorReference(
    GeneratorReference(ModelGenerator()))
```

Raven projects can load a compiled generator assembly declaratively:

```xml
<ItemGroup>
  <SourceGenerator Include="extensions/MyGenerators.dll" />
</ItemGroup>
```

The workspace runs generators before returning the project compilation.
Generated syntax trees therefore participate in binding, diagnostics, emit,
project references, and subsequent analyzer execution.

## Built-in JavaScript interop generator

Browser WebAssembly projects do not need to register a generator assembly.
When a Raven compilation contains a method marked `[JSImport]` or `[JSExport]`, the workspace
automatically runs `JavaScriptInteropGenerator`. The authored API mirrors C#:

```raven
import System.Runtime.InteropServices.JavaScript.*

partial class BrowserInterop {
    [JSImport("setGreeting", "raven")]
    static partial func SetGreeting(
        message: string,
        [JSMarshalAs<JSType.Function<JSType.String>>] onRendered: Action<string>
    );

    [JSExport]
    static func FormatGreeting(name: string) -> string
        => "Hello, $name!"
}
```

The generator supplies the matching import implementation and export wrapper
using .NET's low-level `JSFunctionBinding` contract. The MVP accepts static,
non-generic imports returning `unit`, with `string` and `Action<string>`
parameters, and static exports returning `string`, with `string` parameters.
Other import signatures report `RVNJS001`; other export signatures report
`RVNJS002`. This narrow slice gives Raven applications typed imports, delegate
callbacks, and named exports while a future macro design is evaluated.

`GeneratorDriver` can also be used directly by compiler hosts. Its run result
contains each generated source, generator diagnostic, and generator exception.
An unhandled generator exception is converted to `RVNGEN001` and does not crash
the workspace.

Hint names are relative paths. The `.rvn` extension is supplied when omitted,
and duplicate hint names from the same generator are rejected.

Generated trees are in memory by default, as with C# source generators. To also
write them during a build, set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`.
The default directory is `$(IntermediateOutputPath)generated`, normally
`obj/Debug/<target-framework>/generated`. `CompilerGeneratedFilesOutputPath`
can override it with an absolute path or a path relative to the project. Files
are grouped by generator type name and hint name. The compiler driver also
accepts `--generated-files-output-path <directory>`.

Disk output is for inspection; generators still contribute their trees directly
to the compilation. Workspace analysis does not write these files. Keep a custom
output directory out of `Compile` items to avoid compiling the generated files
twice on subsequent builds. The default `obj` directory is already excluded.

In VS Code, Go to Definition opens generated declarations in a read-only
`raven-generated` document backed by the current compilation. Hover and further
definition navigation work inside that document. Compiler and analyzer diagnostics
are shown against the generated document while it is open. Open generated documents
refresh after Raven source edits, including their diagnostics; enabling disk output
is not required for navigation or analysis.
