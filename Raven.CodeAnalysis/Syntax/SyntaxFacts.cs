using System;

namespace Raven.CodeAnalysis.Syntax;

public static class SyntaxFacts
{
    public static string? GetSyntaxTokenText(this SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.WhitespaceTrivia => " ",
            SyntaxKind.NewlineTrivia => "\n",
            SyntaxKind.OpeningParenToken => "(",
            SyntaxKind.ClosingParenToken => ")",
            SyntaxKind.OpenBraceToken => "{",
            SyntaxKind.CloseBraceToken => "}",
            SyntaxKind.OpenSquareToken => "[",
            SyntaxKind.CloseSquareToken => "]",
            SyntaxKind.OpenAngleToken => "<",
            SyntaxKind.CloseAngleToken => ">",
            SyntaxKind.AssignToken => "=",
            SyntaxKind.EqualsToken => "==",
            SyntaxKind.NotEqualsToken => "!=",
            SyntaxKind.GreaterEqualsToken => ">=",
            SyntaxKind.LessEqualsToken => "<=",
            SyntaxKind.DotToken => ".",
            SyntaxKind.PlusToken => "+",
            SyntaxKind.DashToken => "-",
            SyntaxKind.SlashToken => "/",
            SyntaxKind.BackslashToken => "\\",
            SyntaxKind.StarToken => "*",
            SyntaxKind.PercentToken => "%",
            SyntaxKind.ColonToken => ":",
            SyntaxKind.SemicolonToken => ";",
            SyntaxKind.DoublequoteToken => "\"",
            SyntaxKind.SinglequoteToken => "'",
            SyntaxKind.BackquoteToken => "`",
            SyntaxKind.IfToken => "if",
            SyntaxKind.ElseToken => "else",
            SyntaxKind.VarToken => "var",
            SyntaxKind.LetToken => "let",
            _ => null
        };
    }

    public static int GetSyntaxLength(this SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.WhitespaceTrivia:
            case SyntaxKind.NewlineTrivia:
                return 1;

            case SyntaxKind.OpeningParenToken:
            case SyntaxKind.ClosingParenToken:
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.CloseBraceToken:
            case SyntaxKind.OpenSquareToken:
            case SyntaxKind.CloseSquareToken:
            case SyntaxKind.OpenAngleToken:
            case SyntaxKind.CloseAngleToken:
                return 1;

            case SyntaxKind.AssignToken:
                return 1;

            case SyntaxKind.EqualsToken:
            case SyntaxKind.NotEqualsToken:
            case SyntaxKind.GreaterEqualsToken:
            case SyntaxKind.LessEqualsToken:
                return 2;

            case SyntaxKind.DotToken:
                return 1;

            case SyntaxKind.PlusToken:
            case SyntaxKind.DashToken:
            case SyntaxKind.SlashToken:
            case SyntaxKind.BackslashToken:
            case SyntaxKind.StarToken:
            case SyntaxKind.PercentToken:
            case SyntaxKind.ColonToken:
            case SyntaxKind.SemicolonToken:
            case SyntaxKind.DoublequoteToken:
            case SyntaxKind.SinglequoteToken:
            case SyntaxKind.BackquoteToken:
                return 1;

            case SyntaxKind.VarToken:
            case SyntaxKind.LetToken:
                return 3;
            case SyntaxKind.IfToken:
                return 2;
            case SyntaxKind.ElseToken:
                return 4;
        }

        return -1;
    }

    public static bool ParseReservedWord(string text, out SyntaxKind syntaxKind)
    {
        if (Enum.TryParse<SyntaxKind>($"{text}Token", true, out syntaxKind))
        {
            switch (syntaxKind)
            {
                case SyntaxKind.VarToken:
                case SyntaxKind.LetToken:
                case SyntaxKind.IfToken:
                case SyntaxKind.ElseToken:
                    return true;
            }
        }

        return false;
    }

    /*
    public static int GetBinaryOperatorPrecedence(this SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.StarToken:
            case SyntaxKind.SlashToken:
                return 5;

            case SyntaxKind.PlusToken:
            case SyntaxKind.DashToken:
                return 4;

            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.BangEqualsToken:
            case SyntaxKind.LessToken:
            case SyntaxKind.LessOrEqualsToken:
            case SyntaxKind.GreaterToken:
            case SyntaxKind.GreaterOrEqualsToken:
                return 3;

            case SyntaxKind.AmpersandToken:
            case SyntaxKind.AmpersandAmpersandToken:
                return 2;

            case SyntaxKind.PipeToken:
            case SyntaxKind.PipePipeToken:
            case SyntaxKind.HatToken:
                return 1;

            default:
                return 0;
        }
    }
    */
}