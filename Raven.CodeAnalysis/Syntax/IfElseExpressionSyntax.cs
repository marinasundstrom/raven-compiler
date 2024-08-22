namespace Raven.CodeAnalysis.Syntax;

public sealed class IfElseExpressionSyntax : ExpressionSyntax
{
    private SyntaxToken _ifKeyword;
    private ExpressionSyntax? _conditionalExpression;
    private SyntaxToken _thenKeyword;
    private ExpressionSyntax? _thenExpression;
    private SyntaxToken? _elseKeyword;
    private ExpressionSyntax? _elseExpression;

    public IfElseExpressionSyntax(
        SyntaxToken ifKeyword, ExpressionSyntax conditionalExpression, SyntaxToken thenKeyword,
        ExpressionSyntax thenExpression, SyntaxToken? elseKeyword = null, ExpressionSyntax? elseExpression = null)
    {
        _ifKeyword = ifKeyword;
        _conditionalExpression = conditionalExpression;
        _thenKeyword = thenKeyword;
        _thenExpression = thenExpression;
        _elseKeyword = elseKeyword;
        _elseExpression = elseExpression;

        AttachChild(0, ifKeyword);
        AttachChild(1, conditionalExpression);
        AttachChild(2, thenKeyword);

        if (elseKeyword is not null)
        {
            AttachChild(3, elseKeyword);
        }

        if (elseExpression is not null)
        {
            AttachChild(4, elseExpression);
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

    public SyntaxToken IfKeyword => GetOrCreateNode(0, InternalSyntax.IfKeyword, ref _ifKeyword)!;

    public ExpressionSyntax ConditionalExpression => GetOrCreateNode(0, InternalSyntax.ConditionalExpression, ref _conditionalExpression)!;

    public SyntaxToken ThenKeyword => GetOrCreateNode(0, InternalSyntax.ThenKeyword, ref _thenKeyword)!;

    public ExpressionSyntax ThenExpression => GetOrCreateNode(0, InternalSyntax.ThenExpression, ref _thenExpression)!;

    public SyntaxToken? ElseKeyword => GetOrCreateNode(0, InternalSyntax.ElseKeyword, ref _elseKeyword)!;

    public ExpressionSyntax? ElseExpression => GetOrCreateNode(0, InternalSyntax.ElseExpression, ref _elseExpression)!;

    internal IfElseExpressionSyntax(InternalSyntax.IfElseExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.IfElseExpressionSyntax InternalSyntax => (InternalSyntax.IfElseExpressionSyntax)_internalNode;
}