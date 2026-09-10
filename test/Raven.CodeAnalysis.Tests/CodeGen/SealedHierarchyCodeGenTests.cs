using System;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public class SealedHierarchyCodeGenTests
{
    [Theory]
    [InlineData("net10.0", "interface")]
    [InlineData("net11.0", "interface")]
    [InlineData("net10.0", "record")]
    [InlineData("net11.0", "record")]
    public void GenericSealedHierarchy_NestedCases_PublishLoadableMetadataAndExecute(string targetFramework, string rootKind)
    {
        var code = $$"""
import System.*
import System.Console.*

sealed {{rootKind}} Expr<T>{{(rootKind == "record" ? "()" : "")}} {
    record NumericalExpr(Value: float) : Expr<float>
    record StringExpr(Value: string) : Expr<string>
    record AddExpr(Left: Expr<float>, Right: Expr<float>) : Expr<float>
}

class Program {
    public static func Run() -> string {
        let left = Expr.NumericalExpr(40)
        let right = Expr.NumericalExpr(2)
        let result = Expr.AddExpr(left, right)
        return result.ToString()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TargetFrameworkResolver.GetReferenceAssemblies(
                TargetFrameworkResolver.ResolveVersion(targetFramework))
            .Where(File.Exists)
            .Select(MetadataReference.CreateFromFile).ToArray();

        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var assembly = loaded.Assembly;
        var root = assembly.GetType("Expr`1", throwOnError: true)!;
        var usesNativeContract = targetFramework == "net11.0" && rootKind == "record";
        var expectedAttribute = usesNativeContract ? "IsClosedTypeAttribute" : "ClosedHierarchyAttribute";
        var attribute = Assert.Single(root.GetCustomAttributesData(), attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices." + expectedAttribute);
        var permittedArgument = usesNativeContract
            ? Assert.Single(attribute.NamedArguments).TypedValue
            : Assert.Single(attribute.ConstructorArguments);
        var permitted = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyCollection<CustomAttributeTypedArgument>>(
            permittedArgument.Value);
        var permittedTypes = permitted.Select(argument => Assert.IsAssignableFrom<Type>(argument.Value)).ToArray();
        Assert.Equal(3, permittedTypes.Length);
        Assert.All(permittedTypes, type =>
        {
            var bases = rootKind == "interface" ? type.GetInterfaces() : new[] { type.BaseType! };
            Assert.Contains(bases, implemented =>
                implemented.IsGenericType && implemented.GetGenericTypeDefinition() == root);
        });

        var programType = assembly.GetType("Program", throwOnError: true)!;
        var run = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var value = (string)run.Invoke(null, Array.Empty<object>())!;
        Assert.Contains("AddExpr", value, StringComparison.Ordinal);
    }
}
