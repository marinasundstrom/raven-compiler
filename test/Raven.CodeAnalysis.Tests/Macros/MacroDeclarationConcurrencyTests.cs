using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Raven.CodeAnalysis.Macros;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests.Macros;

public sealed class MacroDeclarationConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdMacroQueries_DeferOtherDocumentsUntilDeclarationsComplete(bool asynchronous)
    {
        var firstTree = SyntaxTree.ParseText("import Raven.CodeAnalysis.Tests.Macros.*\n#[identity] class First {} ");
        var secondTree = SyntaxTree.ParseText("import Raven.CodeAnalysis.Tests.Macros.*\n#[identity] class Second {} ");
        var macro = new IdentityMacro();
        var compilation = Compilation.Create("ConcurrentMacros")
            .AddReferences(TestMetadataReferences.Default)
            .AddSyntaxTrees(firstTree, secondTree)
            .AddMacroReferences(new MacroReference(macro));
        var first = compilation.GetSemanticModel(firstTree);
        var second = compilation.GetSemanticModel(secondTree);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (first.EnterSemanticAccess(timeout.Token))
        {
            // A different request must not hold Second's gate while First is
            // still able to initialize shared declarations and expand its macro.
            Task<bool> competingQuery;
            using (ExecutionContext.SuppressFlow())
                competingQuery = Task.Run(async () =>
                {
                    using var access = asynchronous
                        ? await second.TryEnterSemanticAccessAsync(timeout.Token)
                        : second.TryEnterSemanticAccess(timeout.Token);
                    return access is not null;
                });
            Assert.False(await competingQuery.WaitAsync(timeout.Token));
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ISymbol?> queuedQuery;
            using (ExecutionContext.SuppressFlow())
                queuedQuery = Task.Run(async () =>
                {
                    var pendingAccess = second.EnterSemanticAccessAsync(timeout.Token);
                    waiting.SetResult();
                    using var access = await pendingAccess;
                    using var ambient = second.EnterAmbientSemanticAccess();
                    return second.GetDeclaredSymbol(secondTree.GetRoot().Members.Single());
                });
            await waiting.Task.WaitAsync(timeout.Token);
            Assert.False(queuedQuery.IsCompleted);
            Assert.Equal("First", first.GetDeclaredSymbol(firstTree.GetRoot().Members.Single())?.Name);
            Assert.Equal("Second", (await queuedQuery.WaitAsync(timeout.Token))?.Name);

            // Once declarations are initialized, unrelated document requests
            // retain independent semantic access even while First is still held.
            using (ExecutionContext.SuppressFlow())
                competingQuery = Task.Run(async () =>
                {
                    using var access = asynchronous
                        ? await second.TryEnterSemanticAccessAsync(timeout.Token)
                        : second.TryEnterSemanticAccess(timeout.Token);
                    if (access is null)
                        return false;
                    using var ambient = second.EnterAmbientSemanticAccess();
                    return second.GetDeclaredSymbol(secondTree.GetRoot().Members.Single())?.Name == "Second";
                });
            Assert.True(await competingQuery.WaitAsync(timeout.Token));
        }
        Assert.Equal(2, macro.ExpansionCount);
    }

    private sealed class IdentityMacro : IMacroDefinition
    {
        public string Name => "identity";
        public MacroTarget Targets => MacroTarget.Type;
        public int ExpansionCount;
        public MacroExpansionResult Expand(AttachedMacroContext context)
        {
            Interlocked.Increment(ref ExpansionCount);
            return MacroExpansionResult.Empty;
        }
    }
}
