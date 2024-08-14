namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class NumberExpressionSyntax : ExpressionSyntax
{
    public NumberExpressionSyntax(SyntaxToken token)
    {
        Token = AddChild(token);
    }

    public override SyntaxKind Kind => SyntaxKind.NumberExpression;

    public SyntaxToken Token { get; }

    public override string ToFullString()
    {
        return Token.ToFullString();
    }
}
