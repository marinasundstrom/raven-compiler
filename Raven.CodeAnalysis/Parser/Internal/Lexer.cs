using System.IO;
using System.Text;

using Raven.CodeAnalysis.Syntax.InternalSyntax;

using SyntaxKind = Raven.CodeAnalysis.Syntax.SyntaxKind;

namespace Raven.CodeAnalysis.Parser.Internal;

public class Lexer : ILexer
{
    private readonly TextReader textReader;
    private StringBuilder? stringBuilder;
    private SyntaxToken? lookaheadToken;

    public Lexer(TextReader textReader)
    {
        this.textReader = textReader;
    }

    public SyntaxToken ReadToken()
    {
        if (lookaheadToken != null)
        {
            var token = (SyntaxToken)lookaheadToken;
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
        return (SyntaxToken)lookaheadToken;
    }

    private SyntaxToken ReadTokenCore()
    {
        while (ReadChar(out var ch))
        {
            if (char.IsLetterOrDigit(ch))
            {
                stringBuilder = new StringBuilder();

                stringBuilder.Append(ch);

                if (char.IsLetter(ch))
                {
                    SyntaxKind syntaxKind = SyntaxKind.IdentifierToken;

                    while (PeekChar(out ch) && (char.IsLetter(ch) || char.IsDigit(ch) || ch == '_' || ch == '$'))
                    {
                        ReadChar();
                        stringBuilder.Append(ch);
                    }

                    if (!Syntax.SyntaxFacts.ParseReservedWord(stringBuilder.ToString(), out syntaxKind))
                    {
                        syntaxKind = SyntaxKind.IdentifierToken;
                    }

                    return new SyntaxToken(syntaxKind, stringBuilder.ToString());
                }
                else if (char.IsDigit(ch))
                {
                    while (PeekChar(out ch) && char.IsDigit(ch))
                    {
                        ReadChar();
                        stringBuilder.Append(ch);
                    }

                    return new SyntaxToken(SyntaxKind.NumberToken, stringBuilder.ToString());
                }
            }
            else
            {
                string chStr = string.Intern(ch.ToString());

                switch (ch)
                {
                    case '+':
                        return new SyntaxToken(SyntaxKind.PlusToken, chStr);

                    case '-':
                        return new SyntaxToken(SyntaxKind.DashToken, chStr);

                    case '/':
                        return new SyntaxToken(SyntaxKind.SlashToken, chStr);

                    case '\\':
                        return new SyntaxToken(SyntaxKind.BackslashToken, chStr);

                    case '*':
                        return new SyntaxToken(SyntaxKind.StarToken, chStr);

                    case '%':
                        return new SyntaxToken(SyntaxKind.PercentToken, chStr);


                    case ':':
                        return new SyntaxToken(SyntaxKind.ColonToken, chStr);

                    case ';':
                        return new SyntaxToken(SyntaxKind.SemicolonToken, chStr);


                    case '"':
                        return new SyntaxToken(SyntaxKind.DoublequoteToken, chStr);

                    case '\'':
                        return new SyntaxToken(SyntaxKind.SinglequoteToken, chStr);

                    case '`':
                        return new SyntaxToken(SyntaxKind.BackquoteToken, chStr);


                    case '(':
                        return new SyntaxToken(SyntaxKind.OpeningParenToken, chStr);

                    case ')':
                        return new SyntaxToken(SyntaxKind.ClosingParenToken, chStr);

                    case '{':
                        return new SyntaxToken(SyntaxKind.OpenBraceToken, chStr);

                    case '}':
                        return new SyntaxToken(SyntaxKind.CloseBraceToken, chStr);

                    case '[':
                        return new SyntaxToken(SyntaxKind.OpenSquareToken, chStr);

                    case ']':
                        return new SyntaxToken(SyntaxKind.CloseSquareToken, chStr);

                    case '<':
                        return new SyntaxToken(SyntaxKind.OpenAngleToken, chStr);

                    case '>':
                        return new SyntaxToken(SyntaxKind.CloseAngleToken, chStr);


                    case ' ':
                        int length = 1;

                        while (ReadWhile(' '))
                        {
                            length++;
                        }

                        return new SyntaxToken(SyntaxKind.WhitespaceTrivia, string.Intern(new string(' ', length)));

                    case '\t':
                        length = 1;

                        while (ReadWhile(' '))
                        {
                            length++;
                        }

                        return new SyntaxToken(SyntaxKind.TabTrivia, string.Empty);

                    case '\r':
                        while (ReadChar(out ch) && ch == '\n')
                        {
                            return new SyntaxToken(SyntaxKind.NewlineTrivia);
                        }
                        break;
                }
            }

            return new SyntaxToken(SyntaxKind.InvalidSyntax, ch.ToString());
        }

        return new SyntaxToken(SyntaxKind.EndOfFileTrivia, null);
    }

    private void ReadChar()
    {
        textReader.Read();
    }


    private bool ReadChar(out char ch)
    {
        var value = textReader.Read();
        if (value == -1)
        {
            ch = default;
            return false;
        }
        ch = (char)value;
        return true;
    }

    private bool PeekChar(out char ch)
    {
        var value = textReader.Peek();
        if (value == -1)
        {
            ch = default;
            return false;
        }
        ch = (char)value;
        return true;
    }


    private bool ReadWhile(char ch)
    {
        var value = textReader.Peek();
        if (value == ch)
        {
            textReader.Read();
            return true;
        }
        return false;
    }

    public bool IsEndOfLine => textReader.Peek() == -1;
}