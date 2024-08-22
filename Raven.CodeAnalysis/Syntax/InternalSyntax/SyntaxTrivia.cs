namespace Raven.CodeAnalysis.Syntax.InternalSyntax;


public struct SyntaxTrivia
{
    public SyntaxTrivia(SyntaxKind kind, SyntaxToken token)
    {
        Kind = kind;
        Token = token;
    }

    public SyntaxKind Kind { get; }

    public SyntaxToken Token { get; }

    public int Width => Token.Width;

    public string Text => Token.Text!;

    public override string ToString()
    {
        return Token.ToString();
    }
}