using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class AnalyzerConfigurationRecoveryTests
{
    [Theory]
    [InlineData("publicProject")]
    [InlineData("project")]
    [InlineData("document")]
    [InlineData("documentSnapshot")]
    public void ConfigurationChanges_ReplaceCachedDiagnosticsWithoutSourceEdits(string scope)
    {
        var workspace = new AdhocWorkspace();
        var options = new CompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var solution = workspace.CurrentSolution.AddProject("Configuration", compilationOptions: options);
        var projectId = solution.Projects.Single().Id;
        var documentId = DocumentId.CreateNew(projectId);
        var analyzer = new ConfigurationAnalyzer();
        solution = solution.AddDocument(documentId, "input.rvn", SourceText.From("class C {}"))
            .AddAnalyzerReference(projectId, new AnalyzerReference(analyzer));
        workspace.TryApplyChanges(solution).ShouldBeTrue();
        var originalDocumentVersion = workspace.CurrentSolution.GetDocument(documentId)!.Version;

        Diagnostic[] Analyze(bool reportSuppressed = false)
        {
            var analyzerOptions = reportSuppressed ? new CompilationWithAnalyzersOptions(reportSuppressedDiagnostics: true) : null;
            return (scope switch
            {
                "publicProject" => workspace.GetDiagnostics(projectId, analyzerOptions),
                "project" => workspace.GetProjectAnalyzerDiagnostics(projectId, analyzerOptions),
                "documentSnapshot" => workspace.GetDocumentAnalyzerDiagnostics(
                    workspace.CurrentSolution.GetDocument(documentId)!, workspace.GetCompilation(projectId), analyzerOptions),
                _ => workspace.GetDocumentAnalyzerDiagnostics(projectId, documentId, analyzerOptions)
            }).Where(diagnostic => diagnostic.Id == "AN9030").ToArray();
        }

        void Configure(CompilationOptions updated)
            => workspace.TryApplyChanges(workspace.CurrentSolution.WithCompilationOptions(projectId, updated)).ShouldBeTrue();

        var original = Analyze().Single();
        original.Severity.ShouldBe(DiagnosticSeverity.Warning);
        Analyze().Single().Severity.ShouldBe(DiagnosticSeverity.Warning);
        analyzer.Calls.ShouldBe(1);

        Configure(options.WithSpecificDiagnosticOption("AN9030", ReportDiagnostic.Error));
        Analyze().Single().Severity.ShouldBe(DiagnosticSeverity.Error);
        original.Severity.ShouldBe(DiagnosticSeverity.Warning);

        Configure(options.WithSpecificDiagnosticOption("AN9030", ReportDiagnostic.Suppress));
        Analyze().ShouldBeEmpty();
        Analyze(reportSuppressed: true).Single().IsSuppressed.ShouldBeTrue();
        Analyze().ShouldBeEmpty();

        Configure(options.WithDisabledAnalyzers([nameof(ConfigurationAnalyzer)]));
        Analyze().ShouldBeEmpty();
        var disabledCalls = analyzer.Calls;
        Analyze(reportSuppressed: true).ShouldBeEmpty();
        analyzer.Calls.ShouldBe(disabledCalls);

        Configure(options);
        Analyze().Single().IsSuppressed.ShouldBeFalse();
        Analyze().Single().Severity.ShouldBe(DiagnosticSeverity.Warning);

        Configure(options.WithRunAnalyzers(false));
        Analyze().ShouldBeEmpty();
        Configure(options);
        Analyze().Single().Severity.ShouldBe(DiagnosticSeverity.Warning);
        workspace.CurrentSolution.GetDocument(documentId)!.Version.ShouldBe(originalDocumentVersion);
    }

    private sealed class ConfigurationAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create(
            "AN9030", "Configuration", null, "", "Configuration", "Testing", DiagnosticSeverity.Warning);
        public int Calls { get; private set; }

        public override void Initialize(AnalysisContext context)
            => context.RegisterSyntaxTreeAction(action =>
            {
                Calls++;
                action.ReportDiagnostic(Diagnostic.Create(Rule, Location.None));
            });
    }
}
