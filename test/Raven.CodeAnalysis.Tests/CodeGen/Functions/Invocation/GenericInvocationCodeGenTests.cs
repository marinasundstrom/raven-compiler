using System;
using System.IO;
using System.Linq;
using System.Reflection;

using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests;

public class GenericInvocationCodeGenTests
{
    [Fact]
    public void NullableAnnotatedUnconstrainedTypeParameter_ConstructedWithValueType_ExecutesWithoutInvalidProgram()
    {
        const string code = """
import System.Threading.Tasks.*
import Microsoft.AspNetCore.Components.*

class CallbackRunner {
    var Callback: EventCallback<int> = default(EventCallback<int>)

    func Run() -> Task => Callback.InvokeAsync(1)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default
            .Append(CreateAspNetCoreComponentsRuntimeReference())
            .ToArray();
        var compilation = Compilation.Create("nullable_annotated_generic_value_argument", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);
        var invocation = syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(candidate => candidate.Expression.ToString().EndsWith("InvokeAsync", StringComparison.Ordinal));
        var invokedMethod = Assert.IsAssignableFrom<IMethodSymbol>(
            compilation.GetSemanticModel(syntaxTree).GetSymbolInfo(invocation).Symbol);
        var callbackArgument = Assert.Single(invokedMethod.Parameters);
        Assert.True(callbackArgument.Type.TryGetNullableUnderlyingType(out var nullableArgument));
        Assert.Equal(SpecialType.System_Int32, nullableArgument.SpecialType);
        Assert.Equal(NullableAbiProjection.AnnotatedUnderlyingType, callbackArgument.Type.GetNullableAbiProjection());

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("CallbackRunner", true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (System.Threading.Tasks.Task?)method!.Invoke(instance, Array.Empty<object>());

        Assert.NotNull(task);
        task!.GetAwaiter().GetResult();
    }

    [Fact]
    public void NullableAnnotatedConstructedGenericMethods_ExecuteWithValueTypeArguments()
    {
        const string code = """
class NullableGenericMethods {
    static func Echo<T>(value: T?) -> T? => value

    static func EchoValue<T>(value: T?) -> T? where T: struct => value
}

class GenericMethodRunner {
    static func RunUnconstrained() -> int? => NullableGenericMethods.Echo<int>(42)

    static func RunValueConstrained() -> int? => NullableGenericMethods.EchoValue<int>(42)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("nullable_annotated_constructed_methods", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("GenericMethodRunner", true)!;

        var unconstrained = type.GetMethod("RunUnconstrained", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var valueConstrained = type.GetMethod("RunValueConstrained", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(unconstrained);
        Assert.NotNull(valueConstrained);
        Assert.Equal(42, (int?)unconstrained!.Invoke(null, Array.Empty<object>()));
        Assert.Equal(42, (int?)valueConstrained!.Invoke(null, Array.Empty<object>()));
    }

    [Fact]
    public void UnconstrainedTypeParameter_ToString_ReturnsFormattedValue()
    {
        const string code = """
import System.*

class Formatter {
    func Format<T>(value: T) -> string? {
        return value.ToString()
    }

    func Run() -> string? {
        return Format<int>(42)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("generic_invocation", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Formatter", true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var value = (string?)method!.Invoke(instance, Array.Empty<object>());
        Assert.Equal("42", value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructConstrainedVarArgs_ConvertsElementsToObject(bool empty)
    {
        var code = $$"""
class Formatter {
    static func Consume(value: object) -> string {
        return value.ToString() ?? "<null>"
    }

    static func Collect<T>(items: T ...) -> string where T: struct {
        var text = ""
        for item in items {
            text = text + Consume(item)
        }
        return text
    }

    func Run() -> string {
        let arr: int[] = {{(empty ? "[]" : "[1, 2, 3]")}}
        return Collect(arr)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("generic_varargs_boxing", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Formatter", true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var value = (string?)method!.Invoke(instance, Array.Empty<object>());
        Assert.Equal(empty ? "" : "123", value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructConstrainedVarArgs_SingleSpread_PreservesElements(bool empty)
    {
        var code = $$"""
import System.Collections.Immutable.*

class Formatter {
    static func Collect<T>(items: T ...) -> string where T: struct {
        var text = ""
        for item in items {
            text = text + (item.ToString() ?? "<null>")
        }
        return text
    }

    func Run() -> string {
        let arr: ImmutableList<int> = {{(empty ? "[]" : "[1, 2, 3]")}}
        return Collect(...arr)
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("generic_varargs_spread_fastpath", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);

        var type = loaded.Assembly.GetType("Formatter", true)!;
        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var instance = Activator.CreateInstance(type)!;
        var value = (string?)method!.Invoke(instance, Array.Empty<object>());
        Assert.Equal(empty ? "" : "123", value);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-7", -7)]
    [InlineData("invalid", null)]
    public void StaticAbstractInterfaceCallOnTypeParameter_ParsesOrThrows(string text, int? expected)
    {
        const string code = """
import System.*

class Parser {
    static func Parse<T>(text: string) -> T
        where T: IParsable<T>
        => T.Parse(text, null)

    static func Run(text: string) -> int => Parse<int>(text)
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("generic_static_abstract_call", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);
        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);

        var type = loaded.Assembly.GetType("Parser", true)!;
        var parseMethod = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => string.Equals(m.Name, "Parse", StringComparison.Ordinal));
        var typeParameter = parseMethod.GetGenericArguments().Single();
        var constraint = Assert.Single(typeParameter.GetGenericParameterConstraints());
        Assert.Equal(typeof(IParsable<>), constraint.GetGenericTypeDefinition());

        var run = type.GetMethod("Run")!;
        if (expected is { } value)
            Assert.Equal(value, run.Invoke(null, [text]));
        else
            Assert.IsType<FormatException>(Assert.Throws<TargetInvocationException>(() => run.Invoke(null, [text])).InnerException);

    }

    private static MetadataReference CreateAspNetCoreComponentsRuntimeReference()
    {
        var referenceDirectory = ReferenceAssemblyPaths.GetReferenceAssemblyDir(
            targetFramework: "net10.0",
            packId: "Microsoft.AspNetCore.App.Ref");
        Assert.False(string.IsNullOrWhiteSpace(referenceDirectory));

        var versionDirectory = Directory.GetParent(Directory.GetParent(referenceDirectory!)!.FullName)!.FullName;
        var version = Path.GetFileName(versionDirectory);
        var dotnetRoot = Directory.GetParent(Directory.GetParent(Directory.GetParent(versionDirectory)!.FullName)!.FullName)!.FullName;
        var runtimePath = Path.Combine(
            dotnetRoot,
            "shared",
            "Microsoft.AspNetCore.App",
            version,
            "Microsoft.AspNetCore.Components.dll");
        Assert.True(File.Exists(runtimePath), $"Missing ASP.NET Core runtime assembly '{runtimePath}'.");
        return MetadataReference.CreateFromFile(runtimePath);
    }
}
