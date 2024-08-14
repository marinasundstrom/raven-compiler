using Raven.CodeAnalysis.Syntax.InternalSyntax;

using SyntaxKind = Raven.CodeAnalysis.Syntax.SyntaxKind;

namespace Raven.CodeAnalysis.Parser.Internal;

public class Tokenizer
{
    private readonly ILexer lexer;
    private SyntaxToken? lookaheadToken;

    public Tokenizer(ILexer lexer)
    {
        this.lexer = lexer;
    }

    public SyntaxToken ReadToken()
    {
        if (lookaheadToken != null)
        {
            var token = lookaheadToken;
            lookaheadToken = null;
            return token;
        }
        return ReadTokenCore();
    }

    public SyntaxToken PeekToken()
    {
        if (lookaheadToken == null)
        {
            lookaheadToken = ReadTokenCore();
        }
        return lookaheadToken!;
    }

    private SyntaxToken ReadTokenCore()
    {
        SyntaxToken token;

        SyntaxTriviaList leadingTrivia;
        SyntaxTriviaList trailingTrivia;

        leadingTrivia = ReadTrivia();

        token = lexer.ReadToken();

        trailingTrivia = ReadTrivia();

        return new SyntaxToken(leadingTrivia, token.Kind, token.Text ?? string.Empty, trailingTrivia);
    }

    private SyntaxTriviaList ReadTrivia()
    {
        SyntaxTriviaList trivia = new SyntaxTriviaList();

        while (true)
        {
            var token = lexer.PeekToken();

            switch (token)
            {
                case SyntaxToken it when it.Kind == SyntaxKind.WhitespaceTrivia:
                    lexer.ReadToken();
                    trivia.Add(new SyntaxTrivia(SyntaxKind.WhitespaceTrivia, token));
                    continue;

                case SyntaxToken it when it.Kind == SyntaxKind.NewlineTrivia:
                    lexer.ReadToken();
                    trivia.Add(new SyntaxTrivia(SyntaxKind.NewlineTrivia, token));
                    continue;

                case SyntaxToken it when it.Kind == SyntaxKind.EndOfFileTrivia:
                    lexer.ReadToken();
                    trivia.Add(new SyntaxTrivia(SyntaxKind.EndOfFileTrivia, token));
                    break;
            }

            break;
        }

        return trivia;
    }

    private bool ConsumeToken(SyntaxKind kind, out SyntaxToken token)
    {
        token = lexer.PeekToken();
        if (token.Kind == kind)
        {
            lexer.ReadToken();
            return true;
        }
        return false;
    }
}