using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Syntax;

using Xunit;

namespace Raven.CodeAnalysis.Tests.CodeGen.Async;

public sealed class YieldFromCodeGenTests
{
    [Theory]
    [InlineData("yield from source")]
    [InlineData("let done = (yield from source)")]
    public void Sync_DelegatesLazilyAndDisposes(string delegation)
    {
        var method = Compile($$"""
            class Counter {
                func Values(source: IEnumerable<int>) -> IEnumerable<long> {
                    yield 42
                    {{delegation}}
                    yield 99
                }
            }
            """);
        var started = 0;
        var disposed = 0;
        IEnumerable<int> Source()
        {
            started++;
            try
            {
                yield return 1;
                yield return 2;
            }
            finally { disposed++; }
        }
        var values = (IEnumerable<long>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [Source()])!;
        Assert.Equal(0, started);
        using (var iterator = values.GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            Assert.Equal(42, iterator.Current);
            Assert.Equal(0, started);
            Assert.True(iterator.MoveNext());
            Assert.Equal(1, iterator.Current);
            Assert.Equal(1, started);
        }
        Assert.Equal(1, disposed);
        Assert.Equal(new long[] { 42, 1, 2, 99 }, values.ToArray());
        Assert.Equal(2, disposed);
    }

    [Theory]
    [InlineData("yield from source")]
    [InlineData("let done = (yield from source)")]
    public async Task Async_DelegatesAndAwaitsEarlyDisposal(string delegation)
    {
        var method = Compile($$"""
            class Counter {
                async func Values(source: IAsyncEnumerable<int>, [EnumeratorCancellation] token: CancellationToken) -> IAsyncEnumerable<int> {
                    {{delegation}}
                    yield 99
                }
            }
            """);
        var source = new TrackingAsyncEnumerable();
        var values = (IAsyncEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [source, default(CancellationToken)])!;
        Assert.Equal(0, source.Started);
        var iterator = values.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(1, iterator.Current);
        var disposal = iterator.DisposeAsync();
        Assert.False(disposal.IsCompleted);
        Assert.Equal(1, source.DisposeStarted);
        source.ReleaseDisposal();
        await disposal;
        Assert.Equal(1, source.Disposed);
        Assert.False(await iterator.MoveNextAsync());
        await iterator.DisposeAsync();
        Assert.Equal(1, source.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Async_ForwardsCombinedCancellationAndDisposesOnFailure(bool cancelMethodToken)
    {
        var method = Compile("""
            class Counter {
                async func Values(source: IAsyncEnumerable<int>, [EnumeratorCancellation] token: CancellationToken) -> IAsyncEnumerable<int> {
                    yield from source
                }
            }
            """);
        var source = new TrackingAsyncEnumerable();
        source.ReleaseDisposal();
        using var methodCancellation = new CancellationTokenSource();
        using var consumerCancellation = new CancellationTokenSource();
        var values = (IAsyncEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [source, methodCancellation.Token])!;
        await using var iterator = values.GetAsyncEnumerator(consumerCancellation.Token);
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(source.Token.CanBeCanceled);
        (cancelMethodToken ? methodCancellation : consumerCancellation).Cancel();
        Assert.True(source.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await iterator.MoveNextAsync());
        Assert.Equal(1, source.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Async_CompletesDelegationAndContinues(bool useRuntimeAsync)
    {
        var method = Compile("""
            class Counter {
                async func Values(source: IAsyncEnumerable<int>) -> IAsyncEnumerable<int> {
                    yield 42
                    yield from source
                    yield 99
                }
            }
            """, useRuntimeAsync);
        var source = new TrackingAsyncEnumerable();
        source.ReleaseDisposal();
        var values = (IAsyncEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [source])!;
        var result = new List<int>();
        await foreach (var value in values) result.Add(value);
        Assert.Equal(new[] { 42, 1, 2, 99 }, result);
        Assert.Equal(1, source.Disposed);
    }

    [Fact]
    public async Task Async_PendingMoveNextCancelsWithoutBlocking()
    {
        var method = Compile("""
            class Counter {
                async func Values(source: IAsyncEnumerable<int>, [EnumeratorCancellation] token: CancellationToken) -> IAsyncEnumerable<int> {
                    yield from source
                }
            }
            """);
        var source = new TrackingAsyncEnumerable();
        source.ReleaseDisposal();
        using var cancellation = new CancellationTokenSource();
        var values = (IAsyncEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [source, cancellation.Token])!;
        await using var iterator = values.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        source.BlockNext = true;
        var pending = iterator.MoveNextAsync();
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await pending);
        Assert.Equal(1, source.Disposed);
    }

    [Fact]
    public async Task Async_OuterFinallyRunsWhenDelegatedDisposalThrows()
    {
        var method = Compile("""
            import System.*
            class Counter {
                async func Values(source: IAsyncEnumerable<int>, finished: Action) -> IAsyncEnumerable<int> {
                    try {
                        yield from source
                    } finally {
                        finished()
                    }
                }
            }
            """);
        var source = new TrackingAsyncEnumerable { ThrowOnDispose = true };
        source.ReleaseDisposal();
        var finished = 0;
        var values = (IAsyncEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [source, (Action)(() => finished++)])!;
        var iterator = values.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await iterator.DisposeAsync());
        Assert.Equal(1, finished);
    }

    [Fact]
    public void Sync_OuterFinallyRunsOnEarlyDisposal()
    {
        var method = Compile("""
            import System.*
            class Counter {
                func Values(source: IEnumerable<int>, finished: Action) -> IEnumerable<int> {
                    try {
                        yield from source
                    } finally {
                        finished()
                    }
                }
            }
            """);
        var finished = 0;
        var values = (IEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [new[] { 1, 2 }, (Action)(() => finished++)])!;
        using (var iterator = values.GetEnumerator())
        {
            Assert.True(iterator.MoveNext());
            Assert.Equal(0, finished);
        }
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task Async_CanDelegateSynchronousSequence()
    {
        var method = Compile("""
            class Counter {
                async func Values(source: IEnumerable<int>) -> IAsyncEnumerable<int> {
                    yield from source
                }
            }
            """);
        var values = (IAsyncEnumerable<int>)method.Invoke(Activator.CreateInstance(method.DeclaringType!), [new[] { 1, 2 }])!;
        var result = new List<int>();
        await foreach (var value in values) result.Add(value);
        Assert.Equal(new[] { 1, 2 }, result);
    }

    private sealed class TrackingAsyncEnumerable : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockNext { get; set; }
        public bool ThrowOnDispose { get; set; }
        public int Started { get; private set; }
        public int DisposeStarted { get; private set; }
        public int Disposed { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Current { get; private set; }
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Started++;
            Token = cancellationToken;
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            Token.ThrowIfCancellationRequested();
            if (BlockNext)
                await Task.Delay(Timeout.Infinite, Token);
            return ++Current <= 2;
        }
        public async ValueTask DisposeAsync()
        {
            DisposeStarted++;
            await _disposeCompletion.Task;
            Disposed++;
            if (ThrowOnDispose)
                throw new InvalidOperationException("disposal failed");
        }
        public void ReleaseDisposal() => _disposeCompletion.TrySetResult();
    }

    private static MethodInfo Compile(string body, bool useRuntimeAsync = false)
    {
        var tree = SyntaxTree.ParseText("""
            import System.Collections.Generic.*
            import System.Runtime.CompilerServices.*
            import System.Threading.*
            import System.Threading.Tasks.*

            """ + "\n" + body);
        var references = TestMetadataReferences.Default;
        var compilation = Compilation.Create("yield_from_test", new CompilationOptions(OutputKind.DynamicallyLinkedLibrary, useRuntimeAsync: useRuntimeAsync))
            .AddSyntaxTrees(tree).AddReferences(references);
        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var loaded = TestAssemblyLoader.LoadFromStream(pe, references);
        return loaded.Assembly.GetType("Counter", true)!.GetMethod("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    }
}
