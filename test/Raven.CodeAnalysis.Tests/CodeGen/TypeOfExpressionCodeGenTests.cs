using System;
using System.Collections.Generic;
using System.IO;

using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests;

public class TypeOfExpressionCodeGenTests
{
    [Theory]
    [InlineData("Box<>", null, true)]
    [InlineData("Box<int>", null, false)]
    [InlineData("System.Collections.Generic.List<>", typeof(List<>), true)]
    [InlineData("System.Collections.Generic.List<int>", typeof(List<int>), false)]
    [InlineData("System.Collections.Generic.Dictionary<,>", typeof(Dictionary<,>), true)]
    public void TypeOf_GenericType_ReturnsExactRuntimeType(string operand, Type? metadataType, bool open)
    {
        var code = $$"""
import System.*
class Box<T> { }

class Foo {
    public func Run() -> Type {
        return typeof({{operand}})
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var type = loaded.Assembly.GetType("Foo", true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod("Run")!;
        var value = (Type)method.Invoke(instance, Array.Empty<object>())!;

        var sourceDefinition = loaded.Assembly.GetType("Box`1", throwOnError: true)!;
        var expected = metadataType ?? (open ? sourceDefinition : sourceDefinition.MakeGenericType(typeof(int)));
        Assert.Equal(expected, value);
        Assert.Equal(open, value.IsGenericTypeDefinition);
    }
}
