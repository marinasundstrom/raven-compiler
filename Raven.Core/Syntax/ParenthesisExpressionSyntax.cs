namespace Raven.CodeAnalysis.Syntax;

public sealed class ParenthesisExpressionSyntax : ExpressionSyntax
{
    public ParenthesisExpressionSyntax(
        SyntaxToken openingParenToken, ExpressionSyntax expression, SyntaxToken closingParenToken)
    {
        OpeningParenToken = openingParenToken;
        Expression = expression;
        ClosingParenToken = closingParenToken;

        AttachChild(openingParenToken);
        AttachChild(expression);
        AttachChild(closingParenToken);

        _internalNode = new InternalSyntax.ParenthesisExpressionSyntax(
          openingParenToken.InternalSyntax,
          Expression.InternalSyntax,
          ClosingParenToken.InternalSyntax
        );
    }

    public SyntaxToken OpeningParenToken { get; }

    public ExpressionSyntax Expression { get; }

    public SyntaxToken ClosingParenToken { get; }

    internal ParenthesisExpressionSyntax(InternalSyntax.ParenthesisExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.ParenthesisExpressionSyntax InternalSyntax => (InternalSyntax.ParenthesisExpressionSyntax)_internalNode;
}
