using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Symbols;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Semantics.Tests.Metadata;

public sealed class CSharpUnionInteropTests
{
    [Fact]
    public void RavenUnionFromCompiler_IsConsumedByCSharpNet11Application()
    {
        if (!TryGetLatestDotNet11Sdk(out var sdkVersion))
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"raven-produced-union-csharp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "global.json"),
                $$"""
                {
                  "sdk": {
                    "version": "{{sdkVersion}}",
                    "rollForward": "disable"
                  }
                }
                """);

            var ravenAssemblyPath = Path.Combine(directory, "RavenProduced.dll");
            EmitRavenProducedUnionAssembly(ravenAssemblyPath);
            var ravenCorePath = Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll");
            Assert.True(File.Exists(ravenCorePath), ravenCorePath);

            File.WriteAllText(
                Path.Combine(directory, "CSharpConsumer.csproj"),
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <LangVersion>preview</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <WarningsAsErrors>CS8509</WarningsAsErrors>
                  </PropertyGroup>

                  <ItemGroup>
                    <Reference Include="RavenProduced">
                      <HintPath>{{ravenAssemblyPath}}</HintPath>
                      <Private>true</Private>
                    </Reference>
                    <Reference Include="Raven.Core">
                      <HintPath>{{ravenCorePath}}</HintPath>
                      <Private>true</Private>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(directory, "Program.cs"),
                """
                using RavenProduced;

                static int Fail(string message)
                {
                    Console.Error.WriteLine(message);
                    return 1;
                }

                var value = new Choice(new Choice.Int32(42));
                if (!value.HasValue)
                    return Fail("Constructed struct union carrier should have HasValue.");

                if (value.Value is not Choice.Int32 boxedInt || boxedInt.Value != 42)
                    return Fail("Struct union Value should expose the active case object.");

                if (!value.TryGetValue(out Choice.Int32 extractedInt) || extractedInt.Value != 42)
                    return Fail("Struct union TryGetValue should extract the active case.");

                if (value.TryGetValue(out Choice.Text _))
                    return Fail("Struct union TryGetValue should reject inactive cases.");

                var unnamedSingle = new Choice(new Choice.Single(7));
                if (!unnamedSingle.TryGetValue(out Choice.Single single) || single.Value != 7)
                    return Fail("A single unnamed payload should project as Value.");

                var unnamedPair = new Choice(new Choice.Pair(8, "eight"));
                var pairValue = unnamedPair switch
                {
                    Choice.Pair(var item1, var item2) when item2 == "eight" => item1,
                    _ => -1
                };
                if (pairValue != 8)
                    return Fail("Multiple unnamed payloads should project as Item1/Item2 and deconstruct positionally.");

                var none = new Choice(new Choice.None());
                if (!none.HasValue || none.Value is not Choice.None)
                    return Fail("Parameterless case should still produce an active carrier.");

                Choice defaultChoice = default;
                if (defaultChoice.HasValue)
                    return Fail("Default struct union carrier should be inactive.");

                if (defaultChoice.Value is not null)
                    return Fail("Default struct union Value should be null.");

                if (defaultChoice.TryGetValue(out Choice.Int32 _))
                    return Fail("Default struct union should not extract a case.");

                var referenceValue = new ReferenceChoice(new ReferenceChoice.Text("ok"));
                if (!referenceValue.HasValue)
                    return Fail("Constructed class union carrier should have HasValue.");

                if (referenceValue.Value is not ReferenceChoice.Text text || text.Value != "ok")
                    return Fail("Class union Value should expose the active case object.");

                if (!referenceValue.TryGetValue(out ReferenceChoice.Text extractedText) || extractedText.Value != "ok")
                    return Fail("Class union TryGetValue should extract the active case.");

                RavenProduced.Result<int, string> ok = new RavenProduced.Result.Ok<int>(42);
                if (ok.Value is not RavenProduced.Result.Ok<int> genericOk || genericOk.Value != 42)
                    return Fail("Generic companion case should construct and match through the C# union surface.");

                if (ok is not RavenProduced.Result.Ok<int>(var matchedValue) || matchedValue != 42)
                    return Fail("Generic companion case should participate in C# union patterns.");

                RavenProduced.Result<int, string> error = new RavenProduced.Result.Error<string>("bad");
                if (!error.TryGetValue(out RavenProduced.Result.Error<string> extractedError) || extractedError.Error != "bad")
                    return Fail("Generic companion case should preserve only its required generic parameter.");

                var unionOptions = new System.Text.Json.JsonSerializerOptions
                {
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                };
                foreach (var unionType in new[]
                {
                    typeof(System.Union<bool, string>),
                    typeof(System.Union<bool, string, int>),
                    typeof(System.Union<bool, string, int, double>),
                    typeof(System.Union<bool, string, int, double, decimal>)
                })
                {
                    if (unionOptions.GetTypeInfo(unionType).Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Union)
                        return Fail("Standard unions should use the .NET 11 union contract: " + unionType);
                }
                var flag = System.Text.Json.JsonSerializer.Deserialize<System.Union<bool, string>>("true",
                    System.Text.Json.JsonSerializerOptions.Web);
                if (flag.Value is not true)
                    return Fail("Standard bool/string unions should round-trip using web defaults.");
                var generatedJson = System.Text.Json.JsonSerializer.Serialize(flag,
                    typeof(System.Union<bool, string>), NativeUnionJsonContext.Default);
                var generatedValue = (System.Union<bool, string>)System.Text.Json.JsonSerializer.Deserialize(
                    generatedJson, typeof(System.Union<bool, string>), NativeUnionJsonContext.Default)!;
                if (generatedJson != "true" || generatedValue.Value is not true)
                    return Fail("Standard unions should work with System.Text.Json source generation.");
                var unionJson = System.Text.Json.JsonSerializer.Serialize(
                    new System.Union<Authorized, string>(new Authorized("id", 42m)), unionOptions);
                if (unionJson.Contains("$type"))
                    return Fail("Native union JSON should not add a discriminator: " + unionJson);
                var unionRoundTrip = System.Text.Json.JsonSerializer.Deserialize<System.Union<Authorized, string>>(
                    unionJson, unionOptions);
                if (unionRoundTrip.Value is not Authorized { Amount: 42m, PaymentId: "id" })
                    return Fail("Native union JSON should round-trip its object case.");

                var jsonOptions = new System.Text.Json.JsonSerializerOptions
                {
                    InferClosedTypePolymorphism = true
                };
                PaymentEvent payment = new Authorized("id", 42m);
                var json = System.Text.Json.JsonSerializer.Serialize(payment, jsonOptions);
                if (!json.Contains("\"$type\":\"Authorized\"") || !json.Contains("\"Amount\":42"))
                    return Fail("Closed hierarchy JSON should preserve its discriminator and derived data: " + json);
                if (System.Text.Json.JsonSerializer.Deserialize<PaymentEvent>(json, jsonOptions)
                    is not Authorized { Amount: 42m, PaymentId: "id" })
                    return Fail("Closed hierarchy JSON should round-trip the derived record.");

                // CS8509 is an error in this consumer: no discard arm is necessary.
                var description = payment switch
                {
                    Authorized => "authorized",
                    Failed => "failed"
                };
                if (description != "authorized")
                    return Fail("C# should match the Raven closed hierarchy.");

                return 0;

                [System.Text.Json.Serialization.JsonSerializable(typeof(System.Union<bool, string>))]
                internal partial class NativeUnionJsonContext : System.Text.Json.Serialization.JsonSerializerContext
                {
                }
                """);

