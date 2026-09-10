using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests;

public class MemberBindingCodeGenTests
{
    [Fact]
    public void MemberBinding_StaticField_FromReferenceAssembly_ResolvesRuntimeField()
    {
        const string code = """
class Program {
    public static func Get() -> string {
        .Empty
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
        var type = assembly.GetType("Program", throwOnError: true)!;
        var get = type.GetMethod("Get")!;

        var value = (string)get.Invoke(null, Array.Empty<object>())!;
        Assert.Equal(string.Empty, value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtensionPropertyGetter_ReturnsTargetTypedCaseInvocation_FromReturnStatement(bool success)
    {
        const string code = """
union Option<T> {
    case Some(value: T)
    case None
}

union Result<T, E> {
    case Ok(value: T)
    case Error(value: E)
}

extension ResultExtensions<T, E> for Result<T, E> {
    val IsOk: Option<T> {
        get {
            if self is .Ok(let value) {
                return .Some(value)
            }
            .None
        }
    }
}

class Program {
    public static func Get(success: bool) -> int {
        let r: Result<int, string> = if success { .Ok(42) } else { .Error("failure") }
        return match r.IsOk {
            .Some(let value) => value
            .None => -1
        }
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
        var type = assembly.GetType("Program", throwOnError: true)!;
        var get = type.GetMethod("Get")!;

        var value = (int)get.Invoke(null, [success])!;
        Assert.Equal(success ? 42 : -1, value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericExtension_GetType_ResultCanBeStoredAsType(bool valueType)
    {
        var code = $$"""
import System.*
import System.Collections.Generic.*

extension TypeProbe<T> for T {
    func RuntimeType() -> Type {
        let type = self.GetType()
        return type
    }
}

class Program {
    public static func Run() -> Type {
        let obj = {{(valueType ? "42" : "List<int>()")}}
        return obj.RuntimeType()
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
        var type = assembly.GetType("Program", throwOnError: true)!;
        var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var value = Assert.IsAssignableFrom<Type>(run.Invoke(null, Array.Empty<object>()));
        Assert.Equal(valueType ? typeof(int) : typeof(List<int>), value);
    }

    [Fact]
    public void GenericExtension_GetType_WithLambdaPredicate_ExecutesWithoutBadImageFormat()
    {
        const string code = """
import System.*
import System.Linq.*
import System.Collections.Generic.*

extension TypeProbe<T> for T {
    func CountReadableInstanceProperties() -> int {
        let type = self.GetType()
        return type
            .GetProperties()
            .Where(pi => !(pi.GetMethod?.IsStatic ?? false) && pi.GetMethod?.GetParameters()?.Length == 0)
            .Count()
    }
}

class Program {
    public static func Run() -> int {
        let obj = List<int>()
        return obj.CountReadableInstanceProperties()
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
        var type = assembly.GetType("Program", throwOnError: true)!;
        var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var value = (int)run.Invoke(null, Array.Empty<object>())!;
        Assert.Equal(2, value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResultWithMessage_GenericMapErrorLambda_PreservesSuccessOrWrapsError(bool success)
    {
        const string code = """
interface IError {
    val Message: string
}

record ParseError(val Message: string) : IError

record ContextError<TError: IError>(
    val Message: string,
    val InnerError: TError
) : IError {
    val Cause: TError => InnerError
}

union Result<T, E> {
    case Ok(value: T)
    case Error(value: E)
}

extension ErrorExtensions<TError: IError> for TError {
    func WithMessage(message: string) -> ContextError<TError> {
        return ContextError<TError>(message, self)
    }
}

extension ResultExtensions<T, E> for Result<T, E> {
    func MapError<E2>(mapper: E -> E2) -> Result<T, E2> {
        self match {
            .Ok(let value) => .Ok(value)
            .Error(let error) => .Error(mapper(error))
        }
    }
}

extension ResultErrorContextExtensions<T, E: IError> for Result<T, E> {
    func WithMessage(message: string) -> Result<T, ContextError<E>> {
        self.MapError(error => error.WithMessage(message))
    }
}

class Program {
    public static func Run(success: bool) -> string {
        let result: Result<int, ParseError> = if success { .Ok(42) } else { .Error(ParseError("invalid")) }
        let wrapped = result.WithMessage("context")

        return match wrapped {
            .Ok(let value) => value.ToString()
            .Error(let error) => error.Message + ":" + error.Cause.Message
        }
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
        var type = assembly.GetType("Program", throwOnError: true)!;
        var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        var value = (string)run.Invoke(null, [success])!;
        Assert.Equal(success ? "42" : "context:invalid", value);
    }
}
