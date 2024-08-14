namespace Raven.CodeAnalysis.Syntax;

public sealed class IfElseExpressionSyntax : ExpressionSyntax
{
    public IfElseExpressionSyntax(
        SyntaxToken ifKeyword, ExpressionSyntax conditionalExpression, SyntaxToken thenKeyword,
        ExpressionSyntax thenExpression, SyntaxToken? elseKeyword = null, ExpressionSyntax? elseExpression = null)
    {
        IfKeyword = ifKeyword;
        ConditionalExpression = conditionalExpression;
        ThenKeyword = thenKeyword;
        ThenExpression = thenExpression;
        ElseKeyword = elseKeyword;
        ElseExpression = elseExpression;

        AttachChild(ifKeyword);
        AttachChild(conditionalExpression);
        AttachChild(thenKeyword);

        if (elseKeyword is not null)
        {
            AttachChild(elseKeyword);
        }

        if (elseExpression is not null)
        {
            AttachChild(elseExpression);
        }

        _internalNode = new InternalSyntax.IfElseExpressionSyntax(
            ifKeyword.InternalSyntax,
            ConditionalExpression.InternalSyntax,
            ThenKeyword.InternalSyntax,
            ThenExpression.InternalSyntax,
            ElseKeyword?.InternalSyntax,
            ElseExpression?.InternalSyntax
        );
    }

    public SyntaxToken IfKeyword { get; }

    public ExpressionSyntax ConditionalExpression { get; }

    public SyntaxToken ThenKeyword { get; }

    public ExpressionSyntax ThenExpression { get; }

    public SyntaxToken? ElseKeyword { get; }

    public ExpressionSyntax? ElseExpression { get; }

    internal IfElseExpressionSyntax(InternalSyntax.IfElseExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.IfElseExpressionSyntax InternalSyntax => (InternalSyntax.IfElseExpressionSyntax)_internalNode;
}