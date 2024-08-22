using System.Diagnostics.CodeAnalysis;

using Raven.CodeAnalysis.Parser.Internal;
using Raven.CodeAnalysis.Syntax.InternalSyntax;

using SyntaxKind = Raven.CodeAnalysis.Syntax.SyntaxKind;

namespace Raven.CodeAnalysis.Parser;

public class SyntaxParser
{
    private readonly Tokenizer tokenizer;

    public SyntaxParser(Tokenizer tokenizer)
    {
        this.tokenizer = tokenizer;
    }

    public StatementSyntax? ParseStatement()
    {
        var token = tokenizer.PeekToken();

        switch (token.Kind)
        {
            case SyntaxKind.OpenBraceToken:
                return ParseBlockSyntax();

            case SyntaxKind.IfToken:
                return ParseIfStatementSyntax();
        }

        return ParseDeclarationOrExpressionStatementSyntax();
    }

    private StatementSyntax? ParseDeclarationOrExpressionStatementSyntax()
    {
        return null;
    }

    private IfElseStatementSyntax? ParseIfStatementSyntax()
    {
        if (Consume(SyntaxKind.IfToken, out var ifToken))
        {
            //Consume(SyntaxKind.OpeningParenToken, out var openParenToken);

            var condition = ParseExpression();

            Consume(SyntaxKind.ThenToken, out var thenToken);

            //Consume(SyntaxKind.ClosingParenToken, out var closeParenToken);

            var statement = ParseExpression();

            ElseClauseSyntax? elseClauseSyntax = null;

            var elseToken = tokenizer.PeekToken();

            if (elseToken.Kind == SyntaxKind.ElseToken)
            {
                elseClauseSyntax = ParseElseClauseSyntax();
            }

            return new IfElseStatementSyntax(ifToken, condition!, thenToken!, statement!, null, null);
        }

        return null;
    }

    private ElseClauseSyntax? ParseElseClauseSyntax()
    {
        if (Consume(SyntaxKind.ElseToken, out var elseToken))
        {
            return new ElseClauseSyntax(elseToken, ParseStatement());
        }

        return null;
    }

    private ExpressionSyntax? ParseExpression()
    {
        tokenizer.ReadToken();
        return null;
    }

    public BlockSyntax? ParseBlockSyntax()
    {
        if (Consume(SyntaxKind.OpenBraceToken, out var openBraceToken))
        {
            Consume(SyntaxKind.CloseBraceToken, out var closeBraceToken);

            return new BlockSyntax(openBraceToken, closeBraceToken!);
        }

        return null;
    }

