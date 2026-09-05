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

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void GeneratedCodePolicy_ControlsCallbacksAndReportingIndependently(int flags, bool projectLane)
    {
        var analyzer = new PolicyAnalyzer((GeneratedCodeAnalysisFlags)flags);
        var workspace = CreateWorkspace(true, analyzer, out var projectId);
        var diagnostics = projectLane
            ? workspace.GetProjectAnalyzerDiagnostics(projectId)
            : workspace.GetDiagnostics(projectId);
        var analyzeGenerated = (flags & 1) != 0;
        var reportGenerated = (flags & 2) != 0;
        analyzer.TreeCalls.ShouldBe(analyzeGenerated ? 3 : 1);
        analyzer.CompilationCalls.ShouldBe(1);
        diagnostics.Count(diagnostic => diagnostic.Id == "AN9041").ShouldBe(
            1 + (reportGenerated ? 1 + (analyzeGenerated ? 2 : 0) : 0));
    }

    [Fact]
    public void FailedInitialization_DoesNotRetainGeneratedCodePolicy()
    {
        var analyzer = new PolicyAnalyzer(null) { FailInitialization = true };
        var workspace = CreateWorkspace(true, analyzer, out var projectId);
        workspace.GetDiagnostics(projectId);
        analyzer.FailInitialization = false;
        workspace.GetDiagnostics(projectId).Count(diagnostic => diagnostic.Id == "AN9041").ShouldBe(4);
        analyzer.TreeCalls.ShouldBe(3);
    }

    [Fact]
    public void GeneratedProvenance_SurvivesTextChangesAndDoesNotDependOnFileName()
    {
        var workspace = CreateWorkspace(false, new TreeAnalyzer(), out var projectId);
        var compilation = workspace.GetCompilation(projectId);
        var generated = compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("Visible.rvn"));
        var changed = generated.WithChangedText(SourceText.From("class Changed {}"));
        var authored = Raven.CodeAnalysis.Syntax.SyntaxTree.ParseText("class Authored {}", path: generated.FilePath);
        generated.IsGenerated.ShouldBeTrue();
        changed.IsGenerated.ShouldBeTrue();
        authored.IsGenerated.ShouldBeFalse();
    }

    private sealed class PolicyAnalyzer(GeneratedCodeAnalysisFlags? flags) : DiagnosticAnalyzer
    {
        public bool FailInitialization { get; set; }
        public int TreeCalls { get; private set; }
        public int CompilationCalls { get; private set; }
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create(
            "AN9041", "Policy", null, "", "Policy", "Testing", DiagnosticSeverity.Warning);
        public override void Initialize(AnalysisContext context)
        {
            if (FailInitialization)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                throw new InvalidOperationException("Initialization failed after configuration");
            }
            if (flags.HasValue)
                context.ConfigureGeneratedCodeAnalysis(flags.Value);
            context.RegisterCompilationAction(action =>
            {
                CompilationCalls++;
                var tree = action.Compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("Visible.rvn"));
                action.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(tree, new TextSpan(1, 1))));
            });
            context.RegisterSyntaxTreeAction(action =>
            {
                TreeCalls++;
                action.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(action.SyntaxTree, new TextSpan(0, 1))));
            });
        }
    }

    private static bool IsTestDiagnostic(Diagnostic diagnostic) => diagnostic.Id == "AN9040";
    private static string Key(Diagnostic diagnostic) => $"{diagnostic.Location.SourceTree!.FilePath}:{diagnostic.IsSuppressed}";

    private static AdhocWorkspace CreateWorkspace(bool authoredDocument, DiagnosticAnalyzer analyzer, out ProjectId projectId)
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
