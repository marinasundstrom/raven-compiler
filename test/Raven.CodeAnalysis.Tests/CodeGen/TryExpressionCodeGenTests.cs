using System;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using System.Threading.Tasks;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public sealed class TryExpressionCodeGenTests
{

    [Fact]
    public void TryPropagation_AdaptsThrowingApiOnSuccessAndFailure()
    {
        const string code = """
import System.*

class Runner {
    static func Import(text: string) -> Result<int, Exception> {
        let value = try? Convert.ToInt32(text)
        return .Ok(value)
    }

    static func Run(text: string) -> string {
        return Import(text) match {
            .Ok(let value) => "value: $value"
            .Error(let error) => "error: ${error.Message}"
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = GetReferencesWithRavenCore();
        var compilation = Compilation.Create(
            "try-propagation-throwing-api",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        peStream.Position = 0;
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, references);
        var run = loaded.Assembly.GetType("Runner")!.GetMethod(
            "Run",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal("value: 42", run.Invoke(null, ["42"]));
        Assert.StartsWith("error: ", Assert.IsType<string>(run.Invoke(null, ["expired"])));
    }

    [Fact]
    public async Task TryPropagation_WithResultOperand_ReturnsSuccessAndCapturedFailure()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Runner {
    static async func Test(throwExc: bool) -> Task<Result<int, Exception>> {
        let x = try? await Action2(throwExc)
        return .Ok(x + 2)
    }

    static async func Action2(throwExc: bool) -> Task<Result<int, Exception>> {
        await Task.Delay(1)
        if throwExc {
            throw Exception("Boom!")
        }

        return .Ok(40)
    }
    static async func Run(fail: bool) -> Task<string> {
        let result = await Test(fail)
        return match result {
            .Ok(let value) => "ok:" + value.ToString()
            .Error(let error) => "error:" + error.Message
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = GetReferencesWithRavenCore();
        var compilation = Compilation.Create(
            "try-propagation-result-operand",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(stream, references);
        var run = loaded.Assembly.GetType("Runner")!.GetMethod("Run")!;
        foreach (var fail in new[] { false, true })
        {
            var task = Assert.IsAssignableFrom<Task<string>>(run.Invoke(null, [fail]));
            Assert.Equal(fail ? "error:Boom!" : "ok:42", await task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void GenericArrayTryExpression_ReturnsEmptyArrayPayload()
    {
        const string code = """
namespace System

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

namespace Example

import System.*

class Runner {
    static func Wrap<T>() -> Result<T[], Exception> {
        try Array.Empty<T>()
    }
    static func Run() -> int {
        return match Wrap<int>() {
            .Ok(let values) => values.Length
            .Error(let error) => -1
        }
    }
}
""";

        var syntaxTree = SyntaxTree.ParseText(code);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create(
            "generic-array-try-expression",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        using var loaded = TestAssemblyLoader.LoadFromStream(stream, references);
        var run = loaded.Assembly.GetType("Example.Runner")!.GetMethod("Run")!;
        Assert.Equal(0, run.Invoke(null, null));
    }

    private static MetadataReference[] GetReferencesWithRavenCore()
    {
        var corePath = Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll");
        if (!File.Exists(corePath))
            return TestMetadataReferences.Default;

        return [.. TestMetadataReferences.Default, MetadataReference.CreateFromFile(corePath)];
    }
}