            var build = RunDotnet(["build", "/property:WarningLevel=0", "-v:minimal"], directory);
            Assert.True(build.ExitCode == 0, build.Output);

            var run = RunDotnet(["run", "--no-build", "--project", "CSharpConsumer.csproj"], directory);
            Assert.True(run.ExitCode == 0, run.Output);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CSharpTypesFromLatestSdk_ImportUnionsAndClosedHierarchies()
    {
        if (!TryGetLatestDotNet11Sdk(out var sdkVersion))
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"raven-csharp-union-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "global.json"),
                $$"""
                {
                  "sdk": {
                    "version": "{{sdkVersion}}",
                    "rollForward": "disable"
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(directory, "CSharpUnionFixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <LangVersion>preview</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(directory, "UnionFixture.cs"),
                """
                using System;
                using System.Runtime.CompilerServices;

                namespace CSharpUnionFixture;

                public union Foo(int, double?);

                public closed record GenericEvent<T>;
                public sealed record GenericCreated<T>(T Value) : GenericEvent<T>;
                public sealed record GenericRemoved<T> : GenericEvent<T>;
                public closed record PairEvent<TFirst, TSecond>;
                public sealed record Reversed<TSecond, TFirst> : PairEvent<TFirst, TSecond>;



                public closed record Event;
                public record Created(int Value) : Event;
                public sealed record Updated(int Value) : Created(Value);
                public static class Nested
                {
                    public sealed record Removed : Event;
                }
                public sealed record Unrelated;


                [Union]
                public sealed class CustomClass : IUnion
                {
                    public object? Value { get; }

                    public CustomClass(string? value) => Value = value;

                    public CustomClass(int value) => Value = value;

                    public bool TryGetValue(out string? value)
                    {
                        value = Value as string;
                        return value is not null;
                    }

                    public bool TryGetValue(out decimal value)
                    {
                        value = default;
                        return false;
                    }
                }

                [Union]
                public readonly struct CustomStruct : IUnion
                {
                    public object? Value { get; }

                    public CustomStruct(Guid value) => Value = value;
                }

                [Union]
                public sealed class NonNullableContents : IUnion
                {
                    public object? Value { get; }

                    public NonNullableContents(string value) => Value = value;
                }

                [Union]
                public sealed class TryGetExtra : IUnion
                {
                    public object? Value { get; }

                    public TryGetExtra(int value) => Value = value;

                    public bool TryGetValue(out string? value)
                    {
                        value = null;
                        return false;
                    }
                }
                """);

            var build = RunDotnet(["build", "/property:WarningLevel=0", "-v:minimal"], directory);
            Assert.True(build.ExitCode == 0, build.Output);

            var referencePath = Path.Combine(directory, "bin", "Debug", "net11.0", "CSharpUnionFixture.dll");
            Assert.True(File.Exists(referencePath), referencePath);

            var net11Version = TargetFrameworkResolver.ResolveVersion("net11.0");
            var net11References = TargetFrameworkResolver.GetReferenceAssemblies(net11Version)
                .Where(File.Exists)
                .Select(MetadataReference.CreateFromFile)
                .ToArray();

            var compilation = Compilation.Create(
                "csharp-union-interop",
                [],
                [.. net11References, MetadataReference.CreateFromFile(referencePath)]);
            var fixtureNamespace = compilation.GlobalNamespace.GetMembers("CSharpUnionFixture").OfType<INamespaceSymbol>().Single();
            var closedEvent = Assert.IsAssignableFrom<INamedTypeSymbol>(
                compilation.GetTypeByMetadataName("CSharpUnionFixture.Event"));
            Assert.True(closedEvent.IsSealedHierarchy);
            Assert.Equal(["Created", "Removed"],
                closedEvent.PermittedDirectSubtypes.Select(type => type.Name).Order());
            var invalidDerivation = compilation.AddSyntaxTrees(SyntaxTree.ParseText(
                "public record ExternalEvent : CSharpUnionFixture.Event"));
            Assert.Contains(invalidDerivation.GetDiagnostics(), diagnostic =>
                diagnostic.Descriptor == CompilerDiagnostics.CannotInheritFromClosedType);

            var genericEvent = Assert.IsAssignableFrom<INamedTypeSymbol>(
                compilation.GetTypeByMetadataName("CSharpUnionFixture.GenericEvent`1"));
            Assert.True(genericEvent.IsSealedHierarchy);
            Assert.Equal(["GenericCreated", "GenericRemoved"],
                genericEvent.PermittedDirectSubtypes.Select(type => type.Name).Order());

            foreach (var (arms, exhaustive) in new[]
            {
                ("GenericCreated<int> => 1\n GenericRemoved<int> => 2", true),
                ("GenericCreated<int> => 1", false)
            })
            {
                var tree = SyntaxTree.ParseText($$"""
                    import CSharpUnionFixture.*
                    public class GenericEvaluator {
                        public static func Evaluate(value: GenericEvent<int>) -> int {
                            return match value { {{arms}} }
                        }
                        public static func Reorder(value: PairEvent<int, string>) -> int {
                            return match value { Reversed<string, int> => 3 }
                        }
                    }
                    """);
                var consumer = Compilation.Create("GenericConsumer", [tree],
                    [.. net11References, MetadataReference.CreateFromFile(referencePath)],
                    new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                var diagnostics = consumer.GetDiagnostics();
                Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
                    diagnostic.Descriptor != CompilerDiagnostics.MatchExpressionNotExhaustive);
                var model = consumer.GetSemanticModel(tree);
                var match = tree.GetRoot().DescendantNodes().OfType<MatchExpressionSyntax>().First();
                Assert.Equal(exhaustive, model.GetMatchExhaustiveness(match).IsExhaustive);
                if (!exhaustive)
                    Assert.Contains("GenericRemoved<int>", model.GetMatchExhaustiveness(match).MissingCases);
                else
                {
                    using var emitted = new MemoryStream();
                    var emit = consumer.Emit(emitted);
                    Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
                    emitted.Position = 0;
                    var loadContext = new System.Runtime.Loader.AssemblyLoadContext("generic-closed-interop", isCollectible: true);
                    try
                    {
                        var fixture = loadContext.LoadFromAssemblyPath(referencePath);
                        var assembly = loadContext.LoadFromStream(emitted);
                        var evaluator = assembly.GetType("GenericEvaluator")!;
                        var created = fixture.GetType("CSharpUnionFixture.GenericCreated`1")!.MakeGenericType(typeof(int));
                        Assert.Equal(1, evaluator.GetMethod("Evaluate")!.Invoke(null, [Activator.CreateInstance(created, 42)]));
                        var reversed = fixture.GetType("CSharpUnionFixture.Reversed`2")!.MakeGenericType(typeof(string), typeof(int));
                        Assert.Equal(3, evaluator.GetMethod("Reorder")!.Invoke(null, [Activator.CreateInstance(reversed)]));
                    }
                    finally
                    {
                        loadContext.Unload();
                    }
                }
            }

            var foo = fixtureNamespace.GetMembers("Foo").OfType<IUnionSymbol>().Single();

            Assert.True(foo.ContentMayBeNull);
            Assert.Contains(foo.MemberTypes, static member => member.SpecialType == SpecialType.System_Int32);
            Assert.Contains(foo.MemberTypes, static member => member.SpecialType == SpecialType.System_Double && !member.IsNullable);

            var valueProperty = Assert.Single(foo.GetMembers("Value").OfType<IPropertySymbol>());
            Assert.True(valueProperty.Type.IsNullable);

            var customClass = fixtureNamespace.GetMembers("CustomClass").OfType<IUnionSymbol>().Single();
            Assert.True(customClass.ContentMayBeNull);
            Assert.Contains(customClass.MemberTypes, static member => member.SpecialType == SpecialType.System_String && !member.IsNullable);
            Assert.Contains(customClass.MemberTypes, static member => member.SpecialType == SpecialType.System_Int32);
            Assert.DoesNotContain(customClass.MemberTypes, static member => member.SpecialType == SpecialType.System_Decimal);

            var customStruct = fixtureNamespace.GetMembers("CustomStruct").OfType<IUnionSymbol>().Single();
            Assert.Equal(TypeKind.Struct, customStruct.TypeKind);
            Assert.Contains(customStruct.MemberTypes, static member => member.Name == "Guid");

            var nonNullableContents = fixtureNamespace.GetMembers("NonNullableContents").OfType<IUnionSymbol>().Single();
            Assert.False(nonNullableContents.ContentMayBeNull);
            Assert.Contains(nonNullableContents.MemberTypes, static member => member.SpecialType == SpecialType.System_String);

            var tryGetExtra = fixtureNamespace.GetMembers("TryGetExtra").OfType<IUnionSymbol>().Single();
            Assert.False(tryGetExtra.ContentMayBeNull);
            Assert.Collection(tryGetExtra.MemberTypes, member => Assert.Equal(SpecialType.System_Int32, member.SpecialType));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static bool TryGetLatestDotNet11Sdk(out string sdkVersion)
    {
        var result = RunDotnet(["--list-sdks"], Directory.GetCurrentDirectory());
        if (result.ExitCode != 0)
        {
            sdkVersion = string.Empty;
            return false;
        }

        sdkVersion = result.Output
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(static version => version is not null && version.StartsWith("11.", StringComparison.Ordinal))
            .LastOrDefault() ?? string.Empty;

        return sdkVersion.Length > 0;
    }

    private static void EmitRavenProducedUnionAssembly(string assemblyPath)
    {
        var net11Version = TargetFrameworkResolver.ResolveVersion("net11.0");
        var references = TargetFrameworkResolver.GetReferenceAssemblies(net11Version)
            .Where(File.Exists)
            .Select(MetadataReference.CreateFromFile)
            .ToArray();

        var syntaxTree = SyntaxTree.ParseText(
            """
            namespace RavenProduced

            public union Choice {
                case Int32(value: int)
                case Text(value: string)
                case Single(int)
                case Pair(int, string)
                case None
            }

            public union class ReferenceChoice {
                case Text(value: string)
                case Number(value: int)
            }

            public sealed record PaymentEvent(PaymentId: string) permits Authorized, Failed
            public record Authorized(PaymentId: string, Amount: decimal) : PaymentEvent(PaymentId)
            public record Failed(PaymentId: string, Reason: string) : PaymentEvent(PaymentId)

            public union Result<T, E> {
                case Ok(value: T)
                case Error(error: E)
            }
            """);

        var compilation = Compilation.Create(
            "RavenProduced",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = File.Create(assemblyPath);
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));
    }

    private static bool IsNullableOf(ITypeSymbol type, SpecialType underlyingSpecialType)
    {
        if (type.GetNullableUnderlyingType() is { } nullableUnderlying)
            return nullableUnderlying.SpecialType == underlyingSpecialType;

        return type is INamedTypeSymbol { SpecialType: SpecialType.System_Nullable_T } namedType &&
               namedType.TypeArguments.Length == 1 &&
               namedType.TypeArguments[0].SpecialType == underlyingSpecialType;
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
}
