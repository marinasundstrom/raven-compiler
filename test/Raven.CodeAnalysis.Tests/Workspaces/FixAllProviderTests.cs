using Raven.CodeAnalysis.Diagnostics;
using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;
using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis.Tests.Workspaces;

public sealed class FixAllProviderTests
{
    [Fact]
    public void BuiltInCodeFixProviders_OptIntoBatchFixAll()
    {
        var providerType = typeof(CodeFixProvider);
        var builtInNamespace = typeof(VarCanBeLetCodeFixProvider).Namespace;
        var providers = providerType.Assembly.GetTypes()
            .Where(type =>
                type.Namespace == builtInNamespace &&
                !type.IsAbstract &&
                providerType.IsAssignableFrom(type))
            .Select(type => (CodeFixProvider)Activator.CreateInstance(type)!)
            .OrderBy(provider => provider.GetType().Name, StringComparer.Ordinal)
            .ToArray();

        providers.ShouldNotBeEmpty();
        foreach (var provider in providers)
            provider.GetFixAllProvider().ShouldBeSameAs(WellKnownFixAllProviders.BatchFixer);
    }

    [Theory]
    [InlineData(FixAllScope.Document, "let first = 1", "var second = 2")]
    [InlineData(FixAllScope.Project, "let first = 1", "let second = 2")]
    public void BatchFixer_AppliesEquivalentNonOverlappingChanges(
        FixAllScope scope,
        string expectedFirst,
        string expectedSecond)
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestTargetFramework.Default);
        var projectId = workspace.AddProject("FixAll");
        var firstDocumentId = DocumentId.CreateNew(projectId);
        var secondDocumentId = DocumentId.CreateNew(projectId);
        var solution = workspace.CurrentSolution
            .AddDocument(firstDocumentId, "First.rvn", SourceText.From("func First() -> int {\n    var first = 1\n    return first\n}"))
            .AddDocument(secondDocumentId, "Second.rvn", SourceText.From("func Second() -> int {\n    var second = 2\n    return second\n}"))
            .AddAnalyzerReference(projectId, new AnalyzerReference(new VarCanBeLetAnalyzer()));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        var provider = new VarCanBeLetCodeFixProvider();
        var triggerFix = workspace.GetCodeFixes(projectId, [provider])
            .Single(fix => fix.DocumentId == firstDocumentId);
        triggerFix.Action.EquivalenceKey.ShouldNotBeNullOrWhiteSpace();

        var fixAll = workspace.GetFixAll(
            projectId,
            provider,
            scope,
            triggerFix.Action.EquivalenceKey!,
            firstDocumentId);

        fixAll.ShouldNotBeNull();
        var updated = fixAll.GetChangedSolution(workspace.CurrentSolution);
        updated.GetDocument(firstDocumentId)!.Text.ToString().ShouldContain(expectedFirst);
        updated.GetDocument(secondDocumentId)!.Text.ToString().ShouldContain(expectedSecond);
    }

    [Fact]
    public void ProviderWithoutFixAllSupport_DoesNotOfferFixAll()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestTargetFramework.Default);
        var projectId = workspace.AddProject("NoFixAll");
        var documentId = DocumentId.CreateNew(projectId);
        var solution = workspace.CurrentSolution.AddDocument(
            documentId,
            "Input.rvn",
            SourceText.From("var value = 1"));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        workspace.GetFixAll(
            projectId,
            new NoFixAllProvider(),
            FixAllScope.Document,
            "NoFixAll",
            documentId).ShouldBeNull();
    }

    [Fact]
    public void BatchFixer_SolutionScopeAppliesEquivalentFixesAcrossProjects()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestTargetFramework.Default);
        var firstProjectId = workspace.AddProject("FirstProject");
        var secondProjectId = workspace.AddProject("SecondProject");
        var firstDocumentId = DocumentId.CreateNew(firstProjectId);
        var secondDocumentId = DocumentId.CreateNew(secondProjectId);
        var solution = workspace.CurrentSolution
            .AddDocument(firstDocumentId, "First.rvn", SourceText.From("func First() -> int {\n    var first = 1\n    return first\n}"))
            .AddDocument(secondDocumentId, "Second.rvn", SourceText.From("func Second() -> int {\n    var second = 2\n    return second\n}"))
            .AddAnalyzerReference(firstProjectId, new AnalyzerReference(new VarCanBeLetAnalyzer()))
            .AddAnalyzerReference(secondProjectId, new AnalyzerReference(new VarCanBeLetAnalyzer()));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        var provider = new VarCanBeLetCodeFixProvider();
        var triggerFix = workspace.GetCodeFixes(firstProjectId, [provider]).Single();
        var fixAll = workspace.GetFixAll(
            firstProjectId,
            provider,
            FixAllScope.Solution,
            triggerFix.Action.EquivalenceKey!,
            firstDocumentId);

        fixAll.ShouldNotBeNull();
        var updated = fixAll.GetChangedSolution(workspace.CurrentSolution);
        updated.GetDocument(firstDocumentId)!.Text.ToString().ShouldContain("let first = 1");
        updated.GetDocument(secondDocumentId)!.Text.ToString().ShouldContain("let second = 2");
    }

    [Fact]
    public void BatchFixer_AppliesOnlyActionsWithTheRequestedEquivalenceKey()
    {
        var workspace = RavenWorkspace.Create(targetFramework: TestTargetFramework.Default);
        var projectId = workspace.AddProject("EquivalenceKeys");
        var documentId = DocumentId.CreateNew(projectId);
        var solution = workspace.CurrentSolution
            .AddDocument(
                documentId,
                "Input.rvn",
                SourceText.From("func Sum() -> int {\n    var first = 1\n    var second = 2\n    return first + second\n}"))
            .AddAnalyzerReference(projectId, new AnalyzerReference(new VarCanBeLetAnalyzer()));
        workspace.TryApplyChanges(solution).ShouldBeTrue();

        var provider = new AlternativeVarCodeFixProvider();
        var triggerFix = workspace.GetCodeFixes(projectId, [provider])
            .First(fix => fix.Action.EquivalenceKey == AlternativeVarCodeFixProvider.UseValEquivalenceKey);
        var fixAll = workspace.GetFixAll(
            projectId,
            provider,
            FixAllScope.Document,
            triggerFix.Action.EquivalenceKey!,
            documentId);

        fixAll.ShouldNotBeNull();
        var updatedText = fixAll
            .GetChangedSolution(workspace.CurrentSolution)
            .GetDocument(documentId)!
            .Text
            .ToString();
        updatedText.ShouldContain("val first = 1");
        updatedText.ShouldContain("val second = 2");
        updatedText.ShouldNotContain("let first");
    }

    private sealed class NoFixAllProvider : CodeFixProvider
    {
        public override IEnumerable<string> FixableDiagnosticIds => ["RAV9004"];

        public override void RegisterCodeFixes(CodeFixContext context)
        {
        }
    }

    private sealed class AlternativeVarCodeFixProvider : CodeFixProvider
    {
        public const string UseValEquivalenceKey = nameof(AlternativeVarCodeFixProvider) + ".UseVal";
        private const string UseLetEquivalenceKey = nameof(AlternativeVarCodeFixProvider) + ".UseLet";

        public override IEnumerable<string> FixableDiagnosticIds => [VarCanBeLetAnalyzer.DiagnosticId];

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override void RegisterCodeFixes(CodeFixContext context)
        {
            var root = context.Document.GetSyntaxTreeAsync(context.CancellationToken)
                .GetAwaiter()
                .GetResult()?
                .GetRoot(context.CancellationToken);
            var declaration = root?
                .FindNode(context.Diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)?
                .FirstAncestorOrSelf<VariableDeclarationSyntax>();
            if (declaration is null || !declaration.BindingKeyword.IsKind(SyntaxKind.VarKeyword))
                return;

            context.RegisterCodeFix(CodeAction.CreateTextChange(
                "Use let",
                context.Document.Id,
                new TextChange(declaration.BindingKeyword.Span, "let"),
                UseLetEquivalenceKey));
            context.RegisterCodeFix(CodeAction.CreateTextChange(
                "Use val",
                context.Document.Id,
                new TextChange(declaration.BindingKeyword.Span, "val"),
                UseValEquivalenceKey));
        }
    }
}
