namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class BinaryOperationExpression : ExpressionSyntax
{
    public BinaryOperationExpression(
        ExpressionSyntax leftHandSide, SyntaxToken operatorToken, ExpressionSyntax eightHandSide)
    {
        LeftHandSide = AddChild(leftHandSide);
        OperatorToken = AddChild(operatorToken);
        RightHandSide = AddChild(eightHandSide);
    }

    public ExpressionSyntax LeftHandSide { get; }

    public SyntaxToken OperatorToken { get; }

    public ExpressionSyntax RightHandSide { get; }
}
