namespace Raven.CodeAnalysis.Syntax;

public sealed class NumberExpressionSyntax : ExpressionSyntax
{
    public NumberExpressionSyntax(SyntaxToken token)
    {
        Token = token;

        AttachChild(token);

        _internalNode = new InternalSyntax.NumberExpressionSyntax(
            token.InternalSyntax
        );
    }

    public SyntaxToken Token { get; }

    internal NumberExpressionSyntax(InternalSyntax.NumberExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.NumberExpressionSyntax InternalSyntax => (InternalSyntax.NumberExpressionSyntax)_internalNode;
}