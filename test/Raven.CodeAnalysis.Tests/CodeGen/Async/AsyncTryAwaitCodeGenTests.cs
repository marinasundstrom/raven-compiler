using System;
using System.IO;
using System.Linq;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Tests.Utilities;

using Xunit;
using Xunit.Abstractions;

namespace Raven.CodeAnalysis.Tests;

public sealed class AsyncTryAwaitCodeGenTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void TryAwaitExpression_EmitsAndRuns()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Fetch() -> Task<Result<int, Exception>> {
        let value = try? await Task.FromResult(42)
        return .Ok(value)
    }

    static async func Main() -> Task {
        let result = await Program.Fetch()
        Console.WriteLine(result)
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "Result.Ok(42)" }, output);
    }

    [Fact]
    public void TryAwaitExpression_WithUse_EmitsAndRuns()
    {
        const string code = """
import System.*
import System.IO.*
import System.Threading.Tasks.*

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class Program {
    static async func ThrowingAsync() -> Task<int> {
        throw Exception("boom")
    }

    static async func Fetch(shouldThrow: bool) -> Task<Result<int, Exception>> {
        use stream = MemoryStream()

        if shouldThrow {
            let value = try? await Program.ThrowingAsync()
            return .Ok(value)
        }

        let okValue = try? await Task.FromResult(7)
        return .Ok(okValue)
    }

    static async func Main() -> Task {
        let success = await Program.Fetch(false)
        let failure = await Program.Fetch(true)

        let successText = match success {
            .Ok(let v) => "ok:" + v.ToString()
            .Error(let e) => "err:" + e.Message
        }

        let failureText = match failure {
            .Ok(let v) => "ok:" + v.ToString()
            .Error(let e) => "err:" + e.Message
        }

        Console.WriteLine(successText)
        Console.WriteLine(failureText)
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "ok:7", "err:boom" }, output);
    }

    [Fact]
    public void TryAwaitExpression_WithTaskOfResultOperand_ProjectsThrownExceptionToError()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Action(throwExc: bool) -> Task<Result<int, Exception>> {
        await Task.Delay(1)
        if throwExc {
            throw Exception("Boom!")
        }

        return .Ok(40)
    }

    static async func Test(throwExc: bool) -> Task<Result<int, Exception>> {
        let x = try? await Program.Action(throwExc)
        return .Ok(x + 2)
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.Test(false))
        Console.WriteLine(await Program.Test(true))
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal("Result.Ok(42)", output[0]);
        Assert.Contains("Error", output[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TryAwaitExpression_AsMatchInput_EmitsAndRuns()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Fetch() -> Task<int> {
        await Task.Delay(1)
        return 42
    }

    static async func Main() -> Task {
        let text = try await Program.Fetch() match {
            .Ok(let value) => value.ToString()
            .Error(Exception ex) => ex.Message
        }

        Console.WriteLine(text)
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "42" }, output);
    }

    [Fact]
    public void AsyncUse_WithResultMatchReturn_EmitsValidSetResultCall()
    {
        const string code = """
import System.*
import System.IO.*
import System.Threading.Tasks.*

union ApiError {
    case Network(ex: Exception)
}

class Program {
    static async func DownloadText() -> Task<Result<string, ApiError>> {
        use stream = MemoryStream()

        return try await Task.FromResult("ok") match {
            .Ok(let text) => .Ok(text)
            .Error(Exception ex) => .Error(ApiError.Network(ex))
        }
    }

    static async func Main() -> Task {
        let result = await Program.DownloadText()
        Console.WriteLine(result)
    }
}
""";

        EmitOnly(code);
    }

    [Fact]
    public void TryAwaitExpression_WithReferenceTypeOkPayload_DereferencesAfterAwait()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

record class Payload(Text: string)

class Program {
    static async func FetchPayload() -> Task<Result<Payload, Exception>> {
        await Task.Delay(1)
        return .Ok(Payload("hello"))
    }

    static async func FetchTextLength() -> Task<Result<int, Exception>> {
        let payload = try? await Program.FetchPayload()
        await Task.Delay(1)
        return .Ok(payload.Text.Length)
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.FetchTextLength())
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "Result.Ok(5)" }, output);
    }

    [Fact]
    public void TryExpression_WithGenericInvocationInsideAsyncMethod_EmitsAndRuns()
    {
        const string code = """
import System.*
import System.Text.Json.*
import System.Threading.Tasks.*

union Result<T, E> {
    case Ok(value: T)
    case Error(error: E)
}

class Program {
    static async func Run() -> Task<int> {
        await Task.Delay(1)

        let result = try JsonSerializer.Deserialize<int>("1")

        if result is .Ok(let value) {
            return value
        }

        if result is .Error(Exception ex) {
            Console.WriteLine(ex.Message)
            return -1
        }

        return -1
    }

    static func Main() {
        Console.WriteLine(Program.Run().Result)
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "1" }, output);
    }

    [Fact]
    public void AsyncUse_PrefersDisposeAsync_WhenAvailable()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class AsyncProbe : IAsyncDisposable, IDisposable {
    public func Dispose() -> unit => Console.WriteLine("Dispose")
    public func DisposeAsync() -> ValueTask {
        Console.WriteLine("DisposeAsync")
        return ValueTask.CompletedTask
    }
}

