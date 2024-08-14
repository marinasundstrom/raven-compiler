namespace Raven.CodeAnalysis.Syntax.InternalSyntax;

public static partial class SyntaxFactory
{
    //public static SyntaxTrivia ElasticToken { get; } = new SyntaxTrivia((SyntaxKind.ElasticToken);

    public static SyntaxToken ElasticToken { get; } = new SyntaxToken(SyntaxKind.ElasticToken);

    public static readonly SyntaxToken AssignmentToken = new SyntaxToken(SyntaxKind.EqualsToken, "=");

    public static readonly SyntaxToken IfToken = new SyntaxToken(SyntaxKind.IfToken, "if");

    public static readonly SyntaxToken ThenToken = new SyntaxToken(SyntaxKind.ThenToken, "then");

    public static readonly SyntaxToken ElseKeyword = new SyntaxToken(SyntaxKind.ElseToken, "else");

    public static readonly SyntaxToken LetToken = new SyntaxToken(SyntaxKind.LetToken, "let");

    public static readonly SyntaxToken TrueToken = new SyntaxToken(SyntaxKind.TrueToken, "true");

    public static readonly SyntaxToken FalseToken = new SyntaxToken(SyntaxKind.FalseToken, "false");


    //public static readonly SyntaxToken ThenKeyword = new SyntaxToken(SyntaxKind.OpeningParenToken, "then");

    public static readonly SyntaxToken GreaterThanToken = new SyntaxToken(SyntaxKind.CloseAngleToken, ">");

    public static readonly SyntaxToken OpeningParenToken = new SyntaxToken(SyntaxKind.OpeningParenToken, "(");

    public static readonly SyntaxToken ClosingParenToken = new SyntaxToken(SyntaxKind.ClosingParenToken, ")");


    public static SyntaxToken Identifier(SyntaxTriviaList leadingTrivia, SyntaxKind contextualKind, string text, SyntaxTriviaList trailingTrivia)
    {
        return new SyntaxToken(leadingTrivia, contextualKind, text, trailingTrivia);
    }
}