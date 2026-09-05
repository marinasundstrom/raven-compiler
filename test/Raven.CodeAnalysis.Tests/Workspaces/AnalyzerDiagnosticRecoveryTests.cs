using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class AnalyzerDiagnosticRecoveryTests
{
    [Theory]
    [InlineData("initialize", "documentId")]
    [InlineData("callback", "documentId")]
    [InlineData("cancel", "documentId")]
    [InlineData("initialize", "document")]
    [InlineData("callback", "document")]
    [InlineData("cancel", "document")]
    [InlineData("initialize", "project")]
    [InlineData("callback", "project")]
    [InlineData("cancel", "project")]
    [InlineData("initialize", "projectCompilation")]
    [InlineData("callback", "projectCompilation")]
    [InlineData("cancel", "projectCompilation")]
    public void IncompleteAnalysis_IsRetriedAndOnlySuccessfulResultsAreCached(string failure, string scope)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject("Recovery");
        var projectId = solution.Projects.Single().Id;
        var documentId = DocumentId.CreateNew(projectId);
        var analyzer = new RecoveringAnalyzer { Failure = failure, UseCompilationAction = scope == "projectCompilation" };
        solution = solution.AddDocument(documentId, "input.rvn", SourceText.From("class C {}"))
            .AddAnalyzerReference(projectId, new AnalyzerReference(analyzer));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        Diagnostic[] Analyze() => (scope switch
        {
            "document" => workspace.GetDocumentAnalyzerDiagnostics(workspace.CurrentSolution.GetDocument(documentId)!, workspace.GetCompilation(projectId)),
            "project" or "projectCompilation" => workspace.GetDiagnostics(projectId),
            _ => workspace.GetDocumentAnalyzerDiagnostics(projectId, documentId)
        }).Where(diagnostic => diagnostic.Id.StartsWith("AN", StringComparison.Ordinal)).ToArray();

        if (failure == "cancel")
            Should.Throw<OperationCanceledException>(() => Analyze());
        else
            Analyze();
        analyzer.Failure = null;
        Analyze().Single().Id.ShouldBe("AN9010");
        var calls = analyzer.Calls;
        Analyze().Single().Id.ShouldBe("AN9010");
        analyzer.Calls.ShouldBe(calls);

        workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(documentId, SourceText.From("class Changed {}"))).ShouldBeTrue();
        analyzer.Failure = "callback";
        Analyze();
        analyzer.Failure = null;
        Analyze().Single().Id.ShouldBe("AN9010");
        analyzer.Calls.ShouldBeGreaterThan(calls);
    }

    private sealed class RecoveringAnalyzer : DiagnosticAnalyzer
    {
        public string? Failure { get; set; }
        public bool UseCompilationAction { get; init; }
        public int Calls { get; private set; }
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create("AN9010", "Recovered", null, "", "Recovered", "Testing", DiagnosticSeverity.Warning);
        private static readonly DiagnosticDescriptor Partial = DiagnosticDescriptor.Create("AN9011", "Partial", null, "", "Partial", "Testing", DiagnosticSeverity.Warning);
        public override void Initialize(AnalysisContext context)
        {
            if (Failure == "initialize")
                throw new InvalidOperationException("Transient initialization failure");
            if (UseCompilationAction)
                context.RegisterCompilationAction(action => Analyze(action.ReportDiagnostic));
            else
                context.RegisterSyntaxTreeAction(action => Analyze(action.ReportDiagnostic));
        }

        private void Analyze(Action<Diagnostic> reportDiagnostic)
        {
            Calls++;
            if (Failure is "callback" or "cancel")
            {
                reportDiagnostic(Diagnostic.Create(Partial, Location.None));
                if (Failure == "cancel")
                    throw new OperationCanceledException();
                throw new InvalidOperationException("Transient callback failure");
            }
            reportDiagnostic(Diagnostic.Create(Rule, Location.None));
        }
    }
}
