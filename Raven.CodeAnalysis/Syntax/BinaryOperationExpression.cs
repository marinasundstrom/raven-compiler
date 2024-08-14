namespace Raven.CodeAnalysis.Syntax;

public sealed class BinaryOperationExpression : ExpressionSyntax
{
    public BinaryOperationExpression(
        ExpressionSyntax leftHandSide, SyntaxToken operatorToken, ExpressionSyntax eightHandSide)
    {
        LeftHandSide = leftHandSide;
        OperatorToken = operatorToken;
        RightHandSide = eightHandSide;

        AttachChild(leftHandSide);
        AttachChild(operatorToken);
        AttachChild(eightHandSide);

        _internalNode = new InternalSyntax.BinaryOperationExpression(
            leftHandSide.InternalSyntax,
            operatorToken.InternalSyntax,
            eightHandSide.InternalSyntax
        );
    }

    public ExpressionSyntax LeftHandSide { get; }

    public SyntaxToken OperatorToken { get; }

    public ExpressionSyntax RightHandSide { get; }

    internal BinaryOperationExpression(InternalSyntax.BinaryOperationExpression internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.BinaryOperationExpression InternalSyntax => (InternalSyntax.BinaryOperationExpression)_internalNode;
}