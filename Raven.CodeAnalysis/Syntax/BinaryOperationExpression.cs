namespace Raven.CodeAnalysis.Syntax;

public sealed class BinaryOperationExpression : ExpressionSyntax
{
    private ExpressionSyntax? _leftHandSide;
    private SyntaxToken _operatorToken;
    private ExpressionSyntax? _rightHandSide;

    public BinaryOperationExpression(
        ExpressionSyntax leftHandSide, SyntaxToken operatorToken, ExpressionSyntax eightHandSide)
    {
        _leftHandSide = leftHandSide;
        _operatorToken = operatorToken;
        _rightHandSide = eightHandSide;

        AttachChild(0, leftHandSide);
        AttachChild(1, operatorToken);
        AttachChild(2, eightHandSide);

        _internalNode = new InternalSyntax.BinaryOperationExpression(
            leftHandSide.InternalSyntax,
            operatorToken.InternalSyntax,
            eightHandSide.InternalSyntax
        );
    }

    public ExpressionSyntax LeftHandSide => GetOrCreateNode(0, InternalSyntax.LeftHandSide, ref _leftHandSide)!;

    public SyntaxToken OperatorToken => GetOrCreateNode(1, InternalSyntax.OperatorToken, ref _operatorToken)!;

    public ExpressionSyntax RightHandSide => GetOrCreateNode(2, InternalSyntax.RightHandSide, ref _rightHandSide)!;

    internal BinaryOperationExpression(InternalSyntax.BinaryOperationExpression internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.BinaryOperationExpression InternalSyntax => (InternalSyntax.BinaryOperationExpression)_internalNode;
}