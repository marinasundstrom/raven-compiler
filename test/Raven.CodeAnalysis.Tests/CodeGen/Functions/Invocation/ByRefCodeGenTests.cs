using System;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public class ByRefCodeGenTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ByRefLocal_AssignmentWritesThrough(bool explicitType)
    {
        var code = $$"""
class C {
    static func WriteThrough() -> int {
        var value = 1
        let handle{{(explicitType ? ": &int" : "")}} = &value
        value = 3
        handle = *handle + 2
        value
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
        var assembly = loaded.Assembly;
        var type = assembly.GetType("C", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var method = type.GetMethod("WriteThrough", flags);
        Assert.NotNull(method);

        var value = (int)method!.Invoke(null, Array.Empty<object>())!;
        Assert.Equal(5, value);
    }

    [Theory]
    [InlineData("slot: &int", "&value")]
    [InlineData("ref slot: int", "&value")]
    [InlineData("ref slot: int", "ref value")]
    public void ByRefParameter_AssignmentThroughAliasMutatesSource(string parameter, string argument)
    {
        var code = $$"""
class Buffer {
    static func Write({{parameter}}, value: int) -> unit {
        slot = slot + value
    }

    static func Run() -> int {
        var value = 10
        Write({{argument}}, 32)
        value
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
        var assembly = loaded.Assembly;
        var type = assembly.GetType("Buffer", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var method = type.GetMethod("Run", flags);
        Assert.NotNull(method);

        var value = (int)method!.Invoke(null, Array.Empty<object>())!;
        Assert.Equal(42, value);
    }

    [Theory]
    [InlineData("int", "10", "32", "32:10")]
    [InlineData("string", "\"first\"", "\"second\"", "second:first")]
    public void GenericByRefParameters_ForwardAndSwapValues(string typeName, string first, string second, string expected)
    {
        var code = $$"""
class Buffer {
    static func Swap<T>(ref left: T, ref right: T) -> () {
        let original = left
        left = right
        right = original
    }

    static func Forward<T>(ref left: T, ref right: T) -> () {
        Swap(ref left, ref right)
    }

    static func Run() -> string {
        var first: {{typeName}} = {{first}}
        var second: {{typeName}} = {{second}}
        Forward(ref first, ref second)
        return first.ToString() + ":" + second.ToString()
    }
}
""";
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("byref-swap", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(SyntaxTree.ParseText(code))
            .AddReferences(references);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(stream, references);
        var method = loaded.Assembly.GetType("Buffer", throwOnError: true)!.GetMethod("Run")!;
        Assert.Equal(expected, method.Invoke(null, null));
    }

}
