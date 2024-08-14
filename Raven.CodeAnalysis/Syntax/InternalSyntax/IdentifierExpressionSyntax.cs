namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public sealed class IdentifierExpressionSyntax : ExpressionSyntax
{
    public IdentifierExpressionSyntax(SyntaxToken token)
    {
        Token = AddChild(token);
    }

    public override SyntaxKind Kind => SyntaxKind.IdentifierExpression;

    public SyntaxToken Token { get; }

    public override string ToFullString()
    {
        return Token.ToFullString();
    }
}