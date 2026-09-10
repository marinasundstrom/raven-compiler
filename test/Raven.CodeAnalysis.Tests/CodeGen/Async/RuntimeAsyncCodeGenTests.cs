using System;
using System.IO;
using System.Reflection;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public sealed class RuntimeAsyncCodeGenTests
{
    private const int RuntimeAsyncMethodImplBit = 0x2000;

    [Fact]
    public void RuntimeAsyncEnabled_EmitsAsyncMethodImplFlag()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    async func Compute() -> Task<int> {
        return await Task.FromResult(1)
    }

    func Sync() -> int {
        return 2
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var asyncMethod = programType.GetMethod("Compute", methodFlags)!;
        var syncMethod = programType.GetMethod("Sync", methodFlags)!;

        Assert.NotEqual(0, ((int)asyncMethod.GetMethodImplementationFlags()) & RuntimeAsyncMethodImplBit);
        Assert.Equal(0, ((int)syncMethod.GetMethodImplementationFlags()) & RuntimeAsyncMethodImplBit);
    }

    [Fact]
    public void RuntimeAsyncDisabled_DoesNotEmitAsyncMethodImplFlag()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    async func Compute() -> Task<int> {
        return await Task.FromResult(1)
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: false);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var asyncMethod = programType.GetMethod("Compute", methodFlags)!;

        Assert.Equal(0, ((int)asyncMethod.GetMethodImplementationFlags()) & RuntimeAsyncMethodImplBit);
    }

    [Fact]
    public void RuntimeAsyncEnabled_DoesNotMarkAsyncIteratorAsRuntimeAsync()
    {
        const string code = """
import System.Collections.Generic.*
import System.Threading.Tasks.*

class Program {
    async func Stream() -> IAsyncEnumerable<int> {
        yield 1
        await Task.Delay(1)
        yield 2
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var streamMethod = programType.GetMethod("Stream", methodFlags)!;

        Assert.Equal(0, ((int)streamMethod.GetMethodImplementationFlags()) & RuntimeAsyncMethodImplBit);
    }

    [Fact]
    public void RuntimeAsyncEnabled_DoesNotEmitAsyncStateMachineType()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    async func Compute() -> Task<int> {
        return await Task.FromResult(1)
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var generatedTypes = loaded.Assembly.GetTypes();
        Assert.DoesNotContain(
            generatedTypes,
            static t => t.Name.Contains("AsyncStateMachine", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeAsyncEnabled_SupportsValueTaskAndConfiguredAwaitShapes()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    async func ComputeTaskConfigured() -> Task<int> {
        return await Task.FromResult(1).ConfigureAwait(false)
    }

    async func ComputeTask() -> Task {
        await Task.Delay(1).ConfigureAwait(false)
    }

    async func ComputeValueTaskConfigured() -> Task<int> {
        return await ValueTask.FromResult(2).ConfigureAwait(false)
    }

    async func ComputeValueTask() -> Task<int> {
        return await ValueTask.FromResult(3)
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var expectedAsyncMethods = new[]
        {
            "ComputeTaskConfigured",
            "ComputeTask",
            "ComputeValueTaskConfigured",
            "ComputeValueTask"
        };

        foreach (var methodName in expectedAsyncMethods)
        {
            var asyncMethod = programType.GetMethod(methodName, methodFlags)!;
            Assert.NotEqual(0, ((int)asyncMethod.GetMethodImplementationFlags()) & RuntimeAsyncMethodImplBit);
        }

        var generatedTypes = loaded.Assembly.GetTypes();
        Assert.DoesNotContain(
            generatedTypes,
            static t => t.Name.Contains("AsyncStateMachine", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeAsyncEnabled_Await_ReturnsValue()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    async func Compute() -> Task<int> {
        return await Task.FromResult(1)
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var asyncMethod = programType.GetMethod("Compute", methodFlags)!;
        var task = Assert.IsAssignableFrom<Task<int>>(asyncMethod.Invoke(Activator.CreateInstance(programType), null));
        Assert.Equal(1, task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    }

    [Fact]
    public void RuntimeAsyncEnabled_Net11AsyncTaskEntryPoint_ReturnsExitCode()
    {
        if (!RuntimeAsyncEntryPointHandlerAvailable())
            return;

        const string code = """
import System.Threading.Tasks.*

async func Main() -> Task<int> {
    await Task.Yield()
    return 5
}
""";

        using var loaded = EmitAssembly(
            code,
            useRuntimeAsync: true,
            outputKind: OutputKind.ConsoleApplication,
            references: GetFrameworkReferences("net11.0"));

        var entryPoint = loaded.Assembly.EntryPoint;
        Assert.NotNull(entryPoint);

        Assert.Equal(5, entryPoint!.Invoke(null, entryPoint.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() }));
    }

    [Fact]
    public void RuntimeAsyncEnabled_YieldAwaitable_Completes()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    public async func Compute() -> Task<int> {
        await Task.Yield()
        return 42
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var computeMethod = programType.GetMethod("Compute", methodFlags)!;
        var program = Activator.CreateInstance(programType);
        var task = Assert.IsAssignableFrom<Task<int>>(computeMethod.Invoke(program, null));
        Assert.Equal(42, task.GetAwaiter().GetResult());
    }

    [Fact]
    public void RuntimeAsyncDisabled_Net11YieldAwaitable_UsesStateMachineAndCompletes()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    public async func Compute() -> Task<int> {
        await Task.Yield()
        return 42
    }
}
""";

        using var loaded = EmitAssembly(
            code,
            useRuntimeAsync: false,
            references: GetFrameworkReferences("net11.0"));

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var computeMethod = programType.GetMethod("Compute", methodFlags)!;

        Assert.Equal(0, ((int)computeMethod.GetMethodImplementationFlags()) & RuntimeAsyncMethodImplBit);
        Assert.Contains(
            loaded.Assembly.GetTypes(),
            static type => type.Name.Contains("AsyncStateMachine", StringComparison.Ordinal));

        var program = Activator.CreateInstance(programType);
        var task = Assert.IsAssignableFrom<Task<int>>(computeMethod.Invoke(program, null));
        Assert.Equal(42, task.GetAwaiter().GetResult());
    }

    [Fact]
    public void RuntimeAsyncRequested_Net10AsyncTaskEntryPoint_ReturnsExitCode()
    {
        const string code = """
import System.Threading.Tasks.*

async func Main() -> Task<int> {
    await Task.Yield()
    return 5
}
""";

        using var loaded = EmitAssembly(
            code,
            useRuntimeAsync: true,
            outputKind: OutputKind.ConsoleApplication,
            references: GetFrameworkReferences("net10.0"));

        var entryPoint = Assert.IsAssignableFrom<MethodInfo>(loaded.Assembly.EntryPoint);
        Assert.Equal(5, entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() }));
    }

    [Fact]
    public void RuntimeAsyncEnabled_TryCatchReturn_ReturnsValue()
    {
        const string code = """
import System.*
import System.IO.*
import System.Threading.Tasks.*

class Program {
    async func Fetch() -> Task<int> {
        use stream = MemoryStream()
        try {
            let value = await Task.FromResult(42)
            return value
        } catch (Exception e) {
            return -1
        }
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var fetchMethod = programType.GetMethod("Fetch", methodFlags)!;
        var task = Assert.IsAssignableFrom<Task<int>>(fetchMethod.Invoke(Activator.CreateInstance(programType), null));
        Assert.Equal(42, task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    }

    [Fact]
    public void RuntimeAsyncEnabled_AwaitInCatchAndFinally_MatchesStateMachineBehaviorWithoutGeneratedType()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    public static var Trace: string = ""

    public static func ResetTrace() -> unit {
        Program.Trace = ""
    }

    public static async func CatchOnly(mode: int) -> Task<string> {
        try {
            await Task.Delay(1)

            if mode > 0 {
                throw Exception("failure:$mode")
            }

            return "normal"
        } catch (Exception ex) {
            var step = 0
            await Task.Delay(1)
            step = step + 1
            Program.Trace = Program.Trace + "catch:first:$mode:$step;"
            await Task.Delay(1)
            step = step + 1
            Program.Trace = Program.Trace + "catch:second:$mode:$step;"

            if mode == 2 {
                throw ex
            }

            return "handled:" + ex.Message
        }
    }

    public static async func FinallyOnly(mode: int) -> Task<string> {
        try {
            await Task.Delay(1)

            if mode == 1 {
                throw Exception("try-failure")
            }

            if mode == 2 {
                return "early"
            }

            return "normal"
        } finally {
            var step = 0
            await Task.Delay(1)
            step = step + 1
            Program.Trace = Program.Trace + "finally:first:$mode:$step;"
            await Task.Delay(1)
            step = step + 1
            Program.Trace = Program.Trace + "finally:second:$mode:$step;"
        }
    }

    public static async func Combined() -> Task<string> {
        try {
            await Task.Delay(1)
            throw Exception("combined")
        } catch (Exception ex) {
            var catchStep = 0
            await Task.Delay(1)
            catchStep = catchStep + 1
            Program.Trace = Program.Trace + "catch:first:" + ex.Message + ":" + catchStep.ToString() + ";"
            await Task.Delay(1)
            catchStep = catchStep + 1
            Program.Trace = Program.Trace + "catch:second:" + ex.Message + ":" + catchStep.ToString() + ";"
            return "handled"
        } finally {
            var finallyStep = 0
            await Task.Delay(1)
            finallyStep = finallyStep + 1
            Program.Trace = Program.Trace + "finally:first:$finallyStep;"
            await Task.Delay(1)
            finallyStep = finallyStep + 1
            Program.Trace = Program.Trace + "finally:second:$finallyStep;"
        }
    }

    public static async func FinallyFallthrough() -> Task {
        try {
            await Task.Delay(1)
            Program.Trace = Program.Trace + "try;"
        } finally {
            var step = 0
            await Task.Delay(1)
            step = step + 1
            Program.Trace = Program.Trace + "finally:first:$step;"
            await Task.Delay(1)
            step = step + 1
            Program.Trace = Program.Trace + "finally:second:$step;"
        }
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.Static;
        var traceProperty = programType.GetProperty("Trace", methodFlags)!;
        var resetTrace = programType.GetMethod("ResetTrace", methodFlags)!;
        var catchOnly = programType.GetMethod("CatchOnly", methodFlags)!;
        var finallyOnly = programType.GetMethod("FinallyOnly", methodFlags)!;
        var combined = programType.GetMethod("Combined", methodFlags)!;
        var finallyFallthrough = programType.GetMethod("FinallyFallthrough", methodFlags)!;

        Assert.Equal("normal", InvokeTask(catchOnly, 0));
        Assert.Equal("", traceProperty.GetValue(null));

        Assert.Equal("handled:failure:1", InvokeTask(catchOnly, 1));
        Assert.Equal("catch:first:1:1;catch:second:1:2;", traceProperty.GetValue(null));

        resetTrace.Invoke(null, null);
        var catchException = Assert.ThrowsAny<Exception>(() => InvokeTask(catchOnly, 2));
        Assert.Equal("failure:2", catchException.Message);
        Assert.Equal("catch:first:2:1;catch:second:2:2;", traceProperty.GetValue(null));

        resetTrace.Invoke(null, null);
        Assert.Equal("normal", InvokeTask(finallyOnly, 0));
        Assert.Equal("finally:first:0:1;finally:second:0:2;", traceProperty.GetValue(null));

        resetTrace.Invoke(null, null);
        var finallyException = Assert.ThrowsAny<Exception>(() => InvokeTask(finallyOnly, 1));
        Assert.Equal("try-failure", finallyException.Message);
        Assert.Equal("finally:first:1:1;finally:second:1:2;", traceProperty.GetValue(null));

        resetTrace.Invoke(null, null);
        Assert.Equal("early", InvokeTask(finallyOnly, 2));
        Assert.Equal("finally:first:2:1;finally:second:2:2;", traceProperty.GetValue(null));

        resetTrace.Invoke(null, null);
        Assert.Equal("handled", InvokeTask(combined));
        Assert.Equal(
            "catch:first:combined:1;catch:second:combined:2;finally:first:1;finally:second:2;",
            traceProperty.GetValue(null));

        resetTrace.Invoke(null, null);
        InvokeVoidTask(finallyFallthrough);
        Assert.Equal("try;finally:first:1;finally:second:2;", traceProperty.GetValue(null));

        Assert.DoesNotContain(
            loaded.Assembly.GetTypes(),
            static type => type.Name.Contains("AsyncStateMachine", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeAsyncEnabled_BlockBodiedAsyncLambda_ReturnsExpectedTaskResult()
    {
        const string code = """
import System.Threading.Tasks.*

class Program {
    public async func Compute() -> Task<string> {
        let run = async () => {
            await Task.Delay(1)
            return "ok"
        }

        return await run()
    }
}
""";

        using var loaded = EmitAssembly(code, useRuntimeAsync: true);

        var programType = loaded.Assembly.GetType("Program", throwOnError: true)!;
        var methodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var computeMethod = programType.GetMethod("Compute", methodFlags)!;
        var task = Assert.IsAssignableFrom<Task<string>>(computeMethod.Invoke(Activator.CreateInstance(programType), null));
        Assert.Equal("ok", task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    }

    private static TestAssemblyLoader.LoadedAssembly EmitAssembly(
        string code,
        bool useRuntimeAsync,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary,
        MetadataReference[]? references = null)
    {
        var syntaxTree = SyntaxTree.ParseText(code);
        references ??= useRuntimeAsync
            ? GetFrameworkReferences("net11.0")
            : TestMetadataReferences.Default;

        var compilation = Compilation.Create(
            $"runtime-async-{Guid.NewGuid():N}",
            [syntaxTree],
            references,
            new CompilationOptions(outputKind).WithRuntimeAsync(useRuntimeAsync));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

        return TestAssemblyLoader.LoadFromStream(peStream, references);
    }

    private static string InvokeTask(MethodInfo method, params object?[] arguments)
    {
        var task = Assert.IsAssignableFrom<Task<string>>(method.Invoke(null, arguments));
        return task.GetAwaiter().GetResult();
    }

    private static void InvokeVoidTask(MethodInfo method, params object?[] arguments)
    {
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(null, arguments));
        task.GetAwaiter().GetResult();
    }

    private static MetadataReference[] GetFrameworkReferences(string targetFramework)
    {
        var version = TargetFrameworkResolver.ResolveVersion(targetFramework);
        return TargetFrameworkResolver.GetReferenceAssemblies(version)
            .Where(File.Exists)
            .Select(MetadataReference.CreateFromFile)
            .ToArray();
    }

    private static bool RuntimeAsyncEntryPointHandlerAvailable()
    {
        var asyncHelpersType = typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder)
            .Assembly
            .GetType("System.Runtime.CompilerServices.AsyncHelpers", throwOnError: false);

        return asyncHelpersType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Any(static method => string.Equals(method.Name, "HandleAsyncEntryPoint", StringComparison.Ordinal)) == true;
    }
}