class Program {
    static async func Main() -> Task {
        use probe = AsyncProbe()
        await Task.Delay(1)
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "DisposeAsync" }, output);
    }

    [Fact]
    public void AsyncUse_FallsBackToDispose_WhenDisposeAsyncIsUnavailable()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Probe : IDisposable {
    public func Dispose() -> unit => Console.WriteLine("Dispose")
}

class Program {
    static async func Main() -> Task {
        use probe = Probe()
        await Task.Delay(1)
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "Dispose" }, output);
    }

    [Fact]
    public void AwaitInFinally_SuspendsAndPreservesReturnAndExceptionFlow()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Run(mode: int) -> Task<string> {
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
            Console.WriteLine("finally:first:$mode:$step")
            await Task.Delay(1)
            step = step + 1
            Console.WriteLine("finally:second:$mode:$step")
        }
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.Run(0))
        Console.WriteLine(await Program.Run(2))

        try {
            _ = await Program.Run(1)
        } catch (Exception ex) {
            Console.WriteLine(ex.Message)
        }
    }
}
""";

        var output = CompileAndRun(code, verifyIl: true);
        Assert.Equal(
            new[]
            {
                "finally:first:0:1",
                "finally:second:0:2",
                "normal",
                "finally:first:2:1",
                "finally:second:2:2",
                "early",
                "finally:first:1:1",
                "finally:second:1:2",
                "try-failure",
            },
            output);
    }

    [Fact]
    public void AwaitInFinally_TaskMethodFallsThroughAfterMultipleSuspensions()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Run() -> Task {
        try {
            await Task.Delay(1)
            Console.WriteLine("try")
        } finally {
            var step = 0
            await Task.Delay(1)
            step = step + 1
            Console.WriteLine("finally:first:$step")
            await Task.Delay(1)
            step = step + 1
            Console.WriteLine("finally:second:$step")
        }
    }

    static async func Main() -> Task {
        await Program.Run()
        Console.WriteLine("completed")
    }
}
""";

        var output = CompileAndRun(code, verifyIl: true);
        Assert.Equal(
            new[] { "try", "finally:first:1", "finally:second:2", "completed" },
            output);
    }

    [Fact]
    public void AwaitInCatch_SuspendsAndPreservesNormalReturnAndExceptionFlow()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Run(mode: int) -> Task<string> {
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
            Console.WriteLine("catch:first:$mode:$step")
            await Task.Delay(1)
            step = step + 1
            Console.WriteLine("catch:second:$mode:$step")

            if mode == 2 {
                throw ex
            }

            return "handled:" + ex.Message
        }
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.Run(0))
        Console.WriteLine(await Program.Run(1))

        try {
            _ = await Program.Run(2)
        } catch (Exception ex) {
            Console.WriteLine("outer:" + ex.Message)
        }
    }
}
""";

        var output = CompileAndRun(code, verifyIl: true);
        Assert.Equal(
            new[]
            {
                "normal",
                "catch:first:1:1",
                "catch:second:1:2",
                "handled:failure:1",
                "catch:first:2:1",
                "catch:second:2:2",
                "outer:failure:2",
            },
            output);
    }

    [Fact]
    public void AwaitInCatchAndFinally_SuspendsInBothHandlers()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Program {
    static async func Run() -> Task<string> {
        try {
            await Task.Delay(1)
            throw Exception("failure")
        } catch (Exception ex) {
            var catchStep = 0
            await Task.Delay(1)
            catchStep = catchStep + 1
            Console.WriteLine("catch:first:" + ex.Message + ":" + catchStep.ToString())
            await Task.Delay(1)
            catchStep = catchStep + 1
            Console.WriteLine("catch:second:" + ex.Message + ":" + catchStep.ToString())
            return "handled"
        } finally {
            var finallyStep = 0
            await Task.Delay(1)
            finallyStep = finallyStep + 1
            Console.WriteLine("finally:first:$finallyStep")
            await Task.Delay(1)
            finallyStep = finallyStep + 1
            Console.WriteLine("finally:second:$finallyStep")
        }
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.Run())
    }
}
""";

        var output = CompileAndRun(code, verifyIl: true);
        Assert.Equal(
            new[]
            {
                "catch:first:failure:1",
                "catch:second:failure:2",
                "finally:first:1",
                "finally:second:2",
                "handled",
            },
            output);
    }

    [Fact]
    public void Async_ConditionalAccessThenPropagate_UsesSameOutStorage()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

union Err {
    case MissingUser
    case MissingName
}

class User {
    public var Name: string { get; set; } = ""
    public var Item: Option<Item> { get; set; } = .None
}

record class Item(Name: string)

