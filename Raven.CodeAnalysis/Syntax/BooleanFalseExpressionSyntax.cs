namespace Raven.CodeAnalysis.Syntax;

public sealed class BooleanFalseExpressionSyntax : BooleanExpressionSyntax
{
    public BooleanFalseExpressionSyntax(SyntaxToken falseKeyword)
    {
        FalseKeyword = falseKeyword;

        AttachChild(falseKeyword);

        _internalNode = new InternalSyntax.BooleanFalseExpressionSyntax(
            falseKeyword.InternalSyntax
        );
    }

    public SyntaxToken FalseKeyword { get; }

    internal BooleanFalseExpressionSyntax(InternalSyntax.BooleanFalseExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.BooleanFalseExpressionSyntax InternalSyntax => (InternalSyntax.BooleanFalseExpressionSyntax)_internalNode;
}
