namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class IfElseExpressionSyntax : ExpressionSyntax
{
    public IfElseExpressionSyntax(
        SyntaxToken ifKeyword, ExpressionSyntax conditionalExpression, SyntaxToken
        thenKeyword, ExpressionSyntax thenExpression, SyntaxToken? elseKeyword = null, ExpressionSyntax? elseExpression = null)
    {
        IfKeyword = AddChild(0, ifKeyword);
        ConditionalExpression = AddChild(1, conditionalExpression);
        ThenKeyword = AddChild(2, thenKeyword);
        ThenExpression = AddChild(3, thenExpression);

        if (elseKeyword is not null)
        {
            ElseKeyword = AddChild(4, elseKeyword!);
        }

        if (elseExpression is not null)
        {
            ElseExpression = AddChild(5, elseExpression!);
        }
    }

    public override SyntaxKind Kind => SyntaxKind.IfElseExpression;

    public SyntaxToken IfKeyword { get; }

    public ExpressionSyntax ConditionalExpression { get; }

    public SyntaxToken ThenKeyword { get; }

    public ExpressionSyntax ThenExpression { get; }

    public SyntaxToken? ElseKeyword { get; }

    public ExpressionSyntax? ElseExpression { get; }
}