using Raven.CodeAnalysis.Text;

namespace Raven.CodeAnalysis;

/// <summary>Represents a single code change operation.</summary>
public sealed class CodeAction
{
    private readonly Func<Solution, CancellationToken, Solution> _apply;

    private CodeAction(string title, Func<Solution, CancellationToken, Solution> apply, string? equivalenceKey)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        EquivalenceKey = equivalenceKey;
    }

    public string Title { get; }

    public string? EquivalenceKey { get; }

    public Solution GetChangedSolution(Solution solution, CancellationToken cancellationToken = default)
    {
        if (solution is null)
            throw new ArgumentNullException(nameof(solution));

        return _apply(solution, cancellationToken);
    }

    public static CodeAction Create(string title, Func<Solution, CancellationToken, Solution> apply)
        => new(title, apply, equivalenceKey: null);

    public static CodeAction Create(
        string title,
        Func<Solution, CancellationToken, Solution> apply,
        string? equivalenceKey)
        => new(title, apply, equivalenceKey);

    public static CodeAction CreateTextChange(string title, DocumentId documentId, TextChange change)
        => CreateTextChange(title, documentId, change, equivalenceKey: null);

    public static CodeAction CreateTextChange(
        string title,
        DocumentId documentId,
        TextChange change,
        string? equivalenceKey)
    {
        return new CodeAction(
            title,
            (solution, _) =>
            {
                var document = solution.GetDocument(documentId);
                if (document is null)
                    return solution;

                var updatedText = document.Text.WithChange(change);
                return solution.WithDocumentText(documentId, updatedText);
            },
            equivalenceKey);
    }
}
