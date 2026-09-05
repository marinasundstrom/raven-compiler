using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis;

/// <summary>Provides reusable Fix All implementations for code-fix providers.</summary>
public static class WellKnownFixAllProviders
{
    public static FixAllProvider BatchFixer { get; } = new BatchFixAllProvider();

    private sealed class BatchFixAllProvider : FixAllProvider
    {
        public override Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            if (fixAllContext is null)
                throw new ArgumentNullException(nameof(fixAllContext));

            if (!fixAllContext.GetDiagnosticEntries().Any())
                return Task.FromResult<CodeAction?>(null);

            var title = fixAllContext.Scope switch
            {
                FixAllScope.Document => "Fix all in document",
                FixAllScope.Project => "Fix all in project",
                FixAllScope.Solution => "Fix all in solution",
                _ => throw new ArgumentOutOfRangeException(nameof(fixAllContext))
            };

            return Task.FromResult<CodeAction?>(CodeAction.Create(
                title,
                (solution, cancellationToken) => ApplyFixes(solution, fixAllContext, cancellationToken),
                fixAllContext.CodeActionEquivalenceKey));
        }

        private static Solution ApplyFixes(
            Solution solution,
            FixAllContext fixAllContext,
            CancellationToken cancellationToken)
        {
            var acceptedChanges = new Dictionary<DocumentId, List<TextChange>>();
            foreach (var (contextDocument, diagnostic) in fixAllContext.GetDiagnosticEntries()
                         .OrderBy(static entry => entry.Document.Id.ProjectId.ToString(), StringComparer.Ordinal)
                         .ThenBy(static entry => entry.Document.Id.ToString(), StringComparer.Ordinal)
                         .ThenByDescending(static entry => entry.Diagnostic.Location.SourceSpan.Start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = solution.GetDocument(contextDocument.Id);
                if (document is null)
                    continue;

                var projectDiagnostics = fixAllContext
                    .GetProjectDiagnosticsAsync(contextDocument.Project)
                    .GetAwaiter()
                    .GetResult();
                var actions = new List<CodeAction>();
                fixAllContext.CodeFixProvider.RegisterCodeFixes(new CodeFixContext(
                    document,
                    diagnostic,
                    projectDiagnostics,
                    actions.Add,
                    cancellationToken));
                var action = actions.FirstOrDefault(candidate => string.Equals(
                    candidate.EquivalenceKey,
                    fixAllContext.CodeActionEquivalenceKey,
                    StringComparison.Ordinal));
                if (action is null)
                    continue;

                var updatedSolution = action.GetChangedSolution(solution, cancellationToken);
                var actionChanges = GetTextChanges(solution, updatedSolution, cancellationToken);
                if (actionChanges.Count == 0 || HasConflict(acceptedChanges, actionChanges))
                    continue;

                foreach (var (documentId, changes) in actionChanges)
                {
                    if (!acceptedChanges.TryGetValue(documentId, out var accepted))
                    {
                        accepted = [];
                        acceptedChanges.Add(documentId, accepted);
                    }

                    accepted.AddRange(changes);
                }
            }

            foreach (var (documentId, changes) in acceptedChanges)
            {
                var document = solution.GetDocument(documentId);
                if (document is null)
                    continue;

                var text = document.Text;
                foreach (var change in changes.OrderByDescending(static change => change.Span.Start))
                    text = text.WithChange(change);
                solution = solution.WithDocumentText(documentId, text);
            }

            return solution;
        }

        private static Dictionary<DocumentId, IReadOnlyList<TextChange>> GetTextChanges(
            Solution original,
            Solution updated,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<DocumentId, IReadOnlyList<TextChange>>();
            foreach (var project in original.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var updatedDocument = updated.GetDocument(document.Id);
                    if (updatedDocument is null || updatedDocument.Version == document.Version)
                        continue;

                    var changes = updatedDocument.GetTextChangesAsync(document, cancellationToken).GetAwaiter().GetResult();
                    if (changes.Count > 0)
                        result.Add(document.Id, changes);
                }
            }

            return result;
        }

        private static bool HasConflict(
            IReadOnlyDictionary<DocumentId, List<TextChange>> acceptedChanges,
            IReadOnlyDictionary<DocumentId, IReadOnlyList<TextChange>> actionChanges)
        {
            foreach (var (documentId, changes) in actionChanges)
            {
                if (!acceptedChanges.TryGetValue(documentId, out var accepted))
                    continue;

                if (changes.Any(change => accepted.Any(existing => SpansConflict(change.Span, existing.Span))))
                    return true;
            }

            return false;
        }

        private static bool SpansConflict(TextSpan left, TextSpan right)
            => left.Start == right.Start ||
               (left.Start < right.End && right.Start < left.End);
    }
}
