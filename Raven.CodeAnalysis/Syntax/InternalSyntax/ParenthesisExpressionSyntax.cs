namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class ParenthesisExpressionSyntax : ExpressionSyntax
{
    public ParenthesisExpressionSyntax(
        SyntaxToken openingParenToken, ExpressionSyntax expression, SyntaxToken closingParenToken)
    {
        OpeningParenToken = AddChild(0, openingParenToken);
        Expression = AddChild(1, expression);
        ClosingParenToken = AddChild(2, closingParenToken);
    }

    public override SyntaxKind Kind => SyntaxKind.ParenthesisExpression;

    public SyntaxToken OpeningParenToken { get; }

    public ExpressionSyntax Expression { get; }

    public SyntaxToken ClosingParenToken { get; }
}