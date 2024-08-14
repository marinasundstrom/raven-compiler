namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class BooleanFalseExpressionSyntax : BooleanExpressionSyntax
{
    public BooleanFalseExpressionSyntax(SyntaxToken falseKeyword)
    {
        FalseKeyword = AddChild(falseKeyword);
    }

    public override SyntaxKind Kind => SyntaxKind.BooleanFalseExpression;

    public SyntaxToken FalseKeyword { get; }

    public override string ToFullString()
    {
        return FalseKeyword.ToFullString();
    }
}