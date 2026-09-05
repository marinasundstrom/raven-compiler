using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Operations;
using Raven.CodeAnalysis.Syntax;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class AnalyzerInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedInitialization_DoesNotLeakRegistrationsIntoRetry(bool canceled)
    {
        var analyzer = new RetryingAnalyzer(canceled);
        var compilation = Compilation.Create("Initialization", [SyntaxTree.ParseText("class C { func M() { 1 + 2 } }")], TestMetadataReferences.Default);
        if (canceled)
            Should.Throw<OperationCanceledException>(() => analyzer.Analyze(compilation).ToArray());
        else
            analyzer.Analyze(compilation).ShouldBeEmpty();
        analyzer.Calls.ShouldBeEmpty();

        analyzer.Analyze(compilation).ToArray();
        analyzer.Calls.Order().ShouldBe(new[] { "compilation", "node", "operation", "symbol", "tree" });
        analyzer.ConcurrentExecutionEnabled.ShouldBeFalse();

        analyzer.Calls.Clear();
        analyzer.Analyze(compilation).ToArray();
        analyzer.Calls.Order().ShouldBe(new[] { "compilation", "node", "operation", "symbol", "tree" });
        analyzer.Initializations.ShouldBe(2);
    }

    private sealed class RetryingAnalyzer(bool canceled) : DiagnosticAnalyzer
    {
        public int Initializations { get; private set; }
        public List<string> Calls { get; } = [];

        public override void Initialize(AnalysisContext context)
        {
            Initializations++;
            context.RegisterCompilationAction(_ => Calls.Add("compilation"));
            context.RegisterSyntaxTreeAction(_ => Calls.Add("tree"));
            context.RegisterSyntaxNodeAction(_ => Calls.Add("node"), SyntaxKind.ClassDeclaration);
            context.RegisterSymbolAction(_ => Calls.Add("symbol"), SymbolKind.Type);
            context.RegisterOperationAction(_ => Calls.Add("operation"), OperationKind.ExpressionStatement);
            if (Initializations == 1)
            {
                context.EnableConcurrentExecution();
                if (canceled)
                    throw new OperationCanceledException();
                throw new InvalidOperationException("Initialization failed after registering actions.");
            }
        }
    }
}
