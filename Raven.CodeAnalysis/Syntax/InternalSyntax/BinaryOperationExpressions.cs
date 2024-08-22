namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class BinaryOperationExpression : ExpressionSyntax
{
    public BinaryOperationExpression(
        ExpressionSyntax leftHandSide, SyntaxToken operatorToken, ExpressionSyntax eightHandSide)
    {
        LeftHandSide = AddChild(0, leftHandSide);
        OperatorToken = AddChild(1, operatorToken);
        RightHandSide = AddChild(2, eightHandSide);
    }

    public ExpressionSyntax LeftHandSide { get; }

    public SyntaxToken OperatorToken { get; }

    public ExpressionSyntax RightHandSide { get; }
}