class Program {
    static func GetUser() -> Result<User, Err> {
        return .Ok(User { Name = "Marina", Item = Item("Candy") })
    }

    static async func GetItem() -> Task<Result<string, Err>> {
        let maybeItem = GetUser()?.Item?
        await Task.Delay(1)

        return match maybeItem {
            .Some(let item) => .Ok(item.Name)
            .None => .Error(Err.MissingName)
        }
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.GetItem())
    }
}
""";

        EmitOnly(code);
    }


    [Fact]
    public void AsyncOptionInvocation_NoneBranch_EmitsAndRuns()
    {
        const string code = """
import System.*
import System.Option.*
import System.Threading.Tasks.*

class Program {
    static async func Fetch(flag: bool) -> Task<Option<int>> {
        await Task.Delay(1)

        if flag {
            return Some(42)
        }

        return None
    }

    static async func Main() -> Task {
        let some = await Program.Fetch(true)
        let none = await Program.Fetch(false)

        Console.WriteLine(match some {
            Some(let value) => "Some: $value"
            None => "None"
        })

        Console.WriteLine(match none {
            Some(let value) => "Some: $value"
            None => "None"
        })
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "Some: 42", "None" }, output);
    }

    [Fact]
    public void AsyncMethod_PatternLocalUsedAfterAwait_IsPreserved()
    {
        const string code = """
import System.*
import System.Threading.Tasks.*

class Person(value: int) {
    val Value: int => value
}

class Program {
    static async func Read(person: Person?) -> Task<int> {
        if let value: Person = person {
            await Task.Yield()
            return value.Value
        }

        return -1
    }

    static async func Main() -> Task {
        Console.WriteLine(await Program.Read(Person(42)))
        Console.WriteLine(await Program.Read(null))
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "42", "-1" }, output);
    }

    [Fact]
    public void MetadataOption_InferredMatchResult_ClosesParameterlessCaseCarrier()
    {
        const string code = """
import System.*
import System.Option.*

let input: Option<int> = Some(42)
let result = match input {
    Some(let value) => Option<int>.Some(value * 2)
    None => None
}

Console.WriteLine(result)
""";

        var output = CompileAndRun(code);
        Assert.Equal(new[] { "Option.Some(84)" }, output);
    }

    [Fact]
    public void GenericAsyncMethod_ReturningGenericUnionAfterAwait_EmitsAndRuns()
    {
        const string code = """
import System.*
import System.Console.*
import System.Threading.Tasks.*

union TaskState<T> {
    case Success(value: T)
    case Fault(exception: Exception?)
    case Canceled
}

class Program {
    static async func AwaitState<T>(task: Task<T>) -> Task<TaskState<T>> {
        await task
        return match task.Status {
            .RanToCompletion => .Success(task.Result)
            .Faulted => .Fault(task.Exception)
            .Canceled => .Canceled
            _ => .Canceled
        }
    }

    static async func Main() -> Task {
        let state = await Program.AwaitState(Task.FromResult(42))
        _ = state
    }
}
""";

        var output = CompileAndRun(code);
        Assert.Empty(output);
    }

    private string[] CompileAndRun(string code, bool verifyIl = false)
    {
        var syntaxTree = SyntaxTree.ParseText(code);
        var references = GetReferencesWithRavenCore();
        var compilation = Compilation.Create(
            "async-try-await",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.ConsoleApplication));

        var assemblyPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll");

        try
        {
            using (var peStream = File.Create(assemblyPath))
            {
                var emitResult = compilation.Emit(peStream);
                Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
            }

            if (verifyIl && IlVerifyTestHelper.TryResolve(_output))
            {
                Assert.True(
                    IlVerifyRunner.Verify(null, assemblyPath, compilation),
                    "IL verification failed for an async exception-handler state machine.");
            }

            using var assemblyStream = File.OpenRead(assemblyPath);
            using var loaded = TestAssemblyLoader.LoadFromStream(assemblyStream, references);
            var entryPoint = loaded.Assembly.EntryPoint!;
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                var parameters = entryPoint.GetParameters().Length == 0
                    ? null
                    : new object?[] { Array.Empty<string>() };
                entryPoint.Invoke(null, parameters);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            return writer.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToArray();
        }
        finally
        {
            if (File.Exists(assemblyPath))
                File.Delete(assemblyPath);
        }
    }

    private static void EmitOnly(string code)
    {
        var syntaxTree = SyntaxTree.ParseText(code);
        var references = GetReferencesWithRavenCore();
        var compilation = Compilation.Create(
            "async-try-await",
            [syntaxTree],
            references,
            new CompilationOptions(OutputKind.ConsoleApplication));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));
    }

    private static MetadataReference[] GetReferencesWithRavenCore()
    {
        var corePath = Path.Combine(AppContext.BaseDirectory, "Raven.Core.dll");
        if (!File.Exists(corePath))
            return TestMetadataReferences.Default;

        return [.. TestMetadataReferences.Default, MetadataReference.CreateFromFile(corePath)];
    }
}
