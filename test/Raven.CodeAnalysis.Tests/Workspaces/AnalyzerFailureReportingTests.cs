using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Operations;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class AnalyzerFailureReportingTests
{
    [Theory]
    [InlineData("Initialize")]
    [InlineData("Compilation")]
    [InlineData("SyntaxTree")]
    [InlineData("Symbol")]
    [InlineData("SyntaxNode")]
    [InlineData("Operation")]
    public void Failure_ReportsAnalyzerAndPhaseWithoutStoppingHealthyAnalyzers(string phase)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject("Failures");
        var projectId = solution.Projects.Single().Id;
        var documentId = DocumentId.CreateNew(projectId);
        solution = solution.AddDocument(documentId, "input.rvn", SourceText.From("class C { func M() { 1 + 2 } }\nclass D {}"), "/tmp/input.rvn")
            .AddAnalyzerReference(projectId, new AnalyzerReference(new FailingAnalyzer(phase)))
            .AddAnalyzerReference(projectId, new AnalyzerReference(new HealthyAnalyzer()));
        foreach (var reference in TestMetadataReferences.Default)
            solution = solution.AddMetadataReference(projectId, reference);
        workspace.TryApplyChanges(solution).ShouldBeTrue();
        var compilation = workspace.GetCompilation(projectId);
        var sink = new CollectingSink();
        var diagnostics = DocumentAnalyzerDriver.Run(workspace.CurrentSolution.GetProject(projectId)!, compilation.SyntaxTrees.Single(),
            compilation, null, sink, CancellationToken.None, includeCompilationActions: true);

        diagnostics.ShouldContain(diagnostic => diagnostic.Id == "AN9000");
        var failure = sink.Events.Single(item => item.Operation == "documentAnalyzer.failure");
        failure.ProjectName.ShouldBe("Failures");
        failure.DocumentPath.ShouldBe("/tmp/input.rvn");
        failure.Detail.ShouldContain(nameof(FailingAnalyzer));
        failure.Detail.ShouldContain("phase=" + phase);
        failure.Detail.ShouldContain(nameof(InvalidOperationException));
        failure.Detail.ShouldContain("Broken analyzer callback");
        sink.Events.Single(item => item.Operation == "documentAnalyzer.total").Detail.ShouldContain("outcome=completedWithFailures");
    }

    private sealed class CollectingSink : IWorkspaceEventSink
    {
        public List<WorkspaceEvent> Events { get; } = [];
        public void Report(WorkspaceEvent item) => Events.Add(item);
    }

    private sealed class HealthyAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create("AN9000", "Healthy", null, "", "Healthy analyzer ran", "Testing", DiagnosticSeverity.Warning);
        public override void Initialize(AnalysisContext context) => context.RegisterSyntaxTreeAction(action => action.ReportDiagnostic(Diagnostic.Create(Rule, Location.None)));
    }

    private sealed class FailingAnalyzer(string phase) : DiagnosticAnalyzer
    {
        private static void Fail() => throw new InvalidOperationException("Broken analyzer callback");
        public override void Initialize(AnalysisContext context)
        {
            switch (phase)
            {
                case "Initialize": Fail(); break;
                case "Compilation": context.RegisterCompilationAction(_ => Fail()); break;
                case "SyntaxTree": context.RegisterSyntaxTreeAction(_ => Fail()); break;
                case "Symbol": context.RegisterSymbolAction(_ => Fail(), SymbolKind.Type); break;
                case "SyntaxNode": context.RegisterSyntaxNodeAction(_ => Fail(), SyntaxKind.ClassDeclaration); break;
                case "Operation": context.RegisterOperationAction(_ => Fail(), OperationKind.ExpressionStatement); break;
            }
        }
    }
}
