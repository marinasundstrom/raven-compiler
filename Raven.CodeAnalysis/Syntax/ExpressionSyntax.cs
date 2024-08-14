namespace Raven.CodeAnalysis.Syntax;

public abstract class ExpressionSyntax : SyntaxNode
{
    internal InternalSyntax.ExpressionSyntax InternalSyntax => (InternalSyntax.ExpressionSyntax)_internalNode;
}