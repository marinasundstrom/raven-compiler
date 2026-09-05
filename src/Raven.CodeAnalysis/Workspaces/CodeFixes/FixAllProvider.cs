namespace Raven.CodeAnalysis;

/// <summary>Computes one code action that applies an equivalent fix across a broader scope.</summary>
public abstract class FixAllProvider
{
    public virtual IEnumerable<FixAllScope> GetSupportedFixAllScopes()
        => [FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution];

    public virtual IEnumerable<string> GetSupportedFixAllDiagnosticIds(CodeFixProvider originalCodeFixProvider)
    {
        if (originalCodeFixProvider is null)
            throw new ArgumentNullException(nameof(originalCodeFixProvider));

        return originalCodeFixProvider.FixableDiagnosticIds;
    }

    public abstract Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext);
}
