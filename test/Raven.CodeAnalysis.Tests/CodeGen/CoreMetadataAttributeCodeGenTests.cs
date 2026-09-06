using System;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis.Tests.CodeGen;

public sealed class CoreMetadataAttributeCodeGenTests
{
    [Fact]
    public void EmbeddedUnionMetadata_IsDefinedInOutputWhenCoreIsNotReferenced()
    {
        // A compiler host can have Raven.Core loaded even when the target does not reference it.
        Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll"));
        var compilation = Compilation.Create(
            "embedded-union-metadata",
            [SyntaxTree.ParseText("public union Choice<T> { case Present(T); case Missing }")],
            TestMetadataReferences.Default,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithEmbedCoreTypes(true)
                .WithFrameworkProjectionMode(FrameworkProjectionMode.None));
        var hostCompilation = Compilation.Create("host").AddReferences(TestMetadataReferences.DefaultWithRavenCore);
        Assert.NotNull(hostCompilation.GetTypeByMetadataName("Raven.Runtime.CompilerServices.RavenUnionCaseAttribute"));
        var hostOption = Assert.IsAssignableFrom<PENamedTypeSymbol>(hostCompilation.GetTypeByMetadataName("System.Option`1"));
        Assert.NotNull(compilation.ResolveRuntimeType(hostOption));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream);
        foreach (var name in new[] { "RavenUnionCaseAttribute", "RavenUnionCompanionAttribute" })
        {
            Assert.Contains(assembly.MainModule.Types, type => type.Name == name);
        }
        Assert.DoesNotContain(assembly.MainModule.AssemblyReferences, reference => reference.Name == "Raven.Core");
    }
}
