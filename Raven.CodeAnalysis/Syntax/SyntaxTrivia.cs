namespace Raven.CodeAnalysis.Syntax;

public struct SyntaxTrivia
{
    private readonly InternalSyntax.SyntaxTrivia _internalTrivia;

    public SyntaxTrivia(SyntaxKind kind, SyntaxToken token)
    {
        Token = token;
        _internalTrivia = new InternalSyntax.SyntaxTrivia(kind, token.InternalSyntax);
    }

    internal SyntaxTrivia(InternalSyntax.SyntaxTrivia internalTrivia)
    {
        _internalTrivia = internalTrivia;
    }

    public SyntaxKind Kind => _internalTrivia.Kind;

    public SyntaxToken Token { get; }

    public int Width => Token.Width;

    public string Text => _internalTrivia.Text;

    public override string ToString()
    {
        return Token.ToString();
    }
}