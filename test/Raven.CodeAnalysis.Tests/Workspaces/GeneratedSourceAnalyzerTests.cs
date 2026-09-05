using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class GeneratedSourceAnalyzerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProjectAnalysis_IncludesGeneratedTreesAndHonorsTheirSuppression(bool authoredDocument, bool reportSuppressed)
    {
        var workspace = CreateWorkspace(authoredDocument, new TreeAnalyzer(), out var projectId);
        var options = new CompilationWithAnalyzersOptions(reportSuppressedDiagnostics: reportSuppressed);
        var expected = workspace.GetDiagnostics(projectId, options).Where(IsTestDiagnostic).ToArray();
        expected.Length.ShouldBe((authoredDocument ? 1 : 0) + (reportSuppressed ? 2 : 1));

        var actual = workspace.GetProjectAnalyzerDiagnostics(projectId, options).Where(IsTestDiagnostic).ToArray();
        actual.Select(Key).ShouldBe(expected.Select(Key));
        actual.Single(diagnostic => diagnostic.Location.SourceTree!.FilePath.EndsWith("Visible.rvn")).IsSuppressed.ShouldBeFalse();
        if (reportSuppressed)
            actual.Single(diagnostic => diagnostic.Location.SourceTree!.FilePath.EndsWith("Suppressed.rvn")).IsSuppressed.ShouldBeTrue();
    }

    [Fact]
    public void GeneratedTreeFailure_IsRetriedBeforeProjectResultIsCached()
    {
        var analyzer = new TreeAnalyzer { FailGenerated = true };
        var workspace = CreateWorkspace(true, analyzer, out var projectId);
        var compilation = workspace.GetCompilation(projectId);
        workspace.GetProjectAnalyzerResult(projectId, compilation).Succeeded.ShouldBeFalse();
        analyzer.FailGenerated = false;
        var recovered = workspace.GetProjectAnalyzerResult(projectId, compilation);
        recovered.Succeeded.ShouldBeTrue();
        recovered.Diagnostics.Count(IsTestDiagnostic).ShouldBe(2);
        var calls = analyzer.Calls;
        workspace.GetProjectAnalyzerResult(projectId, compilation).Succeeded.ShouldBeTrue();
        analyzer.Calls.ShouldBe(calls);
    }

    private static bool IsTestDiagnostic(Diagnostic diagnostic) => diagnostic.Id == "AN9040";
    private static string Key(Diagnostic diagnostic) => $"{diagnostic.Location.SourceTree!.FilePath}:{diagnostic.IsSuppressed}";

    private static AdhocWorkspace CreateWorkspace(bool authoredDocument, TreeAnalyzer analyzer, out ProjectId projectId)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject("GeneratedAnalysis");
        projectId = solution.Projects.Single().Id;
        solution = solution.AddGeneratorReference(projectId, new GeneratorReference(new TestGenerator()))
            .AddAnalyzerReference(projectId, new AnalyzerReference(analyzer));
        if (authoredDocument)
            solution = solution.AddDocument(DocumentId.CreateNew(projectId), "Input.rvn", SourceText.From("class Input {}"), "/input/Input.rvn");
        workspace.TryApplyChanges(solution).ShouldBeTrue();
        return workspace;
    }

    private sealed class TestGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context) { }
        public void Execute(GeneratorExecutionContext context)
        {
            context.AddSource("Visible.rvn", "class Visible {}");
            context.AddSource("Suppressed.rvn", "#pragma warning disable AN9040\nclass Suppressed {}");
        }
    }

    private sealed class TreeAnalyzer : DiagnosticAnalyzer
    {
        public bool FailGenerated { get; set; }
        public int Calls { get; private set; }
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create(
            "AN9040", "Tree", null, "", "Tree", "Testing", DiagnosticSeverity.Warning);
        public override void Initialize(AnalysisContext context)
            => context.RegisterSyntaxTreeAction(action =>
            {
                Calls++;
                if (FailGenerated && action.SyntaxTree.FilePath.EndsWith("Visible.rvn"))
                    throw new InvalidOperationException("Generated tree analysis failed");
                var start = action.SyntaxTree.GetText().ToString().IndexOf("class", StringComparison.Ordinal);
                action.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(action.SyntaxTree, new TextSpan(start, 5))));
            });
    }
}
