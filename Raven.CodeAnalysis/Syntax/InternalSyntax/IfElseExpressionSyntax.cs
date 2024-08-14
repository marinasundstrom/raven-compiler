namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class IfElseExpressionSyntax : ExpressionSyntax
{
    public IfElseExpressionSyntax(
        SyntaxToken ifKeyword, ExpressionSyntax conditionalExpression, SyntaxToken
        thenKeyword, ExpressionSyntax thenExpression, SyntaxToken? elseKeyword = null, ExpressionSyntax? elseExpression = null)
    {
        IfKeyword = AddChild(ifKeyword);
        ConditionalExpression = AddChild(conditionalExpression);
        ThenKeyword = AddChild(thenKeyword);
        ThenExpression = AddChild(thenExpression);

        if (elseKeyword is not null)
        {
            ElseKeyword = AddChild(elseKeyword!);
        }

        if (elseExpression is not null)
        {
            ElseExpression = AddChild(elseExpression!);
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