    /*

    public Expression ParseExpression()
    {
        return ParseOrExpression();
    }

    private Expression ParseOrExpression()
    {
        Expression ret = ParseAndExpression();
        TokenInfo token;
        while (MaybeEat(TokenKind.Or, out token))
        {
            ret = new BinaryExpression(token, ret, ParseAndExpression());
        }
        return ret;
    }

    private Expression ParseAndExpression()
    {
        Expression ret = ParseNotExpression();
        TokenInfo token;
        while (MaybeEat(TokenKind.And, out token))
        {
            ret = new BinaryExpression(token, ret, ParseAndExpression());
        }
        return ret;
    }

    private Expression ParseNotExpression()
    {
        var token = TokenInfo.Empty;
        if (MaybeEat(TokenKind.NotKeyword, out token))
        {
            Expression ret = new UnaryExpression(token, ParseNotExpression());
            return ret;
        }
        else
        {
            return ParseComparisonExpression();
        }
    }

    /// <summary>
    /// Parse a comparison expression.
    /// </summary>
    /// <returns>An expression.</returns>
    internal Expression ParseComparisonExpression()
    {
        Expression expr = ParseExpressionCore(0);
        while (true)
        {
            var token = PeekToken();

            switch (token.Kind)
            {
                case TokenKind.CloseAngleBracket:
                case TokenKind.GreaterOrEqual:
                case TokenKind.Less:
                case TokenKind.OpenAngleBracket:
                case TokenKind.Equal:
                case TokenKind.NotEquals:
                    ReadToken();
                    break;
                default:
                    return expr;
            }
            Expression rhs = ParseComparisonExpression();
            expr = new BinaryExpression(token, expr, rhs);
        }
    }

    /// <summary>
    /// Parse an expression (Internal)
    /// </summary>
    /// <returns>An expression.</returns>
    /// <param name="precedence">The current level of precedence.</param>
    private Expression ParseExpressionCore(int precedence)
    {
        var expr = ParseFactorExpression();

        while (true)
        {
            var operatorCandidate = PeekToken();

            int prec;
            if (!TryResolveOperatorPrecedence(operatorCandidate, out prec))
                return expr;

            ReadToken();

            if (prec >= precedence)
            {
                var right = ParseExpressionCore(prec + 1);
                expr = new BinaryExpression(operatorCandidate, expr, right);
            }
            else
            {
                return expr;
            }
        }
    }

    /// <summary>
    /// Try to resolve an operation from a specified candidate token.
    /// </summary>
    /// <returns><c>true</c>, if the token is an operation, <c>false</c> otherwise.</returns>
    /// <param name="candidateToken">The candidate token for operation.</param>
    /// <param name="precedence">The operator precedence for the resolved operation.</param>
    private bool TryResolveOperatorPrecedence(TokenInfo candidateToken, out int precedence)
    {
        switch (candidateToken.Kind)
        {
            case TokenKind.Percent:
            case TokenKind.Slash:
            case TokenKind.Star:
                precedence = 2;
                break;

            case TokenKind.Minus:
            case TokenKind.Plus:
                precedence = 1;
                break;

            default:
                precedence = -1;
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parse a factor expression.
    /// </summary>
    /// <returns>An expression.</returns>
    private Expression ParseFactorExpression()
    {
        Expression expr;

        TokenInfo token = PeekToken();

        switch (token.Kind)
        {
            case TokenKind.Plus:
                ReadToken();
                expr = ParseFactorExpression();
                expr = new UnaryExpression(token, expr);
                break;

            case TokenKind.Minus:
                ReadToken();
                expr = ParseFactorExpression();
                expr = new UnaryExpression(token, expr);
                break;

            default:
                return ParsePowerExpression();
        }

        return expr;
    }

    /// <summary>
    /// Parse a power expression.
    /// </summary>
    /// <returns>An expression.</returns>
    private Expression ParsePowerExpression()
    {
        Expression expr = ParsePrimaryExpression();

        if (MaybeEat(TokenKind.Caret, out var token))
        {
            Expression right = ParseFactorExpression();
            expr = new BinaryExpression(token, expr, right);
        }

        return expr;
    }

    /// <summary>
    /// Parse a primary expression.
    /// </summary>
    /// <returns>An expression.</returns>
    private Expression ParsePrimaryExpression()
    {
        Expression expr = null;

        TokenInfo token;

        token = PeekToken();

        switch (token.Kind)
        {
            case TokenKind.Identifier:
                ReadToken();
                expr = new IdentifierExpression(token);
                break;

            case TokenKind.TrueKeyword:
                ReadToken();
                expr = new TrueLiteralExpression(token);
                break;

            case TokenKind.FalseKeyword:
                ReadToken();
                expr = new FalseLiteralExpression(token);
                break;

            case TokenKind.IfKeyword:
                expr = ParseIfThenElseExpression();
                break;

            case TokenKind.LetKeyword:
                expr = ParseLetExpression();
                break;

            case TokenKind.Number:
                expr = ParseNumberExpression();
                break;

            case TokenKind.OpenParen:
                expr = ParseParenthesisExpression();
                break;

            case TokenKind.EndOfFile:
                ReadToken();
                Diagnostics.AddError(Strings.Error_UnexpectedEndOfFile, token.GetSpan());
                break;
        }

        return expr;
    }

    private Expression ParseParenthesisExpression()
    {
        TokenInfo token, token2;
        Expression expr = null;

        token = ReadToken();
        token2 = PeekToken();
        if (!MaybeEat(TokenKind.CloseParen, out token2))
        {
            expr = ParseExpression();
        }
        if (expr == null)
        {
            Diagnostics.AddError(string.Format(Strings.Error_InvalidExpressionTerm, token2.Value), token2.GetSpan());
        }
        else
        {
            if (!Eat(TokenKind.CloseParen, out token2))
            {
                Diagnostics.AddError(string.Format(Strings.Error_ExpectedToken, ')'), token2.GetSpan());
            }
        }
        return new ParenthesisExpression(token, expr, token2);
    }

    private Expression ParseNumberExpression()
    {
        TokenInfo token, token2, token3;
        Expression expr = null;

        token = ReadToken();
        if (MaybeEat(TokenKind.Period, out token2))
        {
            if (MaybeEat(TokenKind.Number, out token3))
            {
                expr = new RealNumberExpression(token, token2, token3);
            }
            else
            {
                Diagnostics.AddError(string.Format(Strings.Error_UnexpectedToken, token3.Value), token3.GetSpan());
            }
        }
        else
        {
            expr = new IntegerNumberExpression(token);
        }

        return expr;
    }

    */

    private bool Consume(SyntaxKind kind, [NotNullWhen(true)] out SyntaxToken? token)
    {
        token = tokenizer.PeekToken();
        if (token.Kind == kind)
        {
            tokenizer.ReadToken();
            return true;
        }
        token = null;
        return false;
    }
}