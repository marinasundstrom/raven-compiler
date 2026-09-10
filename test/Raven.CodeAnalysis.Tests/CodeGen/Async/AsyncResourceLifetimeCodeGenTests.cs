using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

using Xunit;

namespace Raven.CodeAnalysis.Tests;

public sealed class AsyncResourceLifetimeCodeGenTests
{
    [Theory]
    [InlineData("return", true)]
    [InlineData("recover", true)]
    [InlineData("throw", true)]
    [InlineData("fault", true)]
    [InlineData("cancel", true)]
    [InlineData("return", false)]
    [InlineData("recover", false)]
    [InlineData("throw", false)]
    [InlineData("fault", false)]
    [InlineData("cancel", false)]
    public async Task UseResources_DisposeBeforeCompletion_InReverseOrder(string exit, bool acquireBeforeAwait)
    {
        var source = $$"""
import System.*
import System.Collections.Generic.*
import System.Threading.Tasks.*

class Probe(name: string, trace: List<string>) : IDisposable {
    func Dispose() -> () {
        trace.Add("dispose:" + name)
    }
}

class Program {
    static async func Run(trace: List<string>, gate: Task<int>, fail: bool, recover: bool) -> Task<int> {
        trace.Add("entered")
        {{(acquireBeforeAwait ? "" : "let value = await gate")}}
        use first = Probe("first", trace)
        use second = Probe("second", trace)

        try {
            {{(acquireBeforeAwait ? "let value = await gate" : "")}}
            trace.Add("resumed")
            if fail {
                throw InvalidOperationException("after-await")
            }
            return value
        } catch (InvalidOperationException error) {
            trace.Add("caught:" + error.Message)
            if recover {
                return -1
            }
            throw error
        }
    }
}
""";

        var tree = SyntaxTree.ParseText(source);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("async-resource-lifetime", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddSyntaxTrees(tree)
            .AddReferences(references);

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        using var loaded = TestAssemblyLoader.LoadFromStream(stream, references);
        var method = loaded.Assembly.GetType("Program")!.GetMethod("Run")!;
        var trace = new List<string>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Assert.IsAssignableFrom<Task<int>>(method.Invoke(null, [trace, gate.Task, exit is "throw" or "recover", exit == "recover"]));

        try
        {
            Assert.False(task.IsCompleted);
            Assert.Equal(new[] { "entered" }, trace);

            if (exit == "cancel")
                gate.SetCanceled();
            else if (exit == "fault")
                gate.SetException(new InvalidOperationException("gate-failure"));
            else
                gate.SetResult(42);

            if (exit is "return" or "recover")
            {
                Assert.Equal(exit == "recover" ? -1 : 42, await task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else if (exit == "cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(exit == "throw" ? "after-await" : "gate-failure", error.Message);
            }

            var expected = new List<string> { "entered" };
            if (exit is "return" or "throw" or "recover")
                expected.Add("resumed");
            if (exit is "throw" or "recover" || exit == "fault" && acquireBeforeAwait)
                expected.Add("caught:" + (exit is "throw" or "recover" ? "after-await" : "gate-failure"));
            if (acquireBeforeAwait || exit is "return" or "throw" or "recover")
            {
                expected.Add("dispose:second");
                expected.Add("dispose:first");
            }
            Assert.Equal(expected, trace);
        }
        finally
        {
            // Release a suspended invocation even if the pre-resumption assertion fails.
            gate.TrySetCanceled();
        }
    }
}
