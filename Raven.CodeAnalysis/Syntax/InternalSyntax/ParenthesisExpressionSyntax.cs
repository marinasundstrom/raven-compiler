namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class ParenthesisExpressionSyntax : ExpressionSyntax
{
    public ParenthesisExpressionSyntax(
        SyntaxToken openingParenToken, ExpressionSyntax expression, SyntaxToken closingParenToken)
    {
        OpeningParenToken = AddChild(openingParenToken);
        Expression = AddChild(expression);
        ClosingParenToken = AddChild(closingParenToken);
    }

    public override SyntaxKind Kind => SyntaxKind.ParenthesisExpression;

    public SyntaxToken OpeningParenToken { get; }

    public ExpressionSyntax Expression { get; }

    public SyntaxToken ClosingParenToken { get; }

    public override string ToFullString()
    {
        return OpeningParenToken.ToFullString() + Expression.ToFullString() + ClosingParenToken.ToFullString();
    }
}