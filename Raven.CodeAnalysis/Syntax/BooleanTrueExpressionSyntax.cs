namespace Raven.CodeAnalysis.Syntax;

public sealed class BooleanTrueExpressionSyntax : BooleanExpressionSyntax
{
    public BooleanTrueExpressionSyntax(SyntaxToken trueKeyword)
    {
        TrueKeyword = trueKeyword;

        AttachChild(0, trueKeyword);

        _internalNode = new InternalSyntax.BooleanTrueExpressionSyntax(
            trueKeyword.InternalSyntax
        );
    }

    public SyntaxToken TrueKeyword { get; }

    internal BooleanTrueExpressionSyntax(InternalSyntax.BooleanTrueExpressionSyntax internalSyntax)
    {
        _internalNode = internalSyntax;
    }

    new internal InternalSyntax.BooleanTrueExpressionSyntax InternalSyntax => (InternalSyntax.BooleanTrueExpressionSyntax)_internalNode;
}