namespace Raven.CodeAnalysis;

/// <summary>Base type for providers that register code actions for diagnostics.</summary>
public abstract class CodeFixProvider
{
    public abstract IEnumerable<string> FixableDiagnosticIds { get; }

    public virtual FixAllProvider? GetFixAllProvider() => null;

    public abstract void RegisterCodeFixes(CodeFixContext context);
}
