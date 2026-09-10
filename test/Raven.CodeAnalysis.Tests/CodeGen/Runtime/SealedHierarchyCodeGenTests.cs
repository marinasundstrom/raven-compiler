using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Tests.CodeGen;

public class SealedHierarchyCodeGenTests
{
    [Fact]
    public void SealedHierarchy_EmittedType_IsNotILSealed()
    {
        var source = """
sealed class Expr {}
class Lit : Expr {}
""";
        var tree = SyntaxTree.ParseText(source, path: "file.rvn");
        var compilation = Compilation.Create(
                "sealed_hierarchy_il_shape",
                [tree],
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TestMetadataReferences.Default);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(System.Environment.NewLine, result.Diagnostics));

        stream.Position = 0;
        var alc = new AssemblyLoadContext("SealedNotILSealed", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(stream);
            var exprType = assembly.GetType("Expr");
            Assert.NotNull(exprType);
            Assert.False(exprType!.IsSealed);
            Assert.True(exprType.IsAbstract);
        }
        finally
        {
            alc.Unload();
        }
    }

    [Theory]
    [InlineData("net10.0", false)]
    [InlineData("net11.0", true)]
    public void SealedHierarchy_EmitsTargetFrameworkContract(string targetFramework, bool usesFrameworkContract)
    {
        var source = """
sealed class Expr {}
class Lit : Expr {}
class Add : Expr {}
""";
        var tree = SyntaxTree.ParseText(source, path: "file.rvn");
        var compilation = Compilation.Create(
                "sealed_hierarchy_attr",
                [tree],
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(TargetFrameworkResolver.GetReferenceAssemblies(
                TargetFrameworkResolver.ResolveVersion(targetFramework))
                .Where(File.Exists)
                .Select(MetadataReference.CreateFromFile).ToArray());
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(System.Environment.NewLine, result.Diagnostics));

        stream.Position = 0;
        var alc = new AssemblyLoadContext("SealedClosedHierarchyAttr", isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(stream);
            var exprType = assembly.GetType("Expr");
            Assert.NotNull(exprType);

            var attributes = exprType!.GetCustomAttributesData();
            var expectedName = usesFrameworkContract
                ? "System.Runtime.CompilerServices.IsClosedTypeAttribute"
                : "System.Runtime.CompilerServices.ClosedHierarchyAttribute";
            var closedAttr = Assert.Single(attributes, a => a.AttributeType.FullName == expectedName);
            IReadOnlyCollection<CustomAttributeTypedArgument> types;
            if (usesFrameworkContract)
            {
                Assert.Empty(closedAttr.ConstructorArguments);
                var derivedTypes = Assert.Single(closedAttr.NamedArguments);
                Assert.Equal("DerivedTypes", derivedTypes.MemberName);
                types = Assert.IsAssignableFrom<IReadOnlyCollection<CustomAttributeTypedArgument>>(
                    derivedTypes.TypedValue.Value);
                Assert.Equal(typeof(object).Assembly, closedAttr.AttributeType.Assembly);
                Assert.DoesNotContain(attributes, a => a.AttributeType.Name == "ClosedHierarchyAttribute");
            }
            else
            {
                types = Assert.IsAssignableFrom<IReadOnlyCollection<CustomAttributeTypedArgument>>(
                    Assert.Single(closedAttr.ConstructorArguments).Value);
                Assert.Equal(assembly, closedAttr.AttributeType.Assembly);
                Assert.DoesNotContain(attributes, a => a.AttributeType.Name == "IsClosedTypeAttribute");
            }
            Assert.Equal(["Lit", "Add"], types.Select(argument => ((Type)argument.Value!).Name));

        }
        finally
        {
            alc.Unload();
        }
    }
}
