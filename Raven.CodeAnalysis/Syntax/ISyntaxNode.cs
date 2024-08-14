namespace Raven.CodeAnalysis.Syntax;

public interface ISyntaxNode
{
    SyntaxKind Kind { get; }

    TextSpan Span { get; }

    TextSpan FullSpan { get; }

    SyntaxNode? Parent { get; }
}