using System;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

namespace Raven.CodeAnalysis.Tests;

public class ExpressionBodyCodeGenTests
{
    [Fact]
    public void MethodAndConstructorExpressionBodies_AreEmitted()
    {
        var code = """
import System.*

class Foo : IDisposable {
    public init() => Console.WriteLine("Init")

    public func Dispose() -> unit => Console.WriteLine("Dispose")
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
        var fooType = assembly.GetType("Foo", throwOnError: true)!;

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);

            var instance = Activator.CreateInstance(fooType);
            Assert.NotNull(instance);

            var dispose = fooType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(dispose);

            _ = dispose!.Invoke(instance, Array.Empty<object?>());
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[] { "Init", "Dispose" }, output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FunctionStatementExpressionBody_ReturnsExpectedValue(bool hasValue)
    {
        var code = $$"""
union Option<T> {
    case Some(value: T)
    case None
}

class Program {
    public static func Run() -> string {
        func GetMessage() -> Option<string> => {{(hasValue ? ".Some(\"Hello, World!\")" : ".None")}}

        let message = GetMessage()
        return match message {
            .Some(let value) => value
            .None => "<none>"
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create(
                "function-statement-expression-body",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var runMethod = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(runMethod);

        var output = runMethod!.Invoke(null, Array.Empty<object?>());
        Assert.Equal(hasValue ? "Hello, World!" : "<none>", output);
    }

    [Fact]
    public void FunctionStatement_CapturingEnclosingLocal_EmitsAndExecutes()
    {
        const string code = """
class Program {
    public static func Run() -> int {
        let baseValue = 40

        func AddTwo() -> int {
            return baseValue + 2
        }

        return AddTwo()
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create(
                "function-statement-capture",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var runMethod = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(runMethod);

        var output = runMethod!.Invoke(null, Array.Empty<object?>());
        Assert.Equal(42, output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PropertyGetterAccessorExpressionBody_ReturnsExpectedValue(bool hasValue)
    {
        var code = $$"""
union Option<T> {
    case Some(value: T)
    case None
}

class Holder {
    public val Message: Option<string> {
        get => {{(hasValue ? ".Some(\"Hello, World!\")" : ".None")}}
    }
}

class Program {
    public static func Run() -> string {
        let holder = Holder()
        let message = holder.Message
        return match message {
            .Some(let value) => value
            .None => "<none>"
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create(
                "property-get-expression-body",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var runMethod = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(runMethod);

        var output = runMethod!.Invoke(null, Array.Empty<object?>());
        Assert.Equal(hasValue ? "Hello, World!" : "<none>", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PropertyExpressionBody_ReturnsExpectedValue(bool hasValue)
    {
        var code = $$"""
union Option<T> {
    case Some(value: T)
    case None
}

class Holder {
    public val Message: Option<string> => {{(hasValue ? ".Some(\"Hello, World!\")" : ".None")}}
}

class Program {
    public static func Run() -> string {
        let holder = Holder()
        let message = holder.Message
        return match message {
            .Some(let value) => value
            .None => "<none>"
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;

        var compilation = Compilation.Create(
                "property-expression-body",
                new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(syntaxTree)
            .AddReferences(references);

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var runMethod = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(runMethod);

        var output = runMethod!.Invoke(null, Array.Empty<object?>());
        Assert.Equal(hasValue ? "Hello, World!" : "<none>", output);
    }

}
