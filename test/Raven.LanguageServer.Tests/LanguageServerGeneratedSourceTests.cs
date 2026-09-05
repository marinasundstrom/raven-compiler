using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Raven.CodeAnalysis;
using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Text;

using CodeDiagnostic = Raven.CodeAnalysis.Diagnostic;
using CodeDiagnosticSeverity = Raven.CodeAnalysis.DiagnosticSeverity;
using CodeLocation = Raven.CodeAnalysis.Location;

namespace Raven.LanguageServer.Tests;

public sealed class LanguageServerGeneratedSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definition_OpensLiveGeneratedSourceAndNavigatesBack(bool emitFiles)
    {
        const string code = "class Home {}\nclass Consumer { func Create() -> Generated.Route? => null }";
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams());
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var root = Path.Combine(Path.GetTempPath(), "raven-generated-" + Guid.NewGuid().ToString("N"));
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(root, "Input.rvn"));
        var document = await store.UpsertDocumentAsync(uri, code);
        var solution = workspace.CurrentSolution.AddGeneratorReference(document.Project.Id, new GeneratorReference(new RouteGenerator()));
        if (emitFiles)
            solution = solution.WithCompilerGeneratedFilesOutputPath(document.Project.Id, Path.Combine(root, "obj", "generated"));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        var definition = new DefinitionHandler(store, NullLogger<DefinitionHandler>.Instance);
        var result = await definition.Handle(new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = At(code, "Route")
        }, CancellationToken.None);
        var link = result!.Single().LocationLink!;
        link.TargetUri.ToString().ShouldStartWith("raven-generated:");
        GeneratedSourceDocument.TryParse(link.TargetUri, out var origin, out var generatedPath).ShouldBeTrue(link.TargetUri.ToString());
        origin.ShouldBe(uri);
        var generatedContext = await store.GetAnalysisContextAsync(link.TargetUri, CancellationToken.None);
        generatedContext.ShouldNotBeNull(link.TargetUri + " path=" + generatedPath);
        var contentHandler = new GeneratedSourceHandler(store);
        var generatedSource = await contentHandler.Handle(new GeneratedSourceParams { Uri = link.TargetUri }, CancellationToken.None);
        generatedSource.ShouldNotBeNull();
        var content = generatedSource.Text;
        content.ShouldContain("class Route");
        Directory.Exists(root).ShouldBeFalse();

        var hover = await new HoverHandler(store, NullLogger<HoverHandler>.Instance).Handle(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(link.TargetUri),
            Position = At(content, "Home")
        }, CancellationToken.None);
        hover.ShouldNotBeNull();
        hover.Contents.MarkupContent!.Value.ShouldContain("Home");
        var back = await definition.Handle(new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(link.TargetUri),
            Position = At(content, "Home")
        }, CancellationToken.None);
        back!.Single().LocationLink!.TargetUri.ShouldBe(uri);

        await store.UpsertDocumentAsync(uri, code + "\nclass Added {}");
        var updated = await contentHandler.Handle(new GeneratedSourceParams { Uri = link.TargetUri }, CancellationToken.None);
        updated.ShouldNotBeNull();
        updated.Text.ShouldNotBe(content);
        updated.Text.ShouldContain("// types: 3");
    }

    [Fact]
    public async Task Diagnostics_ReportsAnalyzerDiagnosticsForGeneratedSource()
    {
        const string code = "class Home {}\nclass Consumer { func Create() -> Generated.Route? => null }";
        var workspace = RavenWorkspace.Create(targetFramework: "net10.0");
        var manager = new WorkspaceManager(workspace, NullLogger<WorkspaceManager>.Instance);
        manager.Initialize(new InitializeParams());
        var store = new DocumentStore(manager, NullLogger<DocumentStore>.Instance);
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "Input.rvn"));
        var document = await store.UpsertDocumentAsync(uri, code);
        var solution = workspace.CurrentSolution
            .AddGeneratorReference(document.Project.Id, new GeneratorReference(new RouteGenerator()))
            .AddAnalyzerReference(document.Project.Id, new AnalyzerReference(new GeneratedTreeAnalyzer()));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        var definition = new DefinitionHandler(store, NullLogger<DefinitionHandler>.Instance);
        var result = await definition.Handle(new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = At(code, "Route")
        }, CancellationToken.None);
        var generatedUri = result!.Single().LocationLink!.TargetUri;

        var generatedSource = await new GeneratedSourceHandler(store).Handle(
            new GeneratedSourceParams { Uri = generatedUri },
            CancellationToken.None);

        generatedSource.ShouldNotBeNull();
        var diagnostic = generatedSource.Diagnostics.Single(diagnostic => diagnostic.Code?.String == GeneratedTreeAnalyzer.DiagnosticId);
        diagnostic.Source.ShouldBe("raven-analyzer");
        diagnostic.Range.Start.ShouldBe(new Position(1, 0));
    }

    private static Position At(string text, string name) =>
        PositionHelper.ToRange(SourceText.From(text), new TextSpan(text.IndexOf(name, StringComparison.Ordinal) + 1, 0)).Start;

    private sealed class RouteGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
        {
            var count = context.Compilation.SyntaxTrees.Sum(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Count());
            context.AddSource("Route", $"namespace Generated\nclass Route {{ func Back() -> Home? => null }}\n// types: {count}");
        }
    }

    private sealed class GeneratedTreeAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ANLSPGEN001";

        private static readonly DiagnosticDescriptor Rule = DiagnosticDescriptor.Create(
            DiagnosticId,
            "Generated source",
            description: null,
            helpLinkUri: string.Empty,
            messageFormat: "Generated source diagnostic",
            category: "Testing",
            defaultSeverity: CodeDiagnosticSeverity.Warning);

        public override void Initialize(AnalysisContext context)
            => context.RegisterSyntaxTreeAction(action =>
            {
                if (!action.SyntaxTree.FilePath.EndsWith("Route.rvn", StringComparison.Ordinal))
                    return;

                var start = action.SyntaxTree.GetText().ToString().IndexOf("class", StringComparison.Ordinal);
                action.ReportDiagnostic(CodeDiagnostic.Create(Rule, CodeLocation.Create(action.SyntaxTree, new TextSpan(start, 5))));
            });
    }
}
