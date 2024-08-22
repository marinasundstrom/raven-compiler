namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class BooleanTrueExpressionSyntax : BooleanExpressionSyntax
{
    public BooleanTrueExpressionSyntax(SyntaxToken trueKeyword)
    {
        TrueKeyword = AddChild(0, trueKeyword);
    }

    public override SyntaxKind Kind => SyntaxKind.BooleanTrueExpression;

    public SyntaxToken TrueKeyword { get; }

    public override string ToFullString()
    {
        return TrueKeyword.ToFullString();
    }
}