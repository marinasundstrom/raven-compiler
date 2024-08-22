namespace Raven.CodeAnalysis.Syntax;

internal interface IHasParent
{
    SyntaxNode? Parent { get; }

    void SetParent(SyntaxNode parent);
}