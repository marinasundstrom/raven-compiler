using System.Collections.Immutable;

namespace Raven.CodeAnalysis;

/// <summary>Context supplied to a provider when computing a Fix All action.</summary>
public sealed class FixAllContext
{
    private readonly ImmutableDictionary<DocumentId, ImmutableArray<Diagnostic>> _diagnostics;

    internal FixAllContext(
        Solution solution,
        Project project,
        Document? document,
        CodeFixProvider codeFixProvider,
        FixAllScope scope,
        string codeActionEquivalenceKey,
        ImmutableDictionary<DocumentId, ImmutableArray<Diagnostic>> diagnostics,
        CancellationToken cancellationToken)
    {
        Solution = solution;
        Project = project;
        Document = document;
        CodeFixProvider = codeFixProvider;
        Scope = scope;
        CodeActionEquivalenceKey = codeActionEquivalenceKey;
        _diagnostics = diagnostics;
        CancellationToken = cancellationToken;
    }

    public Solution Solution { get; }

    public Project Project { get; }

    public Document? Document { get; }

    public CodeFixProvider CodeFixProvider { get; }

    public FixAllScope Scope { get; }

    public string CodeActionEquivalenceKey { get; }

    public CancellationToken CancellationToken { get; }

    public Task<ImmutableArray<Diagnostic>> GetDocumentDiagnosticsAsync(Document document)
    {
        if (document is null)
            throw new ArgumentNullException(nameof(document));

        return Task.FromResult(
            _diagnostics.TryGetValue(document.Id, out var diagnostics)
                ? diagnostics
                : ImmutableArray<Diagnostic>.Empty);
    }

    public Task<ImmutableArray<Diagnostic>> GetProjectDiagnosticsAsync(Project project)
    {
        if (project is null)
            throw new ArgumentNullException(nameof(project));

        return Task.FromResult(
            project.Documents
                .SelectMany(document => _diagnostics.TryGetValue(document.Id, out var diagnostics)
                    ? diagnostics
                    : [])
                .ToImmutableArray());
    }

    public Task<ImmutableArray<Diagnostic>> GetAllDiagnosticsAsync()
        => Task.FromResult(_diagnostics.Values.SelectMany(static diagnostics => diagnostics).ToImmutableArray());

    internal IEnumerable<(Document Document, Diagnostic Diagnostic)> GetDiagnosticEntries()
    {
        foreach (var (documentId, diagnostics) in _diagnostics)
        {
            var document = Solution.GetDocument(documentId);
            if (document is null)
                continue;

            foreach (var diagnostic in diagnostics)
                yield return (document, diagnostic);
        }
    }
}
