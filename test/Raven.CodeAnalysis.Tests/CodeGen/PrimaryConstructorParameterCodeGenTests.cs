using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Semantics.Tests;

using Xunit;

namespace Raven.CodeAnalysis.Tests.CodeGen;

public sealed class PrimaryConstructorParameterCodeGenTests : CompilationTestBase
{
    [Fact]
    public void Emit_RecordToStringOverride_UsesDeclaredImplementation()
    {
        const string source = """
record ItemId private (Value: int) {
    static func Create(value: int) -> ItemId => ItemId(value)

    override func ToString() -> string? => Value.ToString()
}

class Runner {
    public static func Run() -> string? => ItemId.Create(42).ToString()
}
""";

        var (compilation, _) = CreateCompilation(source);
        compilation.EnsureSetup();

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        peStream.Position = 0;
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var runnerType = loaded.Assembly.GetType("Runner", throwOnError: true)!;
        var runMethod = runnerType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal("42", runMethod.Invoke(null, null));
    }

    [Fact]
    public void Emit_PrivateRecordStructPrimaryConstructor_HasPrivateMetadataAccessibility()
    {
        const string source = """
record struct Year private (Value: int) {
    static func Create(value: int) -> Year => Year(value)
}
""";

        var (compilation, _) = CreateCompilation(source);
        compilation.EnsureSetup();

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        peStream.Position = 0;
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var type = loaded.Assembly.GetType("Year", throwOnError: true)!;
        var constructor = Assert.Single(type
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(static constructor =>
                constructor.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(int)));

        Assert.True(constructor.IsPrivate);
    }

    [Theory]
    [InlineData(21, "ok:50")]
    [InlineData(0, "error:Weight must be positive")]
    [InlineData(-1, "error:Weight must be positive")]
    public async Task Emit_AsyncPrimaryConstructorClassMethod_PreservesParametersAcrossSuspension(int weight, string expected)
    {
        const string source = """
import System.*
import System.Threading.Tasks.*

union Result<T, E> {
    case Ok(value: T)
    case Error(value: E)
}

class ShipmentOrderService(private var pendingCount: int = 0) {
    async func BuildQuote(weightKg: int, gate: Task<int>) -> Task<Result<int, string>> {
        let surcharge = await gate

        if weightKg > 0 {
            return .Ok(weightKg * 2 + pendingCount + surcharge)
        }

        return .Error("Weight must be positive")
    }

    async func Run(weightKg: int, gate: Task<int>) -> Task<string> {
        let quote = await BuildQuote(weightKg, gate)
        return match quote {
            .Ok(let value) => "ok:" + value.ToString()
            .Error(let error) => "error:" + error
        }
    }
}
""";

        var (compilation, _) = CreateCompilation(source);
        compilation.EnsureSetup();

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        peStream.Position = 0;
        using var loaded = TestAssemblyLoader.LoadFromStream(peStream, TestMetadataReferences.Default);
        var type = loaded.Assembly.GetType("ShipmentOrderService", throwOnError: true)!;
        var instance = Activator.CreateInstance(type, new object[] { 3 })!;
        var method = type.GetMethod("Run")!;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Assert.IsAssignableFrom<Task<string>>(method.Invoke(instance, [weight, gate.Task]));
        try
        {
            Assert.False(task.IsCompleted);
            gate.SetResult(5);
            Assert.Equal(expected, await task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            gate.TrySetCanceled();
        }
    }
}
