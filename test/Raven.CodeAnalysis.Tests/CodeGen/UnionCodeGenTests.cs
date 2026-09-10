using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

namespace Raven.CodeAnalysis.Tests;

public class UnionCodeGenTests
{
    [Fact]
    public void RavenCoreStructuredDisplayMarker_IsAppliedToGeneratedDisplayTypes()
    {
        const string code = """
record Reading(Value: int)

union ReadingState {
    case Available(reading: Reading)
    case Missing
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "structured-display-marker",
            [syntaxTree],
            TestMetadataReferences.DefaultWithRavenCore,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.NotNull(compilation.GetTypeByMetadataName("Raven.Runtime.CompilerServices.IRavenStructuredDisplay"));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.DefaultWithRavenCore);
        var assembly = loaded.Assembly;
        AssertStructuredDisplay(assembly.GetType("Reading", throwOnError: true)!);
        AssertStructuredDisplay(assembly.GetType("ReadingState", throwOnError: true)!);
        AssertStructuredDisplay(assembly.GetType("ReadingState+Available", throwOnError: true)!);

        static void AssertStructuredDisplay(Type type)
        {
            Assert.Contains(
                type.GetInterfaces(),
                candidate => candidate.FullName == "Raven.Runtime.CompilerServices.IRavenStructuredDisplay");
        }
    }

    [Fact]
    public void UnionFormatting_EmitsWhenCoreLibraryOmitsDesktopConvenienceApis()
    {
        const string code = """
union TemperatureState {
    case SensorUnavailable
    case Comfortable(celsius: double)
    case Label(text: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var coreLibrary = CreateCoreLibraryWithoutUnionFormattingConvenienceApis();
        var compilation = Compilation.Create(
            "reduced-core-union-formatting",
            [syntaxTree],
            [coreLibrary],
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithEmbedCoreTypes(true)
                .WithFrameworkProjectionMode(FrameworkProjectionMode.None)
                .WithSynthesizeStructuralToString(false));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.NotEqual(0, peStream.Length);

        peStream.Position = 0;
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(peStream);
        var unionTypes = assembly.MainModule.Types
            .Where(type => type.Name == "TemperatureState")
            .SelectMany(type => new[] { type }.Concat(type.NestedTypes))
            .ToArray();
        Assert.NotEmpty(unionTypes);
        Assert.All(
            unionTypes,
            type => Assert.DoesNotContain(
                type.Methods,
                method => method.Name is nameof(object.ToString) or
                    SynthesizedUnionMethodNames.DisplayNameHelper or
                    SynthesizedUnionMethodNames.FriendlyTypeNameHelper or
                    SynthesizedUnionMethodNames.FormatValueHelper));
    }

    [Fact]
    public void Union_ImplementingInterface_EmitsInterfaceAndCallableMember()
    {
        const string code = """
public interface IFailure {
    func Describe() -> string
}

public union Failure: IFailure {
    case Unknown

    public func Describe() -> string => "unknown failure"
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "union-interface",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var interfaceType = assembly.GetType("IFailure", throwOnError: true)!;
        var unionType = assembly.GetType("Failure", throwOnError: true)!;
        var value = Activator.CreateInstance(unionType)!;

        Assert.True(interfaceType.IsAssignableFrom(unionType));
        Assert.Equal("unknown failure", interfaceType.GetMethod("Describe")!.Invoke(value, null));
    }

    private static MetadataReference CreateCoreLibraryWithoutUnionFormattingConvenienceApis()
    {
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        var coreLibraryPath = TargetFrameworkResolver
            .GetReferenceAssemblies(version)
            .Single(path => Path.GetFileName(path) == "System.Runtime.dll");
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            coreLibraryPath,
            new Mono.Cecil.ReaderParameters
            {
                InMemory = true,
                ReadingMode = Mono.Cecil.ReadingMode.Immediate
            });

        var typeType = assembly.MainModule.GetType("System.Type");
        var isPrimitive = typeType.Properties.Single(property => property.Name == nameof(Type.IsPrimitive));
        typeType.Properties.Remove(isPrimitive);
        if (isPrimitive.GetMethod is not null)
            typeType.Methods.Remove(isPrimitive.GetMethod);

        var stringType = assembly.MainModule.GetType("System.String");
        var stringReplace = stringType.Methods.Single(method =>
            method.Name == nameof(string.Replace) &&
            method.Parameters.Count == 2 &&
            method.Parameters.All(parameter => parameter.ParameterType.FullName == "System.String"));
        stringType.Methods.Remove(stringReplace);

        using var image = new MemoryStream();
        assembly.Write(image);
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    [Fact]
    public void UnionCaseConstructor_AssignsFields()
    {
        var code = """
union Option {
    case Some(value: int, label: string)
}

class Container {
    public func Create() -> Option {
        return Option.Some(value: 42, label: "ok")
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Activator.CreateInstance(containerType)!;

        var caseValue = createMethod.Invoke(instance, Array.Empty<object?>());
        Assert.NotNull(caseValue);
        Assert.Equal("Option", caseValue!.GetType().Name);
        Assert.Contains("Some", caseValue.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Union_DoesNotEmitImplicitConversionOperators()
    {
        const string code = """
union Option {
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "union-conversion-ctor",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var optionType = assembly.GetType("Option", throwOnError: true)!;
        var conversionMethods = optionType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "op_Implicit")
            .ToArray();

        Assert.Empty(conversionMethods);
    }

    [Fact]
    public void PublicUnionCases_AreEmittedAsPublicTypes()
    {
        const string code = """
import System.*

public union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "public-union",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;

        var okCase = assembly.GetType("Result+Ok`1", throwOnError: true)!.MakeGenericType(typeof(int));
        Assert.True(okCase.IsNestedPublic);
        var okCtor = okCase.GetConstructor(BindingFlags.Public | BindingFlags.Instance, binder: null, new[] { typeof(int) }, modifiers: null);
        Assert.NotNull(okCtor);

        var errorCase = assembly.GetType("Result+Error", throwOnError: true)!;
        Assert.True(errorCase.IsNestedPublic);
        var errorCtor = errorCase.GetConstructor(BindingFlags.Public | BindingFlags.Instance, binder: null, new[] { typeof(string) }, modifiers: null);
        Assert.NotNull(errorCtor);
    }

    [Fact]
    public void GenericOptionCases_PreservePayloadAndEmptyState()
    {
        const string code = """
import Option.*

class Runner {
    public static func Run(flag: bool) -> int {
        let input: Option<int> = if flag { .Some(42) } else { .None }
        let output: Option<int> = match input {
            Some(let value) => Option<int>.Some(value)
            None => None
        }
        return match output {
            .Some(let value) => value
            .None => -1
        }
    }
}

union Option<T> {
    case Some(value: T)
    case None
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "generic-option-none-create",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var runMethod = runnerType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        var fromSome = runMethod.Invoke(null, [true]);
        var fromNone = runMethod.Invoke(null, [false]);

        Assert.Equal(42, fromSome);
        Assert.Equal(-1, fromNone);
    }

    [Fact]
    public void GenericUnionErrorSubtypeConversion_RunsSuccessfully()
    {
        const string code = """
import System.*
import Result.*

class Runner {
    public static func Run() -> string {
        let result: Result<string, Exception> = Error(InvalidOperationException("x"))
        return match result {
            Error(let e) => e.GetType().Name
            Ok(let value) => value
        }
    }
}

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "generic-union-error-subtype",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var runMethod = runnerType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var output = runMethod.Invoke(null, Array.Empty<object?>());
        Assert.Equal("InvalidOperationException", output);
    }

    [Fact]
    public void InternalUnionCases_AreEmittedWithInternalAccessibility()
    {
        const string code = """
union Hidden<T> {
    case Case(value: T)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "internal-union",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;

        var caseType = assembly.GetType("Hidden+Case`1", throwOnError: true)!.MakeGenericType(typeof(int));
        Assert.True(caseType.IsNestedAssembly);
        var ctor = caseType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, binder: null, new[] { typeof(int) }, modifiers: null);
        Assert.NotNull(ctor);
    }

    [Fact]
    public void DiscriminatedUnionStruct_AnnotatedWithMarkerAttribute()
    {
        var code = """
union Option {
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;

        Assert.Contains(
            unionType.GetCustomAttributesData(),
            a => a.AttributeType.FullName == "System.Runtime.CompilerServices.UnionAttribute");
    }

    [Fact]
    public void DiscriminatedUnion_ImplementsGeneratedIUnionWhenRuntimeDoesNotProvideIt()
    {
        var code = """
union Option {
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion("net10.0");
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;
        var generatedInterface = runtimeAssembly.GetType("System.Runtime.CompilerServices.IUnion", throwOnError: true)!;

        Assert.Contains(generatedInterface, unionType.GetInterfaces());
        Assert.Same(runtimeAssembly, generatedInterface.Assembly);
    }

    [Fact]
    public void DiscriminatedUnion_UsesRuntimeIUnionWhenRuntimeProvidesIt()
    {
        if (typeof(object).Assembly.GetType("System.Runtime.CompilerServices.IUnion", throwOnError: false) is null)
            return;

        var code = """
union Option {
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion("net11.0");
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var peReader = new PEReader(ImmutableArray.Create(peStream.ToArray()));
        var metadata = peReader.GetMetadataReader();
        var optionDefinition = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type => metadata.StringComparer.Equals(type.Name, "Option"));

        Assert.Contains(optionDefinition.GetInterfaceImplementations(), handle =>
        {
            var implementation = metadata.GetInterfaceImplementation(handle);
            return IsMetadataType(metadata, implementation.Interface, "System.Runtime.CompilerServices", "IUnion");
        });
        Assert.DoesNotContain(metadata.TypeDefinitions, handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return IsMetadataType(metadata, type, "System.Runtime.CompilerServices", "IUnion");
        });
    }

    [Fact]
    public void DiscriminatedUnion_Net10UsesRavenCoreUnionContractsInsteadOfHostRuntimeTypes()
    {
        var syntaxTree = SyntaxTree.ParseText(
            """
union Option {
    case Some(value: int)
}
""");
        var version = TargetFrameworkResolver.ResolveVersion("net10.0");
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path)),
            CreateNet10RavenCoreUnionReference()
        ];
        var compilation = Compilation.Create(
            "net10-raven-core-union",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var peReader = new PEReader(ImmutableArray.Create(peStream.ToArray()));
        var metadata = peReader.GetMetadataReader();
        var optionDefinition = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type => metadata.StringComparer.Equals(type.Name, "Option"));
        var unionReferenceHandle = optionDefinition.GetInterfaceImplementations()
            .Select(metadata.GetInterfaceImplementation)
            .Select(static implementation => implementation.Interface)
            .Single(handle => IsMetadataType(
                metadata,
                handle,
                "System.Runtime.CompilerServices",
                "IUnion"));
        var unionReference = metadata.GetTypeReference((TypeReferenceHandle)unionReferenceHandle);
        var assemblyReference = metadata.GetAssemblyReference((AssemblyReferenceHandle)unionReference.ResolutionScope);

        Assert.Equal("Raven.Core", metadata.GetString(assemblyReference.Name));

        var unionAttribute = optionDefinition.GetCustomAttributes()
            .Select(metadata.GetCustomAttribute)
            .Single(attribute =>
                attribute.Constructor.Kind == HandleKind.MemberReference &&
                IsMetadataType(
                    metadata,
                    metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                    "System.Runtime.CompilerServices",
                    "UnionAttribute"));
        var unionAttributeConstructor = metadata.GetMemberReference((MemberReferenceHandle)unionAttribute.Constructor);
        var unionAttributeType = metadata.GetTypeReference((TypeReferenceHandle)unionAttributeConstructor.Parent);
        var unionAttributeAssembly = metadata.GetAssemblyReference(
            (AssemblyReferenceHandle)unionAttributeType.ResolutionScope);

        Assert.Equal("Raven.Core", metadata.GetString(unionAttributeAssembly.Name));
    }

    private static MetadataReference CreateNet10RavenCoreUnionReference()
    {
        const string source = """
namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
    public sealed class UnionAttribute : System.Attribute
    {
    }

    public interface IUnion
    {
        object Value { get; }
    }
}
""";
        var version = TargetFrameworkResolver.ResolveVersion("net10.0");
        var references = TargetFrameworkResolver.GetReferenceAssemblies(version)
            .Where(File.Exists)
            .Select(static path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Raven.Core",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "raven-net10-core-fixture",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var assemblyPath = Path.Combine(outputDirectory, "Raven.Core.dll");
        Microsoft.CodeAnalysis.Emit.EmitResult emitResult;
        using (var assemblyStream = File.Create(assemblyPath))
            emitResult = compilation.Emit(assemblyStream);

        Assert.True(
            emitResult.Success,
            string.Join(Environment.NewLine, emitResult.Diagnostics));
        return MetadataReference.CreateFromFile(assemblyPath);
    }

    [Fact]
    public void UnionWithoutStorageModifier_EmitsValueTypeCarrier()
    {
        var code = """
union Option {
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;

        Assert.False(unionType.IsClass);
        Assert.True(unionType.IsValueType);
    }

    [Fact]
    public void UnionStruct_EmitsValueTypeCarrier()
    {
        var code = """
union struct Option {
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;

        Assert.True(unionType.IsValueType);
    }

    [Fact]
    public void ParenthesizedStructUnion_WithManagedReference_UsesSequentialLayout()
    {
        const string code = """
union Payment(int | string)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create(
            "managed-union-layout",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Payment", throwOnError: true)!;

        Assert.Equal(LayoutKind.Sequential, unionType.StructLayoutAttribute!.Value);
        Assert.NotNull(Activator.CreateInstance(unionType, [42]));
        Assert.NotNull(Activator.CreateInstance(unionType, ["card"]));
    }

    [Fact]
    public void GenericStructUnion_InstancePropertyCanPatternMatchSelf()
    {
        const string code = """
union Option<T> {
    case Some(value: T)
    case None

    val HasSome: bool => self is .Some(_)
}

class Harness {
    public static func Check() -> bool {
        let option: Option<int> = .Some(42)
        return option.HasSome
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create(
            "struct-union-self",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var harnessType = runtimeAssembly.GetType("Harness", throwOnError: true)!;
        var check = harnessType.GetMethod("Check", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(true, check.Invoke(null, null));
    }

    [Fact]
    public void Union_EmitsConventionalValueProperty()
    {
        var code = """
union Option {
    case None
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;
        var valueProperty = unionType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(valueProperty);
        Assert.Equal(typeof(object), valueProperty!.PropertyType);
        var nullability = new NullabilityInfoContext().Create(valueProperty);
        Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
    }

    [Fact]
    public void DefaultStructUnion_ValuePropertyReturnsNull()
    {
        var code = """
union struct Maybe<T> {
    case None
    case Some(value: T)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionTypeDefinition = runtimeAssembly.GetType("Maybe`1", throwOnError: true)!;
        var closedUnionType = unionTypeDefinition.MakeGenericType(typeof(int));
        var instance = Activator.CreateInstance(closedUnionType)!;
        var valueProperty = closedUnionType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)!;
        var hasValueProperty = closedUnionType.GetProperty("HasValue", BindingFlags.Instance | BindingFlags.Public)!;
        var nullability = new NullabilityInfoContext().Create(valueProperty);
        var value = valueProperty.GetValue(instance);

        Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
        Assert.Equal(false, hasValueProperty.GetValue(instance));
        Assert.Null(value);
    }

    [Fact]
    public void ClassUnion_WithNullableMember_EmitsNullableValueProperty()
    {
        var code = """
union class Maybe(string? | int)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Maybe", throwOnError: true)!;
        var valueProperty = unionType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)!;
        var nullability = new NullabilityInfoContext().Create(valueProperty);
        var stringConstructor = unionType.GetConstructor([typeof(string)])!;
        var instance = stringConstructor.Invoke([null]);

        Assert.Equal(typeof(object), valueProperty.PropertyType);
        Assert.Equal(NullabilityState.Nullable, nullability.ReadState);
        Assert.Null(valueProperty.GetValue(instance));
    }

    [Fact]
    public void ClassUnion_WithNullableMember_EmitsNullableCaseConstructors()
    {
        var code = """
import System.Collections.Generic.*

public union class JsonValue(string? | double | bool | JsonObject | JsonValue[])

public record JsonObject(Properties: IDictionary<string, JsonValue>)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("RavenJsonUnion", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("JsonValue", throwOnError: true)!;
        var objectType = runtimeAssembly.GetType("JsonObject", throwOnError: true)!;
        var constructors = unionType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Where(static constructor => constructor.GetParameters().Length == 1)
            .ToArray();
        var nullabilityContext = new NullabilityInfoContext();

        Assert.Equal(5, constructors.Length);
        AssertConstructor(typeof(string), NullabilityState.Nullable);
        AssertConstructor(typeof(double), NullabilityState.NotNull);
        AssertConstructor(typeof(bool), NullabilityState.NotNull);
        AssertConstructor(objectType, NullabilityState.NotNull);
        AssertConstructor(unionType.MakeArrayType(), NullabilityState.NotNull);

        var directory = Path.Combine(Path.GetTempPath(), $"raven-json-union-csharp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var referencePath = Path.Combine(directory, "RavenJsonUnion.dll");
            File.WriteAllBytes(referencePath, peStream.ToArray());

            File.WriteAllText(
                Path.Combine(directory, "CSharpConsumer.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{{TestTargetFramework.Default}}</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="RavenJsonUnion">
                      <HintPath>{{referencePath}}</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(directory, "UseUnion.cs"),
                """
                public static class UseUnion
                {
                    public static object? ReadNull()
                    {
                        var value = new JsonValue((string?)null);
                        return value.Value;
                    }
                }
                """);

            var build = RunDotnet(["build", "/property:WarningLevel=0", "-v:minimal"], directory);
            Assert.True(build.ExitCode == 0, build.Output);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        void AssertConstructor(Type parameterType, NullabilityState expectedNullability)
        {
            var constructor = Assert.Single(constructors, constructor => constructor.GetParameters()[0].ParameterType == parameterType);
            var parameter = constructor.GetParameters()[0];
            var nullability = nullabilityContext.Create(parameter);
            Assert.Equal(expectedNullability, nullability.ReadState);
        }
    }

    [Fact]
    public void StructUnion_ConstructedWithNullablePayload_HasValueFollowsValueNullState()
    {
        var code = """
union struct Maybe<T>(T | int)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionTypeDefinition = runtimeAssembly.GetType("Maybe`1", throwOnError: true)!;
        var closedUnionType = unionTypeDefinition.MakeGenericType(typeof(string));
        var instance = closedUnionType.GetConstructor([typeof(string)])!.Invoke([null]);
        var valueProperty = closedUnionType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)!;
        var hasValueProperty = closedUnionType.GetProperty("HasValue", BindingFlags.Instance | BindingFlags.Public)!;

        Assert.Equal(false, hasValueProperty.GetValue(instance));
        Assert.Null(valueProperty.GetValue(instance));
    }

    [Fact]
    public void ClassUnion_WithNullableValuePayload_MatchesNullAndConstantPatterns()
    {
        var code = """
class Runner {
    public static func NullCase() -> int {
        let v: Foo = (int?)null
        return match v {
            3 => 30
            int i => i
            null => -1
            _ => -2
        }
    }

    public static func ConstantCase() -> int {
        let v: Foo = 3
        return match v {
            3 => 30
            int i => i
            null => -1
            _ => -2
        }
    }

    public static func IntCase() -> int {
        let v: Foo = 42
        return match v {
            3 => 30
            int i => i
            null => -1
            _ => -2
        }
    }
}

union class Foo(int? | string)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var runnerType = runtimeAssembly.GetType("Runner", throwOnError: true)!;

        Assert.Equal(-1, runnerType.GetMethod("NullCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(30, runnerType.GetMethod("ConstantCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(42, runnerType.GetMethod("IntCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void ClassUnion_WithNullableValuePayload_MatchExpressionAndStatementUseValueNullState()
    {
        var code = """
class Runner {
    public static func NullValueIsNull() -> bool {
        let v: Foo = (int?)null
        return v.Value is null
    }

    public static func NullHasValue() -> bool {
        let v: Foo = (int?)null
        return v.HasValue
    }

    public static func ExpressionNullCase() -> int {
        let v: Foo = (int?)null
        return match v {
            int i => i
            null => -1
            _ => -2
        }
    }

    public static func ExpressionValueCase() -> int {
        let v: Foo = 42
        return match v {
            int i => i
            null => -1
            _ => -2
        }
    }

    public static func StatementNullCase() -> int {
        let v: Foo = (int?)null
        match v {
            int i => i
            null => -1
            _ => -2
        }
    }

    public static func StatementValueCase() -> int {
        let v: Foo = 42
        match v {
            int i => i
            null => -1
            _ => -2
        }
    }
}

union class Foo(int? | string)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var runnerType = runtimeAssembly.GetType("Runner", throwOnError: true)!;

        Assert.Equal(true, runnerType.GetMethod("NullValueIsNull", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(false, runnerType.GetMethod("NullHasValue", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(-1, runnerType.GetMethod("ExpressionNullCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(42, runnerType.GetMethod("ExpressionValueCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(-1, runnerType.GetMethod("StatementNullCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(42, runnerType.GetMethod("StatementValueCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void ClassUnion_WithNullableValuePayload_IsDeclarationPatternExtractsMember()
    {
        var code = """
class Runner {
    public static func IntCase() -> int {
        let v: Foo = 42
        if v is int i {
            return i
        }

        return -1
    }

    public static func NullCase() -> int {
        let v: Foo = (int?)null
        if v is int i {
            return i
        }

        return -1
    }
}

union class Foo(int? | string)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var runnerType = runtimeAssembly.GetType("Runner", throwOnError: true)!;

        Assert.Equal(42, runnerType.GetMethod("IntCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal(-1, runnerType.GetMethod("NullCase", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void DefaultStructUnion_CatchAllPatternMatchesInactiveState()
    {
        var code = """
class Runner {
    public static func DescribeDefault() -> string {
        let value: Maybe<int> = default
        return match value {
            .Some(let payload) => payload.ToString()
            .None => "none"
            _ => "inactive"
        }
    }

    public static func DescribeSome() -> string {
        let value: Maybe<int> = .Some(42)
        return match value {
            .Some(let payload) => payload.ToString()
            .None => "none"
            _ => "inactive"
        }
    }
}

union struct Maybe<T> {
    case None
    case Some(value: T)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var runnerType = runtimeAssembly.GetType("Runner", throwOnError: true)!;
        var defaultMethod = runnerType.GetMethod("DescribeDefault", BindingFlags.Public | BindingFlags.Static)!;
        var someMethod = runnerType.GetMethod("DescribeSome", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal("inactive", defaultMethod.Invoke(null, Array.Empty<object?>()));
        Assert.Equal("42", someMethod.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void ActiveStructUnion_PassedAsArgumentPreservesActiveState()
    {
        var code = """
class Runner {
    public static func Describe(value: Maybe<int>) -> string {
        return match value {
            .Some(let payload) => payload.ToString()
            .None => "none"
            _ => "inactive"
        }
    }

    public static func DescribeNoneArgument() -> string {
        return Describe(.None)
    }

    public static func DescribeSomeArgument() -> string {
        return Describe(.Some(42))
    }
}

union Maybe<T> {
    case None
    case Some(value: T)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var runnerType = runtimeAssembly.GetType("Runner", throwOnError: true)!;

        Assert.Equal("none", runnerType.GetMethod("DescribeNoneArgument", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal("42", runnerType.GetMethod("DescribeSomeArgument", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void NominalUnion_EmitsConstructorsForMemberTypes()
    {
        var code = """
record Left(value: int)
record Right(message: string)

union Either(Left | Right)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var eitherType = runtimeAssembly.GetType("Either", throwOnError: true)!;
        var leftType = runtimeAssembly.GetType("Left", throwOnError: true)!;
        var rightType = runtimeAssembly.GetType("Right", throwOnError: true)!;

        Assert.NotNull(eitherType.GetConstructor([leftType]));
        Assert.NotNull(eitherType.GetConstructor([rightType]));
    }

    [Fact]
    public void GenericParenthesizedUnion_ConstructorsAssignCarrierState()
    {
        const string code = """
class Runner {
    public static func Left() -> Either<int, string> {
        return 42
    }

    public static func Right() -> Either<int, string> {
        return "invoice"
    }
}

union Either<T1, T2>(T1 | T2)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "generic-parenthesized-union",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var leftMethod = runnerType.GetMethod("Left", BindingFlags.Public | BindingFlags.Static)!;
        var rightMethod = runnerType.GetMethod("Right", BindingFlags.Public | BindingFlags.Static)!;

        var left = leftMethod.Invoke(null, Array.Empty<object?>());
        var right = rightMethod.Invoke(null, Array.Empty<object?>());

        Assert.Equal("Either(42)", left?.ToString());
        Assert.Equal("Either(\"invoice\")", right?.ToString());
    }

    [Fact]
    public void ParenthesizedUnion_ToString_UsesUnionNameAndEscapesStringPayload()
    {
        const string code = """
class Runner {
    public static func Value() -> Message {
        return "a\"b"
    }
}

union Message(string | int)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "parenthesized-union-tostring",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var valueMethod = runnerType.GetMethod("Value", BindingFlags.Public | BindingFlags.Static)!;

        var value = valueMethod.Invoke(null, Array.Empty<object?>());

        Assert.NotNull(value);
        Assert.Equal("Message(\"a\\\"b\")", value!.ToString());
    }

    [Fact]
    public void Union_ToStringOverride_UsesDeclaredImplementation()
    {
        const string code = """
union class Result {
    case Ok(value: int)

    override func ToString() -> string? => "custom"
}

class Runner {
    public static func Run() -> string? {
        let result: Result = .Ok(42)
        return result.ToString()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "union-tostring-override",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var runnerType = loaded.Assembly.GetType("Runner", throwOnError: true)!;
        var runMethod = runnerType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal("custom", runMethod.Invoke(null, null));
    }

    [Fact]
    public void ParenthesizedUnion_ToString_UsesPayloadToStringWithoutReflection()
    {
        const string code = """
import System.Collections.Generic.*

class Runner {
    public static func Value() -> MyResult<string> {
        return List<string>()
    }
}

union MyResult<T>(List<T> | int)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "parenthesized-union-friendly-type-names",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var valueMethod = runnerType.GetMethod("Value", BindingFlags.Public | BindingFlags.Static)!;

        var value = valueMethod.Invoke(null, Array.Empty<object?>());

        Assert.NotNull(value);
        Assert.Equal("MyResult(System.Collections.Generic.List`1[System.String])", value!.ToString());
    }

    [Fact]
    public void GenericParenthesizedUnion_ExplicitCastExtractsMemberType()
    {
        const string code = """
import System.*

class Runner {
    public static func Left() -> int {
        let value: Either<int, string> = 42
        return (int)value
    }

    public static func Invalid() -> string {
        let value: Either<int, string> = 42
        return (string)value
    }
}

union Either<T1, T2>(T1 | T2)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "generic-parenthesized-union-cast",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var leftMethod = runnerType.GetMethod("Left", BindingFlags.Public | BindingFlags.Static)!;
        var invalidMethod = runnerType.GetMethod("Invalid", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(42, leftMethod.Invoke(null, Array.Empty<object?>()));
        Assert.Throws<TargetInvocationException>(() => invalidMethod.Invoke(null, Array.Empty<object?>()));
        try
        {
            invalidMethod.Invoke(null, Array.Empty<object?>());
        }
        catch (TargetInvocationException ex)
        {
            Assert.IsType<InvalidCastException>(ex.InnerException);
        }
    }

    [Fact]
    public void GenericParenthesizedUnion_TypePatternMatchExtractsMemberValue()
    {
        const string code = """
class Runner {
    public static func DescribeLeft() -> string {
        let value: Either<int, string> = 42
        return match value {
            int amount => "cash $amount"
            string reference => "card $reference"
        }
    }

    public static func DescribeRight() -> string {
        let value: Either<int, string> = "invoice"
        return match value {
            int amount => "cash $amount"
            string reference => "card $reference"
        }
    }
}

union Either<T1, T2>(T1 | T2)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "generic-parenthesized-union-match",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var leftMethod = runnerType.GetMethod("DescribeLeft", BindingFlags.Public | BindingFlags.Static)!;
        var rightMethod = runnerType.GetMethod("DescribeRight", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal("cash 42", leftMethod.Invoke(null, Array.Empty<object?>()));
        Assert.Equal("card invoice", rightMethod.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void GenericParenthesizedUnion_NestedGenericMemberTypesEmitConstructorsAndConstraints()
    {
        const string code = """
import System.Collections.Generic.*

union MyResult2<T>(List<T> | int)
    where T : class

union MyResult3(List<int> | string)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "generic-parenthesized-union-nested-members",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;

        var genericUnionDefinition = assembly.GetType("MyResult2`1", throwOnError: true)!;
        var genericParameter = genericUnionDefinition.GetGenericArguments().Single();
        Assert.True((genericParameter.GenericParameterAttributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0);

        var constructedGenericUnion = genericUnionDefinition.MakeGenericType(typeof(string));
        Assert.NotNull(constructedGenericUnion.GetConstructor([typeof(List<string>)]));
        Assert.NotNull(constructedGenericUnion.GetConstructor([typeof(int)]));

        var concreteUnion = assembly.GetType("MyResult3", throwOnError: true)!;
        Assert.NotNull(concreteUnion.GetConstructor([typeof(List<int>)]));
        Assert.NotNull(concreteUnion.GetConstructor([typeof(string)]));
    }

    [Fact]
    public void ParenthesizedUnion_NominalDeconstructionPatternExtractsMemberValue()
    {
        const string code = """
class Runner {
    public static func DescribeCash() -> string {
        let value = Payment(Cash(42.0m))
        return match value {
            Cash(let amount) => "cash $amount"
            Card(let reference) => "card $reference"
        }
    }

    public static func DescribeCard() -> string {
        let value = Payment(Card("invoice"))
        return match value {
            Cash(let amount) => "cash $amount"
            Card(let reference) => "card $reference"
        }
    }
}

record Cash(Amount: decimal)
record Card(Reference: string)

union Payment(Cash | Card)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "parenthesized-union-nominal-match",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var runnerType = assembly.GetType("Runner", throwOnError: true)!;
        var cashMethod = runnerType.GetMethod("DescribeCash", BindingFlags.Public | BindingFlags.Static)!;
        var cardMethod = runnerType.GetMethod("DescribeCard", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal("cash 42,0", cashMethod.Invoke(null, Array.Empty<object?>()));
        Assert.Equal("card invoice", cardMethod.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void DiscriminatedUnionCaseTypes_AreRecordedOnCarrierWithRavenMetadata()
    {
        var code = """
union Option {
    case None
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;
        Assert.DoesNotContain(runtimeAssembly.GetTypes(), type =>
            type.GetCustomAttributesData().Any(a =>
                a.AttributeType.FullName is
                    "System.Runtime.CompilerServices.UnionCaseAttribute" or
                    "System.Runtime.CompilerServices.DiscriminatedUnionCaseAttribute"));

        var caseAttributes = unionType
            .GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == "Raven.Runtime.CompilerServices.RavenUnionCaseAttribute")
            .OrderBy(a => (int)a.ConstructorArguments[2].Value!)
            .ToArray();

        Assert.Equal(2, caseAttributes.Length);
        Assert.Equal("None", caseAttributes[0].ConstructorArguments[1].Value);
        Assert.Equal(0, caseAttributes[0].ConstructorArguments[2].Value);
        Assert.Equal("Some", caseAttributes[1].ConstructorArguments[1].Value);
        Assert.Equal(1, caseAttributes[1].ConstructorArguments[2].Value);

        Assert.Equal("Option+None", caseAttributes[0].ConstructorArguments[0].Value);
        Assert.Equal("Option+Some", caseAttributes[1].ConstructorArguments[0].Value);
    }

    [Fact]
    public void DiscriminatedUnionConversion_SetsTagAndPayload()
    {
        var code = """
union Option {
    case None
    case Some(value: int)
}

class Container {
    public func Create() -> Option {
        return Option.Some(value: 42)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Activator.CreateInstance(containerType)!;

        var unionValue = createMethod.Invoke(instance, Array.Empty<object?>());
        Assert.NotNull(unionValue);

        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;
        var tagField = unionType.GetField("<Tag>", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payloadField = unionType.GetField("<SomePayload>", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal((byte)2, (byte)tagField.GetValue(unionValue)!);

        var payload = payloadField.GetValue(unionValue);
        Assert.NotNull(payload);

        var caseType = runtimeAssembly.GetType("Option+Some", throwOnError: true)!;
        Assert.Equal(caseType, payload!.GetType());

        var valueProperty = caseType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal(42, (int)valueProperty.GetValue(payload)!);
    }

    [Fact]
    public void UnitPayloadCaseShorthand_ConstructsCaseBeforeImplicitUnionConversion()
    {
        const string code = """
union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class Container {
    public func Make() -> Result<(), string> {
        .Ok
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "unit-case-shorthand",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;
        var containerType = assembly.GetType("Container", throwOnError: true)!;
        var makeMethod = containerType.GetMethod("Make", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Activator.CreateInstance(containerType)!;

        var unionValue = makeMethod.Invoke(instance, Array.Empty<object?>());
        Assert.NotNull(unionValue);

        var unionType = unionValue!.GetType();
        Assert.Equal("Result`2", unionType.Name);
        Assert.Equal("Unit", unionType.GetGenericArguments()[0].Name);
    }

    [Fact]
    public void DiscriminatedUnion_UsesCaseTypedConstructors()
    {
        var code = """
union Option {
    case None
    case Some(value: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Option", throwOnError: true)!;
        var caseType = runtimeAssembly.GetType("Option+Some", throwOnError: true)!;

        var unionCtor = unionType.GetConstructor(new[] { caseType })!;

        var ctor = caseType.GetConstructor(new[] { typeof(int) })!;
        var caseInstance = ctor.Invoke(new object?[] { 7 });

        var unionValue = unionCtor.Invoke(new[] { caseInstance });
        Assert.NotNull(unionValue);

        var tagField = unionType.GetField("<Tag>", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payloadField = unionType.GetField("<SomePayload>", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal((byte)2, (byte)tagField.GetValue(unionValue)!);

        var payload = payloadField.GetValue(unionValue);
        Assert.NotNull(payload);
        Assert.Equal(caseType, payload!.GetType());
    }

    [Fact]
    public void DiscriminatedUnion_UsesExplicitLayoutAndOffsets()
    {
        var code = """
union Result {
    case Ok(value: int)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Result", throwOnError: true)!;

        var layout = unionType.StructLayoutAttribute;
        Assert.NotNull(layout);
        Assert.Equal(LayoutKind.Sequential, layout!.Value);
    }

    [Fact]
    public void DiscriminatedUnion_GenericUsesSequentialLayout()
    {
        var code = """
union Option<T> {
    case Some(value: T)
    case None
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionDefinition = runtimeAssembly.GetType("Option`1", throwOnError: true)!;
        var unionType = unionDefinition.MakeGenericType(typeof(int));

        var layout = unionType.StructLayoutAttribute;
        Assert.NotNull(layout);
        Assert.Equal(LayoutKind.Sequential, layout!.Value);
    }

    [Fact]
    public void DiscriminatedUnionConversion_DoesNotAllocate()
    {
        var code = """
import System.*

union Option {
    case Some(value: int)
    case None
}

class Container {
    public static func Measure() -> long {
        let before = GC.GetAllocatedBytesForCurrentThread()
        let opt: Option = .Some(123)
        let after = GC.GetAllocatedBytesForCurrentThread()
        return after - before
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var measureMethod = containerType.GetMethod("Measure", BindingFlags.Public | BindingFlags.Static)!;

        var allocated = (long)measureMethod.Invoke(null, Array.Empty<object?>())!;
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void DiscriminatedUnion_EmitsTryGetMethods()
    {
        var code = """
union Result {
    case Ok(value: int)
    case Error(message: string)
}

class Container {
    public func GetOk() -> Result {
        return Result.Ok(value: 7)
    }

    public func GetError() -> Result {
        return Result.Error(message: "boom")
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Result", throwOnError: true)!;
        var okCaseType = runtimeAssembly.GetType("Result+Ok", throwOnError: true)!;
        var errorCaseType = runtimeAssembly.GetType("Result+Error", throwOnError: true)!;
        var okTryGetMethod = unionType.GetMethod(
            "TryGetValue",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { okCaseType.MakeByRefType() },
            modifiers: null)!;
        var errorTryGetMethod = unionType.GetMethod(
            "TryGetValue",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: new[] { errorCaseType.MakeByRefType() },
            modifiers: null)!;
        Assert.Null(unionType.GetMethod("TryGetOk", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(unionType.GetMethod("TryGetError", BindingFlags.Public | BindingFlags.Instance));

        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var container = Activator.CreateInstance(containerType)!;
        var getOkMethod = containerType.GetMethod("GetOk", BindingFlags.Public | BindingFlags.Instance)!;
        var getErrorMethod = containerType.GetMethod("GetError", BindingFlags.Public | BindingFlags.Instance)!;

        var okUnionValue = getOkMethod.Invoke(container, Array.Empty<object?>());
        var okArgs = new object?[] { null };
        var okResult = (bool)okTryGetMethod.Invoke(okUnionValue, okArgs)!;
        Assert.True(okResult);
        var okValueProperty = okCaseType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal(7, (int)okValueProperty.GetValue(okArgs[0])!);

        var errorUnionValue = getErrorMethod.Invoke(container, Array.Empty<object?>());
        var errorArgs = new object?[] { null };
        var errorResult = (bool)errorTryGetMethod.Invoke(errorUnionValue, errorArgs)!;
        Assert.True(errorResult);
        var errorMessageProperty = errorCaseType.GetProperty("Message", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal("boom", (string)errorMessageProperty.GetValue(errorArgs[0])!);

        var mismatchArgs = new object?[] { null };
        var mismatchResult = (bool)errorTryGetMethod.Invoke(okUnionValue, mismatchArgs)!;
        Assert.False(mismatchResult);
    }

    [Fact]
    public void DiscriminatedUnionCase_EmitsSynthesizedDeconstruct()
    {
        var code = """
union Result {
    case Ok(value: int)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var okCaseType = runtimeAssembly.GetType("Result+Ok", throwOnError: true)!;
        var deconstructMethod = okCaseType.GetMethod(
            "Deconstruct",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(int).MakeByRefType()],
            modifiers: null)!;

        var okValue = Activator.CreateInstance(okCaseType, [7])!;
        var args = new object?[] { 0 };
        deconstructMethod.Invoke(okValue, args);

        Assert.Equal(7, (int)args[0]!);
    }

    [Fact]
    public void DiscriminatedUnionCase_EmitsSynthesizedPropertyGetter()
    {
        var code = """
union Result {
    case Ok(value: int)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var okCaseType = runtimeAssembly.GetType("Result+Ok", throwOnError: true)!;
        var okValue = Activator.CreateInstance(okCaseType, [7])!;
        var valueProperty = okCaseType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!;

        Assert.Equal(7, (int)valueProperty.GetValue(okValue)!);
    }

    [Fact]
    public void GenericUnionCaseConstruction_PreservesOuterTypeArguments()
    {
        var code = """
union Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}

class Container {
    public func Create() -> Result<int, string> {
        return Result<int, string>.Error(message: "boom")
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Activator.CreateInstance(containerType)!;

        var caseValue = createMethod.Invoke(instance, Array.Empty<object?>());
        Assert.NotNull(caseValue);
        Assert.Equal("Result`2", caseValue!.GetType().Name);
        Assert.Contains("Error", caseValue.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GenericUnionCases_OnlyCaptureUsedTypeParameters()
    {
        var code = """
union Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
    case Pair(left: T, right: E)
    case None
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;

        Assert.Single(runtimeAssembly.GetType("Result+Ok`1", throwOnError: true)!.GetGenericArguments());
        Assert.Single(runtimeAssembly.GetType("Result+Error`1", throwOnError: true)!.GetGenericArguments());
        Assert.Equal(2, runtimeAssembly.GetType("Result+Pair`2", throwOnError: true)!.GetGenericArguments().Length);
        Assert.Empty(runtimeAssembly.GetType("Result+None", throwOnError: true)!.GetGenericArguments());
    }

    [Fact]
    public void GenericDiscriminatedUnionConversion_ClosesTypeArguments()
    {
        var code = """
union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}

class Container {
    public static func CreateOk() -> Result<int> {
        return .Ok(99)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("CreateOk", BindingFlags.Public | BindingFlags.Static)!;

        var unionValue = createMethod.Invoke(null, Array.Empty<object?>());
        Assert.NotNull(unionValue);

        var unionTypeDefinition = runtimeAssembly.GetType("Result`1", throwOnError: true)!;
        var caseTypeDefinition = runtimeAssembly.GetType("Result+Ok`1", throwOnError: true)!;
        var closedUnionType = unionTypeDefinition.MakeGenericType(typeof(int));
        var closedCaseType = caseTypeDefinition.MakeGenericType(typeof(int));

        Assert.Equal(closedUnionType, unionValue!.GetType());
        Assert.False(closedUnionType.IsAssignableFrom(closedCaseType));

        var ctor = closedCaseType.GetConstructor(new[] { typeof(int) })!;
        var caseInstance = ctor.Invoke(new object?[] { 7 });
        Assert.False(closedUnionType.IsInstanceOfType(caseInstance));

        var carrierCtor = closedUnionType.GetConstructor(new[] { closedCaseType })!;
        Assert.IsType(closedUnionType, carrierCtor.Invoke([caseInstance]));
    }

    [Fact]
    public void UnionCaseToString_FormatsUnionNameAndParameters()
    {
        var code = """
union Shape {
    case Rectangle(width: int, height: int)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var caseType = runtimeAssembly.GetType("Shape+Rectangle", throwOnError: true)!;

        var ctor = caseType.GetConstructor(new[] { typeof(int), typeof(int) })!;
        var caseInstance = ctor.Invoke(new object?[] { 3, 6 });
        var toString = caseType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance)!;

        var text = (string)toString.Invoke(caseInstance, Array.Empty<object?>())!;
        Assert.Equal("Shape.Rectangle(Width=3, Height=6)", text);
    }

    [Fact]
    public void UnionToString_IndicatesActiveCase()
    {
        var code = """
union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}

class Container {
    public static func Create() -> Result<int> {
        return .Ok(5)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;

        var unionValue = createMethod.Invoke(null, Array.Empty<object?>());
        Assert.NotNull(unionValue);

        var unionTypeDefinition = runtimeAssembly.GetType("Result`1", throwOnError: true)!;
        var closedUnionType = unionTypeDefinition.MakeGenericType(typeof(int));
        Assert.Equal(closedUnionType, unionValue!.GetType());

        var toString = closedUnionType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance)!;
        var text = (string)toString.Invoke(unionValue, Array.Empty<object?>())!;

        Assert.Equal("Result.Ok(5)", text);
    }

    [Fact]
    public void GenericUnionCaseToString_DirectCaseInstance_FormatsCapturedTypeArguments()
    {
        var code = """
union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var okCaseType = runtimeAssembly.GetType("Result+Ok`1", throwOnError: true)!.MakeGenericType(typeof(int));
        var okCase = okCaseType.GetConstructor(new[] { typeof(int) })!.Invoke([42]);

        Assert.Equal("Result.Ok(42)", okCase!.ToString());
    }

    [Fact]
    public void GenericUnionCaseToString_DoesNotEmitReflectionHelpers()
    {
        var code = """
union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        peStream.Position = 0;
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(peStream);
        Assert.DoesNotContain(
            assembly.MainModule.GetTypeReferences(),
            type => type.Namespace == "System.Reflection" || type.FullName == "System.Type");

        var unionTypes = assembly.MainModule.Types
            .Where(type => type.Name == "Result`1")
            .SelectMany(type => new[] { type }.Concat(type.NestedTypes))
            .ToArray();
        Assert.NotEmpty(unionTypes);
        Assert.All(
            unionTypes,
            type => Assert.DoesNotContain(
                type.Methods,
                method => method.Name is SynthesizedUnionMethodNames.DisplayNameHelper or
                    SynthesizedUnionMethodNames.FriendlyTypeNameHelper));
    }

    [Fact]
    public void UnionMemberCaseInvocation_InLambda_ReturnsExpectedResult()
    {
        var code = """
import System.*

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class Container {
    public static func Build() -> Result<int, string> {
        let factory: Func<int, Result<int, string>> = x => Result.Ok(x)
        return factory(42)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var buildMethod = containerType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!;

        var unionValue = buildMethod.Invoke(null, Array.Empty<object?>());
        Assert.NotNull(unionValue);
        Assert.Equal("Result.Ok(42)", unionValue!.ToString());
    }

    [Fact]
    public void UnionCaseCanonicalForms_EmitEquivalentRuntimeValues()
    {
        var code = """
import Result.*

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class Container {
    public static func CaseValue() -> Ok<int> {
        return Ok(2)
    }

    public static func CaseValueExplicit() -> Ok<int> {
        return Ok<int>(2)
    }

    public static func CarrierQualified() -> Result<int, string> {
        return Result<int, string>.Ok(2)
    }

    public static func CarrierTargetTyped() -> Result<int, string> {
        let value: Result<int, string> = .Ok(2)
        return value
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;

        var caseValue = containerType.GetMethod("CaseValue", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>());
        var caseValueExplicit = containerType.GetMethod("CaseValueExplicit", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>());
        var carrierQualified = containerType.GetMethod("CarrierQualified", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>());
        var carrierTargetTyped = containerType.GetMethod("CarrierTargetTyped", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>());

        Assert.NotNull(caseValue);
        Assert.NotNull(caseValueExplicit);
        Assert.NotNull(carrierQualified);
        Assert.NotNull(carrierTargetTyped);

        Assert.Equal("Result.Ok(2)", caseValue!.ToString());
        Assert.Equal("Result.Ok(2)", caseValueExplicit!.ToString());
        Assert.Equal("Result.Ok(2)", carrierQualified!.ToString());
        Assert.Equal("Result.Ok(2)", carrierTargetTyped!.ToString());
    }

    [Fact]
    public void UnnamedUnionCasePayloads_ExecuteThroughPropertiesAndPatterns()
    {
        const string code = """
union Payload {
    case Single(int)
    case Pair(int, string)
}

class Container {
    public static func Describe() -> string {
        let payload: Payload = .Pair(8, "eight")
        return payload match {
            .Single(let value) => value.ToString()
            .Pair(let first, let second) => "$first:$second"
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "unnamed-union-payloads",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var container = loaded.Assembly.GetType("Container", throwOnError: true)!;

        Assert.Equal("8:eight", container.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null));
    }

    [Fact]
    public void TargetTypedParameterlessUnionCase_EmitsInNestedConstructorArgument()
    {
        var code = """
import Option.*

union Option<T> {
    case Some(value: T)
    case None
}

record User(Profile: Option<Profile>)
record Profile(Settings: Option<Settings>)
record Settings(Theme: Option<Theme>)
record Theme(PrimaryColor: Option<string>)

class Container {
    public static func DescribeMissing() -> string {
        let theme: Option<Theme> = Some(Theme(None))
        let settings: Option<Settings> = Some(Settings(theme))
        let profile: Option<Profile> = Some(Profile(settings))
        let value: Option<User> = Some(User(profile))
        return describe(value)
    }

    public static func DescribeTargetTypedMissing() -> string {
        let theme: Option<Theme> = Some(Theme(.None))
        let settings: Option<Settings> = Some(Settings(theme))
        let profile: Option<Profile> = Some(Profile(settings))
        let value: Option<User> = Some(User(profile))
        return describe(value)
    }

    public static func DescribePresent() -> string {
        let color: Option<string> = Some("blue")
        let theme: Option<Theme> = Some(Theme(color))
        let settings: Option<Settings> = Some(Settings(theme))
        let profile: Option<Profile> = Some(Profile(settings))
        let value: Option<User> = Some(User(profile))
        return describe(value)
    }

    static func describe(user: Option<User>) -> string {
        return match user {
            Some(User(Profile: Some(Profile(Settings: Some(Settings(Theme: Some(Theme(PrimaryColor: Some(let color)))))))) => "Primary color: $color"
            _ => "Could not access primary color"
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;

        Assert.Equal("Could not access primary color", containerType.GetMethod("DescribeMissing", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal("Could not access primary color", containerType.GetMethod("DescribeTargetTypedMissing", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
        Assert.Equal("Primary color: blue", containerType.GetMethod("DescribePresent", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, Array.Empty<object?>()));
    }

    [Fact]
    public void UnionCaseToString_HandlesGenericValueTypes()
    {
        var code = """
union Result<T> {
    case Ok(value: T)
    case Error(message: string)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;

        var caseType = runtimeAssembly.GetType("Result+Ok`1", throwOnError: true)!.MakeGenericType(typeof(int));

        var ctor = caseType.GetConstructor(new[] { typeof(int) })!;
        var caseInstance = ctor.Invoke(new object?[] { 99 });
        var toString = caseType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance)!;

        var text = (string)toString.Invoke(caseInstance, Array.Empty<object?>())!;
        Assert.Equal("Result.Ok(99)", text);
    }

    [Fact]
    public void UnionToString_EscapesStringValues()
    {
        var code = """
union Shape {
    case Label(text: string)
}

class Container {
    public static func Create() -> Shape {
        return .Label("a\"b")
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Shape", throwOnError: true)!;
        var caseType = runtimeAssembly.GetType("Shape+Label", throwOnError: true)!;

        var ctor = caseType.GetConstructor(new[] { typeof(string) })!;
        var caseInstance = ctor.Invoke(new object?[] { "a\"b" });
        var caseToString = caseType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance)!;
        var caseText = (string)caseToString.Invoke(caseInstance, Array.Empty<object?>())!;
        Assert.Equal("Shape.Label(\"a\\\"b\")", caseText);

        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        var unionValue = createMethod.Invoke(null, Array.Empty<object?>());
        Assert.NotNull(unionValue);

        var unionToString = unionType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance)!;
        var unionText = (string)unionToString.Invoke(unionValue, Array.Empty<object?>())!;
        Assert.Equal("Shape.Label(\"a\\\"b\")", unionText);
    }

    [Fact]
    public void UnionToString_QuotesGenericStringPayload()
    {
        var code = """
union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

record CustomError(message: string)

class Container {
    public static func Create() -> Result<string, CustomError> {
        return Result<string, CustomError>.Ok("Foo")
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        var unionValue = createMethod.Invoke(null, Array.Empty<object?>());

        Assert.NotNull(unionValue);

        var text = unionValue!.ToString();
        Assert.Equal("Result.Ok(\"Foo\")", text);
    }

    [Fact]
    public void UnionToString_QuotesGenericCharPayload()
    {
        var code = """
class Container {
    public static func Create() -> Either<char, string> {
        return 'x'
    }
}

union Either<T1, T2>(T1 | T2)
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var createMethod = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        var unionValue = createMethod.Invoke(null, Array.Empty<object?>());

        Assert.NotNull(unionValue);
        Assert.Equal("Either('x')", unionValue!.ToString());
    }

    [Fact]
    public void UnionToString_ReturnsUninitializedWhenTagIsInvalid()
    {
        var code = """
union Maybe<T> {
    case None
    case Some(value: T)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionTypeDefinition = runtimeAssembly.GetType("Maybe`1", throwOnError: true)!;
        var closedUnionType = unionTypeDefinition.MakeGenericType(typeof(int));

        var instance = Activator.CreateInstance(closedUnionType)!;
        var tagField = closedUnionType.GetField("<Tag>", BindingFlags.Instance | BindingFlags.NonPublic)!;
        tagField.SetValue(instance, (byte)255);
        var toString = closedUnionType.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance)!;

        var text = (string)toString.Invoke(instance, Array.Empty<object?>())!;
        Assert.Equal("<Uninitialized>", text);
    }

    [Fact]
    public void DiscriminatedUnion_EmitsSingleToStringPerType()
    {
        var code = """
import System.Console.*

union Test {
    case Something(value: string)
    case Nothing
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var unionType = runtimeAssembly.GetType("Test", throwOnError: true)!;

        var unionToStrings = unionType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(object.ToString));
        Assert.Single(unionToStrings);

        foreach (var caseType in runtimeAssembly.GetTypes().Where(t => t.Name is "Something" or "Nothing"))
        {
            var caseToStrings = caseType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == nameof(object.ToString));

            Assert.Single(caseToStrings);
        }
    }

    [Fact]
    public void GenericUnionCaseType_IsRegisteredForCodeGeneration()
    {
        var code = """
import System.*

union Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}

extension ResultExtensions<T, E> for Result<T, E> {
    public val IsError: bool {
        get {
            if self is .Error(_) {
                return true
            }
            return false
        }
    }
}

class Container {
    public func CreateError() -> Result<int, string> {
        return Result<int, string>.Error(message: "oops")
    }

    public func Check() -> bool {
        var value = CreateError()
        return value.IsError
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var extensionContainer = runtimeAssembly.GetType("ResultExtensions`2")
            ?? runtimeAssembly.GetType("ResultExtensions");
        Assert.NotNull(extensionContainer);
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var check = containerType.GetMethod("Check", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Activator.CreateInstance(containerType)!;

        var isError = (bool)check.Invoke(instance, Array.Empty<object?>())!;
        Assert.True(isError);
    }

    [Fact]
    public void ImplicitTailReturn_ConvertsUnionCaseToUnion()
    {
        const string code = """
import System.*
import System.Linq.*
import System.Collections.Generic.*

union Result<T, E> {
    case Ok(value: T)
    case Error(data: E)
}

class Container {
    public static func Create(items: IEnumerable<int>) -> Result<int, string> {
        let values = items.Take(1).ToList()
        if values.Count == 1 {
            return .Ok(values[0])
        }
        .Error("oops")
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "implicit-tail-return-union",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var create = containerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
        var empty = Array.Empty<int>();
        var value = create.Invoke(null, [empty])!;

        Assert.Equal("Result.Error(\"oops\")", value.ToString());
    }

    [Fact]
    public void ExtensionAccessibility_IsPreservedInMetadata()
    {
        const string code = """
import System.*

public extension IntExtensions for int {
    public func Double() -> int {
        return self * 2
    }
}

internal extension StringExtensions for string {
    internal func Echo() -> string {
        return self
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "extension-accessibility",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;

        var publicExtensions = assembly.GetType("IntExtensions", throwOnError: true)!;
        Assert.True(publicExtensions.IsPublic);
        var doubleMethod = publicExtensions.GetMethod("Double", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(doubleMethod);

        var internalExtensions = assembly.GetType("StringExtensions", throwOnError: true)!;
        Assert.True(internalExtensions.IsNotPublic);
        var echoMethod = internalExtensions.GetMethod("Echo", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(echoMethod);
        Assert.True(echoMethod!.IsAssembly);
    }

    [Fact]
    public void ExtensionMarkerMetadata_IsEmitted()
    {
        const string code = """
class Widget {
    public val Id: int
}

extension WidgetExtensions for Widget {
    public static func Build() -> Widget {
        return Widget()
    }

    public func Describe() -> int {
        return self.Id
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var compilation = Compilation.Create(
            "extension-markers",
            [syntaxTree],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var assembly = loaded.Assembly;

        var extensionMarkerAttribute = assembly.GetType("System.Runtime.CompilerServices.ExtensionMarkerNameAttribute", throwOnError: true)!;
        var extensionContainer = assembly.GetType("WidgetExtensions", throwOnError: true)!;
        var markerType = assembly
            .GetTypes()
            .Where(static t => t.Name.StartsWith("<>__RavenExtensionGrouping_For_", StringComparison.Ordinal))
            .SelectMany(t => t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            .FirstOrDefault(static t => t.Name.StartsWith("<>__RavenExtensionMarker_", StringComparison.Ordinal));
        Assert.NotNull(markerType);

        var markerMethod = markerType!.GetMethod("<Extension>$", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(markerMethod);
        Assert.Equal("Widget", markerMethod!.GetParameters().Single().ParameterType.Name);

        var buildMethod = extensionContainer.GetMethod("Build", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(buildMethod);
        var buildMarkerNames = buildMethod!
            .GetCustomAttributes(extensionMarkerAttribute, inherit: false)
            .Select(attr => (string)extensionMarkerAttribute.GetProperty("Name")!.GetValue(attr)!)
            .ToArray();
        Assert.Contains(markerType.Name, buildMarkerNames);

        var describeMethod = extensionContainer.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(describeMethod);
        var describeMarkerNames = describeMethod!
            .GetCustomAttributes(extensionMarkerAttribute, inherit: false)
            .Select(attr => (string)extensionMarkerAttribute.GetProperty("Name")!.GetValue(attr)!)
            .ToArray();
        Assert.Contains(markerType.Name, describeMarkerNames);
    }

    [Fact]
    public void GenericExtensionProperty_WithSiblingUnion_EmitsCaseTypes()
    {
        var code = """
import System.*

union Result<T, E> {
    case Ok(value: T)
    case Error(message: E)
}

union Outcome<T, E> {
    case Success(value: T)
    case Failure(data: E)
}

extension ResultExtensions<T, E> for Result<T, E> {
    public val IsError: bool {
        get {
            if self is .Error(_) {
                return true
            }
            return false
        }
    }
}

class Container {
    private func Parse(text: string) -> Result<int, string> {
        if text == "42" {
            return Result<int, string>.Ok(42)
        }
        return Result<int, string>.Error("bad")
    }

    public func Check(text: string) -> bool {
        return Parse(text).IsError
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var version = TargetFrameworkResolver.ResolveVersion(TestTargetFramework.Default);
        MetadataReference[] references = [
            .. TargetFrameworkResolver
                .GetReferenceAssemblies(version)
                .Select(path => MetadataReference.CreateFromFile(path))
        ];

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var runtimeAssembly = loaded.Assembly;
        var containerType = runtimeAssembly.GetType("Container", throwOnError: true)!;
        var check = containerType.GetMethod("Check", BindingFlags.Public | BindingFlags.Instance)!;
        var instance = Activator.CreateInstance(containerType)!;
        var isError = (bool)check.Invoke(instance, new object?[] { "foo" })!;
        var isOk = (bool)check.Invoke(instance, new object?[] { "42" })!;

        Assert.True(isError);
        Assert.False(isOk);
    }

    private static (int ExitCode, string Output) RunDotnet(string[] arguments, string workingDirectory)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        output += process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }

    private static bool IsMetadataType(MetadataReader metadata, EntityHandle handle, string @namespace, string name)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => IsMetadataType(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)handle), @namespace, name),
            HandleKind.TypeReference => IsMetadataType(metadata, metadata.GetTypeReference((TypeReferenceHandle)handle), @namespace, name),
            _ => false
        };
    }

    private static bool IsMetadataType(MetadataReader metadata, TypeDefinition type, string @namespace, string name)
    {
        return metadata.StringComparer.Equals(type.Namespace, @namespace) &&
            metadata.StringComparer.Equals(type.Name, name);
    }

    private static bool IsMetadataType(MetadataReader metadata, TypeReference type, string @namespace, string name)
    {
        return metadata.StringComparer.Equals(type.Namespace, @namespace) &&
            metadata.StringComparer.Equals(type.Name, name);
    }
}
