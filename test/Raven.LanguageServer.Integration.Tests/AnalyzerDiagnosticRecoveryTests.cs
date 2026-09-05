using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Text;
using Raven.LanguageServer;

using CodeDiagnostic = Raven.CodeAnalysis.Diagnostic;

namespace Raven.LanguageServer.Integration.Tests;

public sealed class AnalyzerDiagnosticRecoveryTests
{
    [Theory]
    [InlineData("callback", false)]
    [InlineData("initialize", false)]
    [InlineData("cancel", false)]
    [InlineData("callback", true)]
    [InlineData("initialize", true)]
    [InlineData("cancel", true)]
    [InlineData("compilation", true)]
    public async Task FailedRun_PreservesPublishedWarningsUntilSuccessfulRetry(string failure, bool projectLane)
    {
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams());
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var dispatcher = new LanguageServerDispatcher(store, NullLogger<LanguageServerDispatcher>.Instance);
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "recovery-" + Guid.NewGuid().ToString("N"), "input.rvn"));
        var document = await store.UpsertDocumentAsync(uri, "class C {}");
        var analyzer = new ControlledAnalyzer { UseCompilationAction = failure == "compilation" };
        workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, new AnalyzerReference(analyzer))).ShouldBeTrue();
        var lane = projectLane ? DocumentStore.DiagnosticLane.ProjectWithAnalyzers : DocumentStore.DiagnosticLane.DocumentWithAnalyzers;
        var initial = await store.TryGetDiagnosticsAsync(uri, lane, null, CancellationToken.None);
        initial.WasSkipped.ShouldBeFalse();
        initial.Diagnostics.ShouldContain(diagnostic => diagnostic.Code.HasValue && diagnostic.Code.Value.String == "AN9020");
        var publications = new List<IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic>>();
        void Publish(DocumentStore.DiagnosticsComputationResult result, DocumentStore.DiagnosticLane resultLane, int version)
            => dispatcher.PublishDiagnosticsInOrder(uri, resultLane, result.Diagnostics, version, result.SnapshotKey, result.SourceText,
                (diagnostics, _) => publications.Add(diagnostics));
        Publish(initial, lane, 1);

        await store.UpsertDocumentAsync(uri, "class C { }");
        if (failure == "initialize")
        {
            analyzer.ReportWarning = false;
            analyzer = new ControlledAnalyzer { Failure = failure };
            workspace.TryApplyChanges(workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, new AnalyzerReference(analyzer))).ShouldBeTrue();
        }
        else
            analyzer.Failure = failure;
        var failed = await store.TryGetDiagnosticsAsync(uri, lane, null, CancellationToken.None);
        failed.WasSkipped.ShouldBeTrue();

        var compiler = await store.TryGetDiagnosticsAsync(uri, DocumentStore.DiagnosticLane.DocumentCompiler, null, CancellationToken.None);
        compiler.WasSkipped.ShouldBeFalse();
        Publish(compiler, DocumentStore.DiagnosticLane.DocumentCompiler, 2);
        publications.Last().ShouldContain(diagnostic => diagnostic.Code.HasValue && diagnostic.Code.Value.String == "AN9020");
        publications.Last().ShouldNotContain(diagnostic => diagnostic.Code.HasValue && diagnostic.Code.Value.String == "AN9021");

        analyzer.Failure = null;
        analyzer.ReportWarning = false;
        var recovered = await store.TryGetDiagnosticsAsync(uri, lane, null, CancellationToken.None);
        recovered.WasSkipped.ShouldBeFalse();
        recovered.Diagnostics.ShouldBeEmpty();
        Publish(recovered, lane, 2);
        publications.Last().ShouldBeEmpty();
        var calls = analyzer.Calls;
        var cached = await store.TryGetDiagnosticsAsync(uri, lane, null, CancellationToken.None);
        cached.WasSkipped.ShouldBeFalse();
        analyzer.Calls.ShouldBe(calls);
    }

    private sealed class ControlledAnalyzer : DiagnosticAnalyzer
    {
        public string? Failure { get; set; }
        public bool ReportWarning { get; set; } = true;
        public bool UseCompilationAction { get; set; }
        public int Calls { get; private set; }
        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create("AN9020", "Existing warning", null, "", "Existing warning", "Testing", Raven.CodeAnalysis.DiagnosticSeverity.Warning);
        private static readonly DiagnosticDescriptor Partial = DiagnosticDescriptor.Create("AN9021", "Partial", null, "", "Partial", "Testing", Raven.CodeAnalysis.DiagnosticSeverity.Warning);
        public override void Initialize(AnalysisContext context)
        {
            if (Failure == "initialize")
                throw new InvalidOperationException("Transient initialization failure");
            context.RegisterCompilationAction(action =>
            {
                if (Failure == "compilation")
                {
                    action.ReportDiagnostic(CodeDiagnostic.Create(Partial, Raven.CodeAnalysis.Location.None));
                    throw new InvalidOperationException("Transient compilation callback failure");
                }
                if (UseCompilationAction && ReportWarning)
                {
                    var tree = action.Compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("input.rvn"));
                    action.ReportDiagnostic(CodeDiagnostic.Create(Rule, Raven.CodeAnalysis.Location.Create(tree, new TextSpan(6, 1))));
                }
            });
            context.RegisterSyntaxTreeAction(action =>
            {
                Calls++;
                var location = Raven.CodeAnalysis.Location.Create(action.SyntaxTree, new TextSpan(6, 1));
                if (Failure is "callback" or "cancel")
                {
                    action.ReportDiagnostic(CodeDiagnostic.Create(Partial, location));
                    if (Failure == "cancel")
                        throw new OperationCanceledException();
                    throw new InvalidOperationException("Transient callback failure");
                }
                if (ReportWarning && !UseCompilationAction)
                    action.ReportDiagnostic(CodeDiagnostic.Create(Rule, location));
            });
        }
    }
}
