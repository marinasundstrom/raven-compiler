namespace Raven.CodeAnalysis.Syntax.InternalSyntax.Parser;

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;

using Raven.CodeAnalysis.Text;

using static Raven.CodeAnalysis.Syntax.InternalSyntax.SyntaxFactory;

using SyntaxFacts = Raven.CodeAnalysis.Syntax.SyntaxFacts;

internal partial class ExpressionSyntaxParser : SyntaxParser
{
    private readonly bool _allowMatchExpressionSuffixes;
    private readonly bool _stopOnOpenBrace;
    private readonly bool _allowLambdaExpressions;
    private readonly bool _stopOnLeadingNewlineBinaryOperator;
    private readonly bool _stopAtDotDotToken;
    private readonly bool _stopAtLeadingMatchArmOperator;
    private readonly bool _parseAbruptTransfersInBlocksAsExpressions;
    private bool _stopAfterPrimaryExpression;
    private const int RangeOperatorPrecedence = 4;

    public ExpressionSyntaxParser(
        ParseContext parent,
        bool allowMatchExpressionSuffixes = true,
        bool stopOnOpenBrace = false,
        bool allowLambdaExpressions = true,
        bool stopOnLeadingNewlineBinaryOperator = false,
        bool stopAtDotDotToken = false,
        bool stopAtLeadingMatchArmOperator = false,
        bool? parseAbruptTransfersInBlocksAsExpressions = null)
        : base(parent)
    {
        _allowMatchExpressionSuffixes = allowMatchExpressionSuffixes;
        _stopOnOpenBrace = stopOnOpenBrace;
        _allowLambdaExpressions = allowLambdaExpressions;
        _stopOnLeadingNewlineBinaryOperator = stopOnLeadingNewlineBinaryOperator;
        _stopAtDotDotToken = stopAtDotDotToken;
        _stopAtLeadingMatchArmOperator = stopAtLeadingMatchArmOperator;
        _parseAbruptTransfersInBlocksAsExpressions = parseAbruptTransfersInBlocksAsExpressions
            ?? (parent as ExpressionSyntaxParser)?._parseAbruptTransfersInBlocksAsExpressions
            ?? true;
    }

    public ExpressionSyntaxParser ParentExpression => (ExpressionSyntaxParser)Parent!;

    internal bool StopsOnOpenBrace => _stopOnOpenBrace;

    public ExpressionSyntax ParseExpression()
    {
        return ParseNullCoalesceExpression() ?? new ExpressionSyntax.Missing();
    }

    public ExpressionSyntax ParseExpressionOrNull()
    {
        return ParseNullCoalesceExpression();
    }

    public BlockSyntax ParseBlockSyntax()
    {
        var openBrace = ExpectToken(SyntaxKind.OpenBraceToken);

        EnterParens(); // Treat block as a nesting construct
        var statements = new List<StatementSyntax>();

        while (!IsNextToken(SyntaxKind.CloseBraceToken, out _) &&
               !IsNextToken(SyntaxKind.EndOfFileToken, out _))
        {
            var statementStart = Position;
            var stmt = new StatementSyntaxParser(
                this,
                parseAbruptTransfersAsExpressions: _parseAbruptTransfersInBlocksAsExpressions).ParseStatement();
            if (stmt is not null)
                statements.Add(stmt);

            if (Position == statementStart)
            {
                var token = PeekToken();
                var tokenText = string.IsNullOrEmpty(token.Text)
                    ? token.Kind.ToString()
                    : token.Text;

                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                        GetSpanOfPeekedToken(),
                        tokenText));

                ReadToken();
            }

            SetTreatNewlinesAsTokens(false);
        }
        ExitParens();

        var closeBrace = ExpectToken(SyntaxKind.CloseBraceToken);

        return Block(openBrace, List(statements), closeBrace);
    }

    private SyntaxList ParseStatementsList(SyntaxKind untilToken, out SyntaxToken token)
    {
        List<StatementSyntax> statements = new List<StatementSyntax>();
        while (!ConsumeToken(untilToken, out token))
        {
            if (PeekToken().IsKind(SyntaxKind.EndOfFileToken))
                break;

            var statementStart = Position;
            var statement = new StatementSyntaxParser(this).ParseStatement();
            statements.Add(statement);

            if (Position == statementStart)
            {
                var current = PeekToken();
                var tokenText = string.IsNullOrEmpty(current.Text)
                    ? current.Kind.ToString()
                    : current.Text;

                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                        GetSpanOfPeekedToken(),
                        tokenText));

                ReadToken();
            }
        }

        return new SyntaxList(statements.ToArray());
    }

    private ExpressionSyntax ParseNullCoalesceExpression()
    {
        // `??` has lower precedence than `||` / `&&` and is right-associative.
        ExpressionSyntax left = ParseOrExpression();

        var token = PeekToken();
        if (token.IsKind(SyntaxKind.QuestionQuestionToken) && !HasLeadingBlankLine(token))
        {
            // Right-associative: a ?? b ?? c == a ?? (b ?? c)
            var operatorToken = ReadToken();
            var right = ParseNullCoalesceExpression();
            return NullCoalesceExpression(left, operatorToken, right);
        }

        return left;
    }

    private ExpressionSyntax ParseOrExpression()
    {
        ExpressionSyntax ret = ParseAndExpression();
        while (true)
        {
            var token = PeekToken();
            if (!token.IsKind(SyntaxKind.BarBarToken) || HasLeadingBlankLine(token))
                break;

            ReadToken();
            ret = InfixOperatorExpression(SyntaxKind.LogicalOrExpression, ret, token, ParseAndExpression());
        }
        return ret;
    }

    private SyntaxKind GetBinaryExpressionKind(SyntaxToken operatorToken)
    {
        switch (operatorToken.Kind)
        {
            case SyntaxKind.PlusToken:
                return SyntaxKind.AddExpression;

            case SyntaxKind.MinusToken:
                return SyntaxKind.SubtractExpression;

            case SyntaxKind.StarToken:
                return SyntaxKind.MultiplyExpression;

            case SyntaxKind.SlashToken:
                return SyntaxKind.DivideExpression;

            case SyntaxKind.PercentToken:
                return SyntaxKind.ModuloExpression;

            case SyntaxKind.AmpersandToken:
                return SyntaxKind.BitwiseAndExpression;

            case SyntaxKind.BarToken:
                return SyntaxKind.BitwiseOrExpression;

            case SyntaxKind.CaretToken:
                return SyntaxKind.BitwiseXorExpression;

            case SyntaxKind.LessThanLessThanToken:
                return SyntaxKind.ShiftLeftExpression;

            case SyntaxKind.GreaterThanGreaterThanToken:
                return SyntaxKind.ShiftRightExpression;

            case SyntaxKind.PipeToken:
                return SyntaxKind.PipeExpression;

            case SyntaxKind.EqualsEqualsToken:
                return SyntaxKind.EqualsExpression;

            case SyntaxKind.NotEqualsToken:
                return SyntaxKind.NotEqualsExpression;

            case SyntaxKind.LessThanToken:
                return SyntaxKind.LessThanExpression;

            case SyntaxKind.GreaterThanToken:
                return SyntaxKind.GreaterThanExpression;

            case SyntaxKind.LessThanOrEqualsToken:
                return SyntaxKind.LessThanOrEqualsExpression;

            case SyntaxKind.GreaterThanOrEqualsToken:
                return SyntaxKind.GreaterThanOrEqualsExpression;

            case SyntaxKind.AmpersandAmpersandToken:
            case SyntaxKind.AndToken:
                return SyntaxKind.LogicalAndExpression;

            case SyntaxKind.BarBarToken:
            case SyntaxKind.OrToken:
                return SyntaxKind.LogicalOrExpression;
        }

        throw new ArgumentException("Kind is not valid for this expression.");
    }

    private ExpressionSyntax ParseAndExpression()
    {
        ExpressionSyntax ret = ParseLogicalNotExpression();
        while (true)
        {
            var token = PeekToken();
            if (!token.IsKind(SyntaxKind.AmpersandAmpersandToken) || HasLeadingBlankLine(token))
                break;

            ReadToken();
            ret = InfixOperatorExpression(SyntaxKind.LogicalAndExpression, ret, token, ParseAndExpression());
        }
        return ret;
    }

    private ExpressionSyntax ParseLogicalNotExpression()
    {
        if (PeekToken().IsKind(SyntaxKind.ExclamationToken) &&
            PeekToken(1).IsKind(SyntaxKind.OpenBracketToken))
        {
            return ParseCollectionExpression();
        }

        if (ConsumeToken(SyntaxKind.ExclamationToken, out var token))
        {
            ExpressionSyntax ret = PrefixOperatorExpression(SyntaxKind.LogicalNotExpression, token, ParseLogicalNotExpression());
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
    private ExpressionSyntax ParseComparisonExpression()
    {
        ExpressionSyntax expr = ParseExpressionCore(0);
        while (true)
        {
            var token = PeekToken();

            if (_stopOnOpenBrace && token.IsKind(SyntaxKind.OpenBraceToken))
                return expr;

            if (HasLeadingBlankLine(token))
                return expr;

            if (_stopAtLeadingMatchArmOperator &&
                HasLeadingNewLine(token) &&
                CanStartMatchArmOperatorPattern(token.Kind))
            {
                return expr;
            }

            switch (token.Kind)
            {
                case SyntaxKind.GreaterThanToken:
                case SyntaxKind.LessThanToken:
                case SyntaxKind.GreaterThanOrEqualsToken:
                case SyntaxKind.LessThanOrEqualsToken:
                case SyntaxKind.EqualsEqualsToken:
                case SyntaxKind.NotEqualsToken:
                    ReadToken();
                    break;

                case SyntaxKind.IsKeyword:
                    {
                        ReadToken();
                        var pattern = new PatternSyntaxParser(this).ParsePattern();
                        return IsPatternExpression(expr, token, pattern);
                    }

                case SyntaxKind.AsKeyword:
                    {
                        ReadToken();
                        var type = new NameSyntaxParser(this).ParseTypeName();
                        return AsExpression(expr, token, type);
                    }

                default:
                    return expr;
            }
            ExpressionSyntax rhs = ParseComparisonExpression();
            expr = InfixOperatorExpression(GetBinaryExpressionKind(token), expr, token, rhs);
        }
    }

    /// <summary>
    /// Parse an ExpressionSyntax (Internal)
    /// </summary>
    /// <returns>An expression.</returns>
    /// <param name="precedence">The current level of precedence.</param>
    private ExpressionSyntax ParseExpressionCore(int precedence)
    {
        int start = this.Position;

        PatternSyntax? assignmentPattern = null;
        ExpressionSyntax? expr = null;

        if (precedence == 0 &&
            IsPossibleAssignmentPatternStart(PeekToken()) &&
            TryParseAssignmentPattern(out var pattern))
        {
            assignmentPattern = pattern;
        }
        else
        {
            if (PeekToken().IsKind(SyntaxKind.DotDotToken) && precedence <= RangeOperatorPrecedence)
            {
                expr = null;
            }
            else
            {
                expr = ParseFactorExpression();
            }
        }

        if (TryConsumeAssignmentOperator(out var assignToken))
        {
            ExpressionOrPatternSyntax leftNode;
            if (assignmentPattern is not null)
            {
                leftNode = assignmentPattern;
            }
            else
            {
                leftNode = expr!;

                if (expr is not IdentifierNameSyntax
                    and not MemberAccessExpressionSyntax
                    and not MemberBindingExpressionSyntax
                    and not ElementAccessExpressionSyntax
                    and not ConditionalAccessExpressionSyntax
                    and not PrefixOperatorExpressionSyntax { Kind: SyntaxKind.DereferenceExpression })
                {
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.IdentifierExpected,
                            GetActualTextSpan(start, expr)
                        ));
                }
            }

            var right = ParseExpressionCore(0);

            return AssignmentExpression(GetAssignmentExpressionKind(assignToken), leftNode, assignToken, right);
        }

        while (true)
        {
            var operatorCandidate = PeekToken();

            if (operatorCandidate.IsKind(SyntaxKind.EndOfFileToken))
                return expr ?? new ExpressionSyntax.Missing();

            if (HasLeadingBlankLine(operatorCandidate))
                return expr ?? new ExpressionSyntax.Missing();

            if (_stopAtLeadingMatchArmOperator &&
                HasLeadingNewLine(operatorCandidate) &&
                CanStartMatchArmOperatorPattern(operatorCandidate.Kind))
            {
                return expr ?? new ExpressionSyntax.Missing();
            }

            if (operatorCandidate.IsKind(SyntaxKind.DotDotToken))
            {
                // In some contexts (e.g. pattern parsing), the caller wants `..` to terminate
                // the expression instead of producing a RangeExpression.
                if (_stopAtDotDotToken)
                    return expr ?? new ExpressionSyntax.Missing();

                if (RangeOperatorPrecedence < precedence)
                    return expr ?? new ExpressionSyntax.Missing();

                ReadToken();
                var lessThanToken = ParseExclusiveRangeToken();
                var right = ParseRangeBoundaryExpression();
                expr = RangeExpression(expr, operatorCandidate, lessThanToken, right);
                continue;
            }

            int prec;
            if (!TryResolveOperatorPrecedence(operatorCandidate, out prec))
                return expr ?? new ExpressionSyntax.Missing();

            // In some contexts (e.g., equals-value initializers), a leading `*` on the
            // next line can start a dereference statement (e.g., `val p = &x` then `*p = 1`).
            // Only stop continuation for the `*identifier` shape (no trailing whitespace after `*`),
            // so newline multiplication like `* 3` can still continue.
            if (_stopOnLeadingNewlineBinaryOperator
                && IsLeadingDereferenceLikeStar(operatorCandidate))
                return expr ?? new ExpressionSyntax.Missing();

            if (prec >= precedence)
            {
                ReadToken();
                var right = ParseExpressionCore(prec + 1);
                expr = InfixOperatorExpression(GetBinaryExpressionKind(operatorCandidate), expr!, operatorCandidate, right);
            }
            else
            {
                return expr ?? new ExpressionSyntax.Missing();
            }
        }
    }

    private static bool CanStartMatchArmOperatorPattern(SyntaxKind kind)
        => kind is SyntaxKind.LessThanToken
            or SyntaxKind.LessThanOrEqualsToken
            or SyntaxKind.GreaterThanToken
            or SyntaxKind.GreaterThanOrEqualsToken
            or SyntaxKind.EqualsEqualsToken
            or SyntaxKind.NotEqualsToken;

    private ExpressionSyntax? ParseRangeBoundaryExpression()
    {
        var next = PeekToken();

        if (next.IsKind(SyntaxKind.CloseBracketToken) ||
            next.IsKind(SyntaxKind.CloseParenToken) ||
            next.IsKind(SyntaxKind.CloseBraceToken) ||
            next.IsKind(SyntaxKind.CommaToken) ||
            next.IsKind(SyntaxKind.EndOfFileToken))
        {
            return null;
        }

        return ParseExpressionCore(RangeOperatorPrecedence + 1);
    }

    private SyntaxToken ParseExclusiveRangeToken()
    {
        if (ConsumeToken(SyntaxKind.LessThanToken, out var lessThanToken))
            return lessThanToken;

        return Token(SyntaxKind.None);
    }

    private bool TryParseAssignmentPattern(out PatternSyntax pattern)
    {
        pattern = null!;

        if (!IsPossibleAssignmentPatternStart(PeekToken()))
            return false;

        // Parenthesized lambda parameters can start with pattern syntax like `([a, ...rest]) => ...`.
        // Avoid speculative pattern parsing here, which can otherwise leak diagnostics before lambda parsing wins.
        if (PeekToken().IsKind(SyntaxKind.OpenParenToken) && HasFatArrowBeforeLineBreakOrEquals())
            return false;

        var checkpoint = CreateCheckpoint("assignment-pattern");

        if (PeekToken().IsKind(SyntaxKind.OpenParenToken) &&
            ContainsAssignmentBeforeMatchingCloseParen())
        {
            checkpoint.Rewind();
            return false;
        }

        var parsedPattern = new PatternSyntaxParser(
            this,
            allowImplicitDeconstructionElementBindings: true,
            allowWholePatternDesignation: false).ParsePattern();

        if (!PeekToken().IsKind(SyntaxKind.EqualsToken))
        {
            checkpoint.Rewind();
            return false;
        }

        pattern = parsedPattern;
        return true;
    }

    private bool ContainsAssignmentBeforeMatchingCloseParen()
    {
        var depth = 0;
        var offset = 0;

        while (true)
        {
            var token = PeekToken(offset++);

            if (token.Kind == SyntaxKind.EndOfFileToken)
                return false;

            if (token.Kind == SyntaxKind.OpenParenToken)
            {
                depth++;
                continue;
            }

            if (token.Kind == SyntaxKind.CloseParenToken)
            {
                depth--;

                if (depth == 0)
                    return false;

                continue;
            }

            if (token.Kind == SyntaxKind.EqualsToken && depth > 0)
                return true;
        }
    }

    private bool HasFatArrowBeforeLineBreakOrEquals()
    {
        const int maxLookahead = 64;

        for (var i = 0; i < maxLookahead; i++)
        {
            var token = PeekToken(i);

            if (token.IsKind(SyntaxKind.EndOfFileToken))
                return false;

            if (IsNewLineLike(token))
                return false;

            if (i > 0 && TokenHasLeadingNewLine(token))
                return false;

            if (token.IsKind(SyntaxKind.FatArrowToken))
                return true;

            if (token.IsKind(SyntaxKind.EqualsToken))
                return false;
        }

        return false;
    }

    private static SyntaxKind GetAssignmentExpressionKind(SyntaxToken operatorToken)
    {
        return operatorToken.Kind switch
        {
            SyntaxKind.PlusEqualsToken => SyntaxKind.AddAssignmentExpression,
            SyntaxKind.MinusEqualsToken => SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.StarEqualsToken => SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.SlashEqualsToken => SyntaxKind.DivideAssignmentExpression,
            SyntaxKind.AmpersandEqualsToken => SyntaxKind.BitwiseAndAssignmentExpression,
            SyntaxKind.BarEqualsToken => SyntaxKind.BitwiseOrAssignmentExpression,
            SyntaxKind.CaretEqualsToken => SyntaxKind.BitwiseXorAssignmentExpression,
            SyntaxKind.QuestionQuestionEqualsToken => SyntaxKind.NullCoalesceAssignmentExpression,
            _ => SyntaxKind.SimpleAssignmentExpression,
        };
    }

    private bool TryConsumeAssignmentOperator(out SyntaxToken token)
    {
        var candidate = PeekToken();
        if (IsAssignmentOperator(candidate.Kind) && !HasLeadingBlankLine(candidate))
        {
            token = ReadToken();
            return true;
        }

        token = null!;
        return false;
    }

    private static bool IsAssignmentOperator(SyntaxKind kind)
    {
        return kind is SyntaxKind.EqualsToken
            or SyntaxKind.PlusEqualsToken
            or SyntaxKind.MinusEqualsToken
            or SyntaxKind.StarEqualsToken
            or SyntaxKind.SlashEqualsToken
            or SyntaxKind.AmpersandEqualsToken
            or SyntaxKind.BarEqualsToken
            or SyntaxKind.CaretEqualsToken
            or SyntaxKind.QuestionQuestionEqualsToken;
    }

    private static bool IsPossibleAssignmentPatternStart(SyntaxToken token)
    {
        return token.Kind switch
        {
            SyntaxKind.OpenParenToken => true,
            SyntaxKind.OpenBracketToken => true,
            SyntaxKind.LetKeyword => true,
            SyntaxKind.ValKeyword => true,
            SyntaxKind.VarKeyword => true,
            SyntaxKind.UnderscoreToken => true,
            _ => false,
        };
    }

    /// <summary>
    /// Try to resolve an operation from a specified candidate token.
    /// </summary>
    /// <returns><c>true</c>, if the token is an operation, <c>false</c> otherwise.</returns>
    /// <param name="candidateToken">The candidate token for operation.</param>
    /// <param name="precedence">The operator precedence for the resolved operation.</param>
    private bool TryResolveOperatorPrecedence(SyntaxToken candidateToken, out int precedence)
    {
        return SyntaxFacts.TryResolveOperatorPrecedence(candidateToken.Kind, out precedence);
    }

    /// <summary>
    /// Parse a factor expression.
    /// </summary>
    /// <returns>An expression.</returns>
    private ExpressionSyntax ParseFactorExpression()
    {
        if (_allowLambdaExpressions && TryParseLambdaExpression(out var lambda))
        {
            if (!_allowMatchExpressionSuffixes)
                return lambda;

            return ParseMatchExpressionSuffixes(lambda);
        }

        ExpressionSyntax expr;

        SyntaxToken token = PeekToken();

        // A disclosed macro alias takes precedence over the ordinary meaning
        // of a keyword only when the keyword is immediately followed by `!`.
        if (SyntaxFacts.IsKeywordKind(token.Kind) && IsFreestandingMacroExpressionStart())
            return ParseFreestandingMacroExpression();

        switch (token.Kind)
        {
            case SyntaxKind.PlusToken:
                ReadToken();
                if (token.TrailingTrivia.Width != 0)
                    AddDiagnostic(DiagnosticInfo.Create(CompilerDiagnostics.UnaryOperatorRequiresAdjacentOperand, GetSpanOfLastToken(), token.Text));
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.UnaryPlusExpression, token, expr);
                break;

            case SyntaxKind.PlusPlusToken:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.PreIncrementExpression, token, expr);
                break;

            case SyntaxKind.MinusToken:
                ReadToken();
                if (token.TrailingTrivia.Width != 0)
                    AddDiagnostic(DiagnosticInfo.Create(CompilerDiagnostics.UnaryOperatorRequiresAdjacentOperand, GetSpanOfLastToken(), token.Text));
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.UnaryMinusExpression, token, expr);
                break;

            case SyntaxKind.MinusMinusToken:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.PreDecrementExpression, token, expr);
                break;

            case SyntaxKind.AmpersandToken:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.AddressOfExpression, token, expr);
                break;

            case SyntaxKind.FixedKeyword:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.FixedExpression, token, expr);
                break;

            case SyntaxKind.StackAllocKeyword:
                expr = ParseStackAllocExpression();
                break;

            case SyntaxKind.StarToken:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.DereferenceExpression, token, expr);
                break;

            case SyntaxKind.TildeToken:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.BitwiseNotExpression, token, expr);
                break;

            case SyntaxKind.CaretToken:
                ReadToken();
                if (token.TrailingTrivia.Width != 0)
                {
                    var unexpected = PeekToken();
                    var unexpectedText = string.IsNullOrEmpty(unexpected.Text)
                        ? unexpected.Kind.ToString()
                        : unexpected.Text;
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                            GetSpanOfPeekedToken(),
                            unexpectedText));
                    expr = new ExpressionSyntax.Missing();
                    break;
                }

                expr = ParseFactorExpression();
                expr = IndexExpression(token, expr);
                break;

            case SyntaxKind.IfKeyword:
                expr = ParseIfExpressionSyntax();
                break;

            case SyntaxKind.MatchKeyword:
                expr = ParseMatchExpressionKeywordFirst();
                break;

            case SyntaxKind.TryKeyword:
                expr = ParseTryExpression();
                break;

            case SyntaxKind.UnsafeKeyword:
                expr = ParseUnsafeExpression();
                break;

            case SyntaxKind.ReturnKeyword:
                expr = ParseReturnExpression();
                break;

            case SyntaxKind.ThrowKeyword:
                expr = ParseThrowExpression();
                break;

            case SyntaxKind.BreakKeyword:
                expr = ParseBreakExpression();
                break;

            case SyntaxKind.ContinueKeyword:
                expr = ParseContinueExpression();
                break;

            case SyntaxKind.YieldKeyword:
                expr = ParseYieldExpression();
                break;

            case SyntaxKind.AwaitKeyword:
                ReadToken();
                expr = ParseFactorExpression();
                expr = PrefixOperatorExpression(SyntaxKind.AwaitExpression, token, expr);
                break;

            case SyntaxKind.OpenBraceToken:
                expr = ParseBlockSyntax();
                break;

            default:
                expr = ParsePowerExpression();
                break;
        }

        if (!_allowMatchExpressionSuffixes)
            return expr;

        return ParseMatchExpressionSuffixes(expr);
    }

    private StackAllocExpressionSyntax ParseStackAllocExpression()
    {
        var stackAllocKeyword = ReadToken();
        var elementType = new NameSyntaxParser(this).ParseStackAllocElementType();
        ConsumeTokenOrMissing(SyntaxKind.OpenBracketToken, out var openBracketToken);
        var count = ParseExpression();
        ConsumeTokenOrMissing(SyntaxKind.CloseBracketToken, out var closeBracketToken);
        return StackAllocExpression(stackAllocKeyword, elementType, openBracketToken, count, closeBracketToken);
    }

    private UnsafeExpressionSyntax ParseUnsafeExpression()
    {
        var unsafeKeyword = ReadToken();
        var block = ParseBlockSyntax();
        return UnsafeExpression(unsafeKeyword, block);
    }

    private TryExpressionSyntax ParseTryExpression()
    {
        var tryKeyword = ReadToken();
        var questionToken = ConsumeToken(SyntaxKind.QuestionToken, out var consumedQuestionToken)
            ? consumedQuestionToken
            : Token(SyntaxKind.None);
        var expression = new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false).ParseExpression();
        return TryExpression(tryKeyword, questionToken, expression);
    }

    private ReturnExpressionSyntax ParseReturnExpression()
    {
        var returnKeyword = ReadToken();
        var expression = HasLineBreakBeforePeekToken() || PeekToken().Kind is
            SyntaxKind.SemicolonToken or
            SyntaxKind.CloseBraceToken or
            SyntaxKind.EndOfFileToken or
            SyntaxKind.ElseKeyword
                ? null
                : new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false).ParseExpression();
        return ReturnExpression(returnKeyword, expression);
    }

    private ThrowExpressionSyntax ParseThrowExpression()
    {
        var throwKeyword = ReadToken();
        var expression = new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false).ParseExpression();
        return ThrowExpression(throwKeyword, expression);
    }

    private BreakExpressionSyntax ParseBreakExpression()
    {
        var breakKeyword = ReadToken();
        return BreakExpression(breakKeyword, ParseOptionalControlFlowLabel());
    }

    private ContinueExpressionSyntax ParseContinueExpression()
    {
        var continueKeyword = ReadToken();
        return ContinueExpression(continueKeyword, ParseOptionalControlFlowLabel());
    }

    private SyntaxToken ParseOptionalControlFlowLabel()
    {
        if (HasLineBreakBeforePeekToken())
            return Token(SyntaxKind.None);

        var next = PeekToken();
        if (CanTokenBeIdentifier(next))
            return ReadIdentifierToken();

        if (SyntaxFacts.IsReservedWordKind(next.Kind))
            return ReadToken();

        return Token(SyntaxKind.None);
    }

    private ExpressionSyntax ParseYieldExpression()
    {
        var yieldKeyword = ReadToken();
        if (ConsumeToken(SyntaxKind.BreakKeyword, out var breakKeyword))
        {
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.YieldBreakFormRemoved,
                GetSpanOfLastToken()));
            return YieldExpression(yieldKeyword, Token(SyntaxKind.None), BreakExpression(breakKeyword, Token(SyntaxKind.None)));
        }

        SyntaxToken? returnKeyword = null;
        if (ConsumeToken(SyntaxKind.ReturnKeyword, out var consumedReturnKeyword))
        {
            returnKeyword = consumedReturnKeyword;
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.YieldReturnFormRemoved,
                GetSpanOfLastToken()));
        }

        var fromKeyword = PeekToken() is { Kind: SyntaxKind.IdentifierToken, Text: "from" }
            ? ReadToken()
            : Token(SyntaxKind.None);

        var expression = new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false).ParseExpression();
        if (returnKeyword is not null)
            expression = ReturnExpression(returnKeyword, expression);

        return YieldExpression(yieldKeyword, fromKeyword, expression);
    }

    private bool TryParseLambdaExpression(out FunctionExpressionSyntax? lambda)
    {
        lambda = null;

        var token = PeekToken();

        // Lambda fast-path: if there's no `=>` ahead before newline/terminator, don't even try.
        // Bracket-starting lambdas need a slower path because target-specifier attributes
        // (e.g. `[return: Attr]`) can confuse the cheap token scan.
        if (token.Kind != SyntaxKind.OpenBracketToken &&
            token.Kind != SyntaxKind.FuncKeyword &&
            token.Kind != SyntaxKind.AsyncKeyword &&
            token.Kind != SyntaxKind.StaticKeyword &&
            !LooksLikeLambdaAhead(startOffset: 0))
            return false;

        if (token.Kind == SyntaxKind.StaticKeyword)
        {
            var checkpoint = CreateCheckpoint("static-func-lambda");
            var staticKeyword = ReadToken();
            SyntaxToken? asyncKeyword = null;
            if (ConsumeToken(SyntaxKind.AsyncKeyword, out var parsedAsyncKeyword))
                asyncKeyword = parsedAsyncKeyword;

            if (!ConsumeToken(SyntaxKind.FuncKeyword, out var funcKeyword))
            {
                checkpoint.Rewind();
                return false;
            }

            if (PeekToken().Kind == SyntaxKind.OpenParenToken)
            {
                if (!IsParenthesizedCastAhead() &&
                    TryParseParenthesizedLambdaExpression(staticKeyword, asyncKeyword, funcKeyword, out lambda))
                    return true;
            }
            else if (PeekToken().Kind == SyntaxKind.LessThanToken)
            {
                if (TryParseParenthesizedLambdaExpression(staticKeyword, asyncKeyword, funcKeyword, out lambda))
                    return true;
            }
            else if (IsSimpleLambdaParameterStart(PeekToken()))
            {
                var shapeCheckpoint = CreateCheckpoint("static-func-lambda-shape");
                _ = ReadSimpleLambdaParameterToken();
                var looksNamedParenthesized = PeekToken().Kind is SyntaxKind.OpenParenToken or SyntaxKind.LessThanToken;
                shapeCheckpoint.Rewind();

                if (looksNamedParenthesized &&
                    TryParseParenthesizedLambdaExpression(staticKeyword, asyncKeyword, funcKeyword, out lambda))
                {
                    return true;
                }

                if (TryParseSimpleLambdaExpression(staticKeyword, asyncKeyword, funcKeyword, out lambda))
                    return true;
            }

            checkpoint.Rewind();
            return false;
        }

        if (token.Kind == SyntaxKind.AsyncKeyword)
        {
            var checkpoint = CreateCheckpoint("async-lambda");
            var asyncKeyword = ReadToken();
            SyntaxToken? funcKeyword = null;
            if (ConsumeToken(SyntaxKind.FuncKeyword, out var parsedFuncKeyword))
                funcKeyword = parsedFuncKeyword;

            if (PeekToken().Kind == SyntaxKind.OpenParenToken)
            {
                if (!IsParenthesizedCastAhead() &&
                    TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword, funcKeyword, out lambda))
                    return true;
            }
            else if (funcKeyword is not null && PeekToken().Kind == SyntaxKind.LessThanToken)
            {
                if (TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword, funcKeyword, out lambda))
                    return true;
            }
            else if (IsSimpleLambdaParameterStart(PeekToken()))
            {
                var shapeCheckpoint = CreateCheckpoint("async-func-lambda-shape");
                _ = ReadSimpleLambdaParameterToken();
                var looksNamedParenthesized = PeekToken().Kind is SyntaxKind.OpenParenToken or SyntaxKind.LessThanToken;
                shapeCheckpoint.Rewind();

                if (funcKeyword is not null &&
                    looksNamedParenthesized &&
                    TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword, funcKeyword, out lambda))
                {
                    return true;
                }

                if (TryParseSimpleLambdaExpression(staticKeyword: null, asyncKeyword, funcKeyword, out lambda))
                    return true;
            }
            else if (PeekToken().Kind == SyntaxKind.OpenBracketToken)
            {
                if (funcKeyword is null && TryParseSimpleLambdaExpression(staticKeyword: null, asyncKeyword, funcKeyword, out lambda))
                    return true;

                if (funcKeyword is null && TryParseAttributedParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword, out lambda))
                    return true;
            }

            checkpoint.Rewind();
            return false;
        }

        if (token.Kind == SyntaxKind.OpenParenToken)
        {
            if (IsParenthesizedCastAhead())
                return false;

            // `(x => body)` is a parenthesized expression whose value is a simple
            // lambda. A parenthesized lambda parameter list has its arrow after the
            // matching `)`, as in `(x) => body`.
            if (HasFatArrowInsideInitialParentheses())
                return false;

            if (TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword: null, out lambda))
                return true;
        }

        if (token.Kind == SyntaxKind.OpenBracketToken)
        {
            if (TryParseSimpleLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword: null, out lambda))
                return true;

            if (TryParseAttributedParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword: null, out lambda))
                return true;
        }

        if (IsSimpleLambdaParameterStart(token) &&
            TryParseSimpleLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword: null, out lambda))
            return true;

        if (token.Kind == SyntaxKind.FuncKeyword)
        {
            var checkpoint = CreateCheckpoint("func-lambda");
            var funcKeyword = ReadToken();

            if (PeekToken().Kind == SyntaxKind.OpenParenToken)
            {
                if (!IsParenthesizedCastAhead() &&
                    TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword, out lambda))
                    return true;
            }
            else if (PeekToken().Kind == SyntaxKind.LessThanToken)
            {
                if (TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword, out lambda))
                    return true;
            }
            else if (IsSimpleLambdaParameterStart(PeekToken()))
            {
                var shapeCheckpoint = CreateCheckpoint("func-lambda-shape");
                _ = ReadSimpleLambdaParameterToken();
                var looksNamedParenthesized = PeekToken().Kind is SyntaxKind.OpenParenToken or SyntaxKind.LessThanToken;
                shapeCheckpoint.Rewind();

                if (looksNamedParenthesized &&
                    TryParseParenthesizedLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword, out lambda))
                {
                    return true;
                }

                if (TryParseSimpleLambdaExpression(staticKeyword: null, asyncKeyword: null, funcKeyword, out lambda))
                    return true;
            }

            checkpoint.Rewind();
            return false;
        }

        return false;
    }

    private bool HasFatArrowInsideInitialParentheses()
    {
        if (!PeekToken().IsKind(SyntaxKind.OpenParenToken))
            return false;

        const int maxLookahead = 64;
        var depth = 0;

        for (var offset = 0; offset < maxLookahead; offset++)
        {
            var token = PeekToken(offset);
            if (token.IsKind(SyntaxKind.EndOfFileToken) ||
                (offset > 0 && TokenHasLeadingNewLine(token)))
            {
                return false;
            }

            if (token.IsKind(SyntaxKind.OpenParenToken))
            {
                depth++;
                continue;
            }

            if (token.IsKind(SyntaxKind.CloseParenToken))
            {
                depth--;
                if (depth == 0)
                    return false;

                continue;
            }

            if (depth > 0 && token.IsKind(SyntaxKind.FatArrowToken))
                return true;
        }

        return false;
    }

    private bool IsParenthesizedCastAhead()
    {
        var checkpoint = CreateCheckpoint("lambda-cast-lookahead");
        try
        {
            if (!PeekToken().IsKind(SyntaxKind.OpenParenToken))
                return false;

            ReadToken();

            var typeName = new NameSyntaxParser(this).ParseTypeName();

            if (typeName.IsMissing)
                return false;

            if (!PeekToken().IsKind(SyntaxKind.CloseParenToken))
                return false;

            ReadToken();

            var next = PeekToken();

            return !next.IsKind(SyntaxKind.FatArrowToken);
        }
        finally
        {
            checkpoint.Rewind();
        }
    }

    private bool TryParseParenthesizedLambdaExpression(
        SyntaxToken? staticKeyword,
        SyntaxToken? asyncKeyword,
        SyntaxToken? funcKeyword,
        out FunctionExpressionSyntax? lambda)
    {
        lambda = null;

        var allowBlockBodyWithoutArrow = funcKeyword is { Kind: SyntaxKind.FuncKeyword };

        var checkpoint = CreateCheckpoint("parenthesized-lambda");

        var lambdaIdentifier = Token(SyntaxKind.None);
        if (funcKeyword is { Kind: SyntaxKind.FuncKeyword } && CanTokenBeIdentifier(PeekToken()))
        {
            var identifierCheckpoint = CreateCheckpoint("func-lambda-identifier");
            var identifierToken = ReadIdentifierToken();
            if (PeekToken().IsKind(SyntaxKind.LessThanToken) || PeekToken().IsKind(SyntaxKind.OpenParenToken))
            {
                lambdaIdentifier = identifierToken;
            }
            else
            {
                identifierCheckpoint.Rewind();
            }
        }

        TypeParameterListSyntax? typeParameterList = null;
        if (funcKeyword is { Kind: SyntaxKind.FuncKeyword } && PeekToken().IsKind(SyntaxKind.LessThanToken))
        {
            typeParameterList = new TypeDeclarationParser(this).ParseTypeParameterList();
        }

        if (!PeekToken().IsKind(SyntaxKind.OpenParenToken))
        {
            checkpoint.Rewind();
            return false;
        }

        // Fast-path: only speculate if we can see a `=>` ahead before newline/terminator.
        if (!allowBlockBodyWithoutArrow && !LooksLikeLambdaAhead(startOffset: 0))
        {
            checkpoint.Rewind();
            return false;
        }

        var parameterList = new StatementSyntaxParser(this).ParseParameterList(
            allowDestructuringPatterns: true,
            allowDiscardParameters: true);

        var returnType = new TypeAnnotationClauseSyntaxParser(this).ParseReturnTypeAnnotation();
        var constraintClauses = typeParameterList is not null
            ? new ConstrainClauseListParser(this).ParseConstraintClauseList()
            : SyntaxList.Empty;

        if (returnType is null &&
            !IsNextToken(SyntaxKind.FatArrowToken) &&
            !(allowBlockBodyWithoutArrow && PeekToken().IsKind(SyntaxKind.OpenBraceToken)))
        {
            checkpoint.Rewind();
            return false;
        }
        BlockSyntax? blockBody;
        ArrowExpressionClauseSyntax? expressionBody;
        if (IsNextToken(SyntaxKind.FatArrowToken))
        {
            ConsumeTokenOrMissing(SyntaxKind.FatArrowToken, out var fatArrowToken);
            var body = PeekToken().IsKind(SyntaxKind.OpenBraceToken)
                ? new ExpressionSyntaxParser(
                    this,
                    parseAbruptTransfersInBlocksAsExpressions: false).ParseBlockSyntax()
                : ParseRequiredLambdaExpressionBody();
            blockBody = null;
            expressionBody = ArrowExpressionClause(fatArrowToken, body);
        }
        else
        {
            blockBody = new ExpressionSyntaxParser(
                this,
                parseAbruptTransfersInBlocksAsExpressions: false).ParseBlockSyntax();
            expressionBody = null;
        }

        lambda = ParenthesizedFunctionExpression(
            staticKeyword ?? Token(SyntaxKind.None),
            asyncKeyword ?? Token(SyntaxKind.None),
            funcKeyword ?? Token(SyntaxKind.None),
            lambdaIdentifier,
            typeParameterList,
            parameterList,
            returnType,
            constraintClauses,
            blockBody,
            expressionBody);

        return true;
    }

    private bool TryParseAttributedParenthesizedLambdaExpression(
        SyntaxToken? staticKeyword,
        SyntaxToken? asyncKeyword,
        out FunctionExpressionSyntax? lambda)
    {
        lambda = null;

        if (!AttributeDeclarationParser.IsAttributeListStart(this))
            return false;

        var checkpoint = CreateCheckpoint("attributed-parenthesized-lambda");

        var leadingAttributeLists = AttributeDeclarationParser.ParseAttributeLists(this);
        if (leadingAttributeLists.SlotCount == 0 || !PeekToken().IsKind(SyntaxKind.OpenParenToken))
        {
            checkpoint.Rewind();
            return false;
        }

        SplitLeadingLambdaAttributeLists(leadingAttributeLists, out var parameterAttributeLists, out var returnAttributeLists);

        var parameterList = new StatementSyntaxParser(this).ParseParameterList(
            allowDestructuringPatterns: true,
            allowDiscardParameters: true);
        if (parameterAttributeLists.SlotCount > 0 &&
            parameterList.Parameters.SlotCount > 0 &&
            parameterList.Parameters[0] is ParameterSyntax firstParameter)
        {
            var mergedAttributes = ConcatSyntaxLists(parameterAttributeLists, firstParameter.AttributeLists);
            var updatedFirstParameter = firstParameter.Update(
                mergedAttributes,
                firstParameter.OnKeyword,
                firstParameter.AccessibilityKeyword,
                firstParameter.ScopedKeyword,
                firstParameter.RefKindKeyword,
                firstParameter.ParamsKeyword,
                firstParameter.BindingKeyword,
                firstParameter.Identifier,
                firstParameter.Pattern,
                firstParameter.TypeAnnotation,
                firstParameter.DotDotDotToken,
                firstParameter.DefaultValue);

            var items = new GreenNode[parameterList.Parameters.SlotCount];
            for (var i = 0; i < items.Length; i++)
                items[i] = parameterList.Parameters[i];

            items[0] = updatedFirstParameter;

            parameterList = parameterList.Update(
                parameterList.OpenParenToken,
                List(items),
                parameterList.CloseParenToken);
        }

        var returnType = new TypeAnnotationClauseSyntaxParser(this).ParseReturnTypeAnnotation();
        if (returnType is not null && returnAttributeLists.SlotCount > 0)
        {
            var mergedReturnAttributes = ConcatSyntaxLists(returnAttributeLists, returnType.AttributeLists);
            returnType = returnType.Update(mergedReturnAttributes, returnType.ArrowToken, returnType.Type);
        }

        if ((returnType is null && !IsNextToken(SyntaxKind.FatArrowToken)) ||
            (returnType is null && returnAttributeLists.SlotCount > 0))
        {
            checkpoint.Rewind();
            return false;
        }

        ConsumeTokenOrMissing(SyntaxKind.FatArrowToken, out var fatArrowToken);

        var parsedBody = PeekToken().IsKind(SyntaxKind.OpenBraceToken)
            ? new ExpressionSyntaxParser(
                this,
                parseAbruptTransfersInBlocksAsExpressions: false).ParseBlockSyntax()
            : ParseRequiredLambdaExpressionBody();
        BlockSyntax? body;
        ArrowExpressionClauseSyntax? expressionBody;
        if (parsedBody is BlockSyntax block)
        {
            body = block;
            expressionBody = null;
        }
        else
        {
            body = null;
            expressionBody = ArrowExpressionClause(fatArrowToken, parsedBody);
        }

        lambda = ParenthesizedFunctionExpression(
            staticKeyword ?? Token(SyntaxKind.None),
            asyncKeyword ?? Token(SyntaxKind.None),
            funcKeyword: Token(SyntaxKind.None),
            identifier: Token(SyntaxKind.None),
            typeParameterList: null,
            parameterList,
            returnType,
            constraintClauses: SyntaxList.Empty,
            body,
            expressionBody);

        return true;
    }

    private static SyntaxList ConcatSyntaxLists(SyntaxList leading, SyntaxList trailing)
    {
        if (leading.SlotCount == 0)
            return trailing;

        if (trailing.SlotCount == 0)
            return leading;

        var merged = new GreenNode[leading.SlotCount + trailing.SlotCount];
        var index = 0;

        for (var i = 0; i < leading.SlotCount; i++)
            merged[index++] = leading[i];

        for (var i = 0; i < trailing.SlotCount; i++)
            merged[index++] = trailing[i];

        return List(merged);
    }

    private static void SplitLeadingLambdaAttributeLists(
        SyntaxList attributeLists,
        out SyntaxList parameterAttributes,
        out SyntaxList returnAttributes)
    {
        if (attributeLists.SlotCount == 0)
        {
            parameterAttributes = SyntaxList.Empty;
            returnAttributes = SyntaxList.Empty;
            return;
        }

        var parameterItems = new List<GreenNode>();
        var returnItems = new List<GreenNode>();

        for (var i = 0; i < attributeLists.SlotCount; i++)
        {
            if (attributeLists[i] is not AttributeListSyntax attributeList)
                continue;

            var isReturnTarget = attributeList.Target is { Identifier.Text: "return" };
            if (isReturnTarget)
                returnItems.Add(attributeList);
            else
                parameterItems.Add(attributeList);
        }

        parameterAttributes = parameterItems.Count == 0 ? SyntaxList.Empty : List(parameterItems);
        returnAttributes = returnItems.Count == 0 ? SyntaxList.Empty : List(returnItems);
    }

    private bool TryParseSimpleLambdaExpression(
        SyntaxToken? staticKeyword,
        SyntaxToken? asyncKeyword,
        SyntaxToken? funcKeyword,
        out FunctionExpressionSyntax? lambda)
    {
        lambda = null;

        var allowBlockBodyWithoutArrow = funcKeyword is { Kind: SyntaxKind.FuncKeyword };

        // Fast-path: don't speculate unless we can see a `=>` before newline/terminator.
        if (!allowBlockBodyWithoutArrow && !LooksLikeLambdaAhead(startOffset: 0))
            return false;

        var checkpoint = CreateCheckpoint("simple-lambda");

        var attributeLists = AttributeDeclarationParser.ParseAttributeLists(this);

        if (asyncKeyword is null && funcKeyword is null && ConsumeToken(SyntaxKind.AsyncKeyword, out var parsedAsync))
            asyncKeyword = parsedAsync;

        var scopedKeyword = Token(SyntaxKind.None);
        if (PeekToken().Kind == SyntaxKind.ScopedKeyword)
            scopedKeyword = ReadToken();

        var refKindKeyword = Token(SyntaxKind.None);
        if (ConsumeToken(SyntaxKind.RefKeyword, out var modifier)
            || ConsumeToken(SyntaxKind.OutKeyword, out modifier)
            || ConsumeToken(SyntaxKind.InKeyword, out modifier))
        {
            refKindKeyword = modifier;
        }

        var bindingKeyword = Token(SyntaxKind.None);
        if (ConsumeToken(SyntaxKind.LetKeyword, out var binding)
            || ConsumeToken(SyntaxKind.ValKeyword, out binding)
            || ConsumeToken(SyntaxKind.VarKeyword, out binding)
            || ConsumeToken(SyntaxKind.ConstKeyword, out binding))
        {
            bindingKeyword = binding;
        }

        if (!IsSimpleLambdaParameterStart(PeekToken()))
        {
            checkpoint.Rewind();
            return false;
        }

        var identifier = ReadSimpleLambdaParameterToken();

        var typeAnnotation = new TypeAnnotationClauseSyntaxParser(this).ParseTypeAnnotation();

        EqualsValueClauseSyntax? defaultValue = null;
        if (IsNextToken(SyntaxKind.EqualsToken, out _))
            defaultValue = new EqualsValueClauseSyntaxParser(this).Parse();

        var returnType = new TypeAnnotationClauseSyntaxParser(this).ParseReturnTypeAnnotation();

        if (returnType is null &&
            !IsNextToken(SyntaxKind.FatArrowToken) &&
            !(allowBlockBodyWithoutArrow && PeekToken().IsKind(SyntaxKind.OpenBraceToken)))
        {
            checkpoint.Rewind();
            return false;
        }
        BlockSyntax? blockBody;
        ArrowExpressionClauseSyntax? expressionBody;
        if (IsNextToken(SyntaxKind.FatArrowToken))
        {
            ConsumeTokenOrMissing(SyntaxKind.FatArrowToken, out var fatArrowToken);
            var body = PeekToken().IsKind(SyntaxKind.OpenBraceToken)
                ? new ExpressionSyntaxParser(
                    this,
                    parseAbruptTransfersInBlocksAsExpressions: false).ParseBlockSyntax()
                : ParseRequiredLambdaExpressionBody();
            blockBody = null;
            expressionBody = ArrowExpressionClause(fatArrowToken, body);
        }
        else
        {
            blockBody = new ExpressionSyntaxParser(
                this,
                parseAbruptTransfersInBlocksAsExpressions: false).ParseBlockSyntax();
            expressionBody = null;
        }

        var parameter = Parameter(attributeLists, Token(SyntaxKind.None), Token(SyntaxKind.None), scopedKeyword, refKindKeyword, Token(SyntaxKind.None), bindingKeyword, identifier, null, typeAnnotation, Token(SyntaxKind.None), defaultValue);

        lambda = SimpleFunctionExpression(
            staticKeyword ?? Token(SyntaxKind.None),
            asyncKeyword ?? Token(SyntaxKind.None),
            funcKeyword ?? Token(SyntaxKind.None),
            parameter,
            returnType,
            blockBody,
            expressionBody);

        return true;
    }

    private ExpressionSyntax ParseRequiredLambdaExpressionBody()
    {
        var body = new ExpressionSyntaxParser(this).ParseExpression();
        if (body is ExpressionSyntax.Missing)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.ExpressionExpected,
                    GetSpanOfPeekedToken()));
        }

        return body;
    }

    private static bool IsSimpleLambdaParameterStart(SyntaxToken token)
        => CanTokenBeIdentifier(token) || token.IsKind(SyntaxKind.UnderscoreToken);

    private SyntaxToken ReadSimpleLambdaParameterToken()
        => PeekToken().IsKind(SyntaxKind.UnderscoreToken)
            ? ReadToken()
            : ReadIdentifierToken();

    /// <summary>
    /// Parse a primary expression and trailers.
    /// </summary>
    /// <returns>An expression.</returns>
    private ExpressionSyntax ParsePowerExpression()
    {
        var start = Position;

        ExpressionSyntax expr = ParsePrimaryExpression();

        if (_stopAfterPrimaryExpression)
        {
            _stopAfterPrimaryExpression = false;
            return expr;
        }

        expr = AddTrailers(start, expr);

        return expr;
    }

    private ExpressionSyntax AddTrailers(int start, ExpressionSyntax expr)
    {
        while (true) // Loop to handle consecutive member access and invocations
        {
            var token = PeekToken();

            // In statement/condition contexts (if/while/for headers, if-expression condition),
            // `{` begins the following block expression/body and must not be consumed as an
            // object-initializer trailer.
            if (_stopOnOpenBrace && token.IsKind(SyntaxKind.OpenBraceToken))
                break;

            if (token.IsKind(SyntaxKind.OpenParenToken)) // Invocation
            {
                // Break if the '(' token has a leading newline.
                // This prevents: <expr> [newline] '('
                if (HasLeadingNewLine(token))
                    return expr;

                var argumentList = ParseArgumentListSyntax();

                // An object initializer may immediately follow an invocation: Foo(...) { ... }
                // But in statement/header contexts (for/if/while conditions), `{` begins the body block,
                // so we must not consume it as an initializer.
                ObjectInitializerExpressionSyntax? initializer = null;
                if (!_stopOnOpenBrace && PeekToken().IsKind(SyntaxKind.OpenBraceToken))
                    initializer = ParseObjectInitializerExpression();

                expr = InvocationExpression(expr, argumentList, initializer);
            }
            else if (token.IsKind(SyntaxKind.DotToken) || token.IsKind(SyntaxKind.ArrowToken)) // Member Access
            {
                if (TreatNewlinesAsTokens && HasLeadingNewLine(token))
                    return expr;

                // Keep fluent chains across a single newline (`a\n  .b()`), but treat a blank-line
                // separation as a new statement so target-typed member bindings like `.Ok` can start
                // the next expression.
                if (HasLeadingBlankLine(token))
                    return expr;

                var operatorToken = ReadToken();
                SimpleNameSyntax memberName;
                if (CanTokenBeIdentifier(PeekToken()))
                {
                    memberName = new NameSyntaxParser(this).ParseSimpleName();
                }
                else
                {
                    ConsumeTokenOrMissing(SyntaxKind.IdentifierToken, out var identifier);
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.IdentifierExpected,
                            GetEndOfLastToken()));
                    memberName = IdentifierName(identifier);
                }
                var memberAccessKind = operatorToken.Kind == SyntaxKind.ArrowToken
                    ? SyntaxKind.PointerMemberAccessExpression
                    : SyntaxKind.SimpleMemberAccessExpression;
                expr = MemberAccessExpression(memberAccessKind, expr, operatorToken, memberName);
            }
            else if (token.IsKind(SyntaxKind.OpenBracketToken)) // Element access
            {
                // Break if the '[' token has a leading newline.
                // This prevents: <expr> [newline] '['
                if (HasLeadingNewLine(token))
                    return expr;
                var argumentList = ParseBracketedArgumentListSyntax();

                expr = ElementAccessExpression(expr, argumentList);
            }
            else if (token.IsKind(SyntaxKind.OpenBraceToken)) // Object initializer trailer
            {
                // A newline before '{' starts a new statement-level block scope.
                // Keep object initializers as same-line syntax.
                if (HasLeadingNewLine(token))
                    return expr;

                var initializer = ParseObjectInitializerExpression();

                if (expr is InvocationExpressionSyntax inv)
                {
                    expr = InvocationExpression(inv.Expression, inv.ArgumentList, initializer);
                }
                else
                {
                    var missingArgs = CreateMissingArgumentList();
                    expr = InvocationExpression(expr, missingArgs, initializer);
                }
            }
            else if (token.IsKind(SyntaxKind.WithKeyword)) // With-expression trailer: `<expr> with { ... }`
            {
                // Break if the `with` token has a leading newline.
                // This prevents: <expr> [newline] with { ... }
                if (HasLeadingNewLine(token))
                    return expr;

                expr = ParseWithExpression(expr);
            }
            else if (token.IsKind(SyntaxKind.QuestionToken)) // Conditional access OR propagate (`<expr>?`)
            {
                var operatorToken = ReadToken();
                var next = PeekToken();

                // Conditional access only when followed by one of the conditional-access trailers.
                // Otherwise, treat `?` as the Result-propagation postfix operator.
                if (!next.IsKind(SyntaxKind.DotToken)
                    && !next.IsKind(SyntaxKind.OpenParenToken)
                    && !next.IsKind(SyntaxKind.OpenBracketToken))
                {
                    expr = PropagateExpression(expr, operatorToken);
                    continue;
                }

                ExpressionSyntax whenNotNull;
                if (next.IsKind(SyntaxKind.DotToken))
                {
                    var dotToken = ReadToken();
                    SimpleNameSyntax memberName;
                    if (CanTokenBeIdentifier(PeekToken()))
                    {
                        memberName = new NameSyntaxParser(this).ParseSimpleName();
                    }
                    else
                    {
                        ConsumeTokenOrMissing(SyntaxKind.IdentifierToken, out var identifier);
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.IdentifierExpected,
                                GetEndOfLastToken()));
                        memberName = IdentifierName(identifier);
                    }

                    var memberBinding = MemberBindingExpression(dotToken, memberName);
                    if (PeekToken().IsKind(SyntaxKind.OpenParenToken))
                    {
                        var argumentList = ParseArgumentListSyntax();
                        whenNotNull = InvocationExpression(memberBinding, argumentList);
                    }
                    else
                    {
                        whenNotNull = memberBinding;
                    }
                }
                else if (next.IsKind(SyntaxKind.OpenParenToken))
                {
                    var argumentList = ParseArgumentListSyntax();
                    var receiverBinding = ReceiverBindingExpression(Token(SyntaxKind.None));
                    whenNotNull = InvocationExpression(receiverBinding, argumentList);
                }
                else if (next.IsKind(SyntaxKind.OpenBracketToken))
                {
                    var argumentList = ParseBracketedArgumentListSyntax();
                    whenNotNull = ElementBindingExpression(argumentList);
                }
                else
                {
                    ConsumeTokenOrMissing(SyntaxKind.DotToken, out var missingDot);
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.IdentifierExpected,
                            GetEndOfLastToken()));
                    var missingName = IdentifierName(SyntaxFactory.MissingToken(SyntaxKind.IdentifierToken));
                    whenNotNull = MemberBindingExpression(missingDot, missingName);
                }

                expr = ConditionalAccessExpression(expr, operatorToken, whenNotNull);
            }
            else if (token.IsKind(SyntaxKind.PlusPlusToken)) // Post-increment
            {
                var operatorToken = ReadToken();
                expr = PostfixOperatorExpression(SyntaxKind.PostIncrementExpression, expr, operatorToken);
            }
            else if (token.IsKind(SyntaxKind.MinusMinusToken)) // Post-decrement
            {
                var operatorToken = ReadToken();
                expr = PostfixOperatorExpression(SyntaxKind.PostDecrementExpression, expr, operatorToken);
            }
            else if (token.IsKind(SyntaxKind.ExclamationToken)) // Null-forgiving / nullable escape hatch
            {
                if (HasLeadingNewLine(token))
                    return expr;

                var operatorToken = ReadToken();
                expr = PostfixOperatorExpression(SyntaxKind.SuppressNullableWarningExpression, expr, operatorToken);
            }
            else
            {
                // No more trailers, break out of the loop
                break;
            }
        }

        return expr;
    }

    internal ArgumentListSyntax ParseArgumentListSyntax(bool allowLegacyNamedArgumentEquals = true)
    {
        // We assume current token is '('
        var openParenToken = ReadToken();

        var argumentList = new List<GreenNode>();
        var seenNames = new HashSet<string>();
        int parsedArgs = 0;

        // Inside a parenthesized argument list, newlines should behave like trivia.
        // Save & restore whatever backing state you use for this.
        var restoreNewlinesAsTokens = TreatNewlinesAsTokens; // or a property getter if you have one
        SetTreatNewlinesAsTokens(false);

        SyntaxToken closeParenToken;

        try
        {
            while (true)
            {
                var t = PeekToken();

                while (IsNewLineLike(t))
                {
                    ReadToken();
                    t = PeekToken();
                }

                // End of argument list
                if (t.IsKind(SyntaxKind.EndOfFileToken) ||
                    t.IsKind(SyntaxKind.CloseParenToken) ||
                    t.IsKind(SyntaxKind.CloseBraceToken))
                {
                    break;
                }

                // After the first argument, we expect a comma before the next one.
                if (parsedArgs > 0)
                {
                    t = PeekToken();

                    if (t.IsKind(SyntaxKind.CommaToken))
                    {
                        var commaToken = ReadToken();
                        argumentList.Add(commaToken);
                    }
                    else
                    {
                        argumentList.Add(MissingToken(SyntaxKind.CommaToken));
                        // Newlines are trivia here, so if we see *anything* other than a comma
                        // we complain about a missing ',' but still try to parse the next arg.
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.CharacterExpected,
                                GetSpanOfPeekedToken(),
                                ","));
                    }
                }

                // Parse the argument expression itself.
                // Newlines are currently *not* tokens at this level, so examples like:
                //
                //   Foo(
                //      42)
                //
                // and
                //
                //   Foo(a,
                //      42)
                //
                // just see the newlines as trivia around '42'.
                var argumentStart = Position;
                var arg = new ExpressionSyntaxParser(this).ParseArgument(allowLegacyNamedArgumentEquals, out var nameSpan);

                if (arg is null or { IsMissing: true })
                {
                    var peekedToken = PeekToken();
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.InvalidExpressionTerm,
                            GetSpanOfPeekedToken(),
                            peekedToken.Text));
                }
                else if (arg.NameColon is { } nameColon)
                {
                    var name = nameColon.Name.Identifier.GetValueText();
                    if (!seenNames.Add(name))
                    {
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.DuplicateNamedArgument,
                                nameSpan ?? GetSpanOfLastToken(),
                                name));
                    }

                    if (!allowLegacyNamedArgumentEquals &&
                        (nameColon.ColonToken.IsMissing || nameColon.ColonToken.Kind != SyntaxKind.ColonToken))
                    {
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.CharacterExpected,
                                GetSpanOfLastToken(),
                                ":"));
                    }
                }

                if (ShouldStopArgumentListRecoveryAfter(arg))
                    break;

                if (Position == argumentStart)
                {
                    var token = PeekToken();
                    var tokenText = string.IsNullOrEmpty(token.Text)
                        ? token.Kind.ToString()
                        : token.Text;

                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                            GetSpanOfPeekedToken(),
                            tokenText));

                    if (token.IsKind(SyntaxKind.CloseParenToken) || token.IsKind(SyntaxKind.EndOfFileToken))
                        break;

                    ReadToken();
                    continue;
                }

                argumentList.Add(arg);
                parsedArgs++;
            }

            ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out closeParenToken);
        }
        finally
        {
            // Make sure we put the global newline behavior back
            SetTreatNewlinesAsTokens(restoreNewlinesAsTokens);
        }

        if (closeParenToken.IsMissing)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.CharacterExpected,
                    GetSpanOfLastToken(),
                    ")"));
        }

        return ArgumentList(openParenToken, List(argumentList.ToArray()), closeParenToken);
    }

    private bool ShouldStopArgumentListRecoveryAfter(ArgumentSyntax argument)
    {
        return IsUnterminatedSingleLineStringExpression(argument.Expression);
    }

    public ArgumentSyntax ParseArgument()
    {
        return ParseArgument(allowLegacyNamedArgumentEquals: true, out _);
    }

    public ArgumentSyntax ParseArgument(out TextSpan? nameSpan)
    {
        return ParseArgument(allowLegacyNamedArgumentEquals: true, out nameSpan);
    }

    public ArgumentSyntax ParseArgument(bool allowLegacyNamedArgumentEquals, out TextSpan? nameSpan)
    {
        var refKindKeyword = Token(SyntaxKind.None);
        var bindingKeyword = Token(SyntaxKind.None);
        NameColonSyntax? nameColon = null;
        nameSpan = null;
        var dotDotDotToken = Token(SyntaxKind.None);

        if (PeekToken().Kind is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
        {
            var next = PeekToken(1);
            if (!next.IsKind(SyntaxKind.ColonToken) && !next.IsKind(SyntaxKind.EqualsToken))
                refKindKeyword = ReadToken();
        }

        if (refKindKeyword.Kind == SyntaxKind.OutKeyword &&
            PeekToken().Kind is SyntaxKind.VarKeyword or SyntaxKind.ValKeyword or SyntaxKind.LetKeyword)
        {
            bindingKeyword = ReadToken();
        }

        // Try to parse optional name:
        if (PeekToken(1).IsKind(SyntaxKind.ColonToken)
            && CanTokenBeIdentifier(PeekToken()))
        {
            var name = ReadToken(); // identifier or keyword
            if (name.Kind != SyntaxKind.IdentifierToken)
            {
                name = ToIdentifierToken(name);
                UpdateLastToken(name);
            }
            nameSpan = GetSpanOfLastToken();
            var colon = ReadToken(); // colon
            nameColon = NameColon(IdentifierName(name), colon);
        }
        else if (PeekToken(1).IsKind(SyntaxKind.EqualsToken)
           && CanTokenBeIdentifier(PeekToken()))
        {
            var name = ReadToken(); // identifier or keyword
            if (name.Kind != SyntaxKind.IdentifierToken)
            {
                name = ToIdentifierToken(name);
                UpdateLastToken(name);
            }
            nameSpan = GetSpanOfLastToken();
            var equals = ReadToken();

            if (allowLegacyNamedArgumentEquals)
            {
                // Backward compatibility for regular invocation argument lists.
                // Attribute argument lists intentionally reject this form.
                nameColon = NameColon(IdentifierName(name), equals);
            }
            else
            {
                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.CharacterExpected,
                        GetSpanOfLastToken(),
                        ":"));

                nameColon = NameColon(IdentifierName(name), MissingToken(SyntaxKind.ColonToken));
            }
        }

        if (PeekToken().IsKind(SyntaxKind.DotDotDotToken))
            dotDotDotToken = ReadToken();

        ExpressionSyntax expr;
        if (bindingKeyword.Kind != SyntaxKind.None && CanTokenBeIdentifier(PeekToken()))
        {
            expr = IdentifierName(ReadIdentifierToken());
        }
        else
        {
            expr = ParseExpression();
        }

        return Argument(refKindKeyword, bindingKeyword, nameColon, dotDotDotToken, expr);
    }

    private BracketedArgumentListSyntax ParseBracketedArgumentListSyntax()
    {
        var openBracketToken = ReadToken();

        List<GreenNode> argumentList = new List<GreenNode>();

        var parsedArgs = 0;
        var restoreNewlinesAsTokens = TreatNewlinesAsTokens;
        SetTreatNewlinesAsTokens(false);

        SyntaxToken closeBracketToken;

        try
        {
            while (true)
            {
                var t = PeekToken();

                while (IsNewLineLike(t))
                {
                    ReadToken();
                    t = PeekToken();
                }

                if (t.IsKind(SyntaxKind.EndOfFileToken) ||
                    t.IsKind(SyntaxKind.CloseBracketToken))
                {
                    break;
                }

                if (parsedArgs > 0)
                {
                    if (t.IsKind(SyntaxKind.CommaToken))
                    {
                        var commaToken = ReadToken();
                        argumentList.Add(commaToken);
                    }
                    else
                    {
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.CharacterExpected,
                                GetSpanOfLastToken(),
                                ","));
                    }
                }

                var argumentStart = Position;
                var argument = new ExpressionSyntaxParser(this).ParseArgument();

                if (argument is null or { IsMissing: true })
                {
                    var peekedToken = PeekToken();
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.InvalidExpressionTerm,
                            GetSpanOfPeekedToken(),
                            peekedToken.Text));
                }

                if (Position == argumentStart)
                {
                    var token = PeekToken();
                    var tokenText = string.IsNullOrEmpty(token.Text)
                        ? token.Kind.ToString()
                        : token.Text;

                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                            GetSpanOfPeekedToken(),
                            tokenText));

                    if (token.IsKind(SyntaxKind.CloseBracketToken) || token.IsKind(SyntaxKind.EndOfFileToken))
                        break;

                    ReadToken();
                    continue;
                }

                argumentList.Add(argument);
                parsedArgs++;
            }

            ConsumeTokenOrMissing(SyntaxKind.CloseBracketToken, out closeBracketToken);
        }
        finally
        {
            SetTreatNewlinesAsTokens(restoreNewlinesAsTokens);
        }

        if (closeBracketToken.IsMissing)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.CharacterExpected,
                    GetSpanOfLastToken(),
                    "]"));
        }

        return BracketedArgumentList(openBracketToken, List(argumentList.ToArray()), closeBracketToken);
    }

    private static bool IsNewLineLike(SyntaxKind kind)
    {
        return kind is SyntaxKind.LineFeedToken
            or SyntaxKind.CarriageReturnToken
            or SyntaxKind.CarriageReturnLineFeedToken
            or SyntaxKind.EndOfLineTrivia;
    }

    private static bool IsNewLineLike(SyntaxToken token)
    {
        return IsNewLineLike(token.Kind);
    }

    private bool HasLeadingNewLine(SyntaxToken token)
    {
        return CountLineBreaksBefore(token) > 0;
    }

    private static bool TokenHasLeadingNewLine(SyntaxToken token)
    {
        foreach (var trivia in token.LeadingTrivia)
        {
            if (IsNewLineLike(trivia.Kind))
                return true;
        }

        return false;
    }

    private bool HasLeadingBlankLine(SyntaxToken token)
    {
        return CountLineBreaksBefore(token) >= 2;
    }

    private int CountLineBreaksBefore(SyntaxToken token)
    {
        var count = 0;

        SyntaxToken lastToken = default;
        try
        {
            lastToken = LastToken;
        }
        catch (InvalidOperationException)
        {
            lastToken = default;
        }

        if (lastToken != default)
        {
            if (IsNewLineLike(lastToken))
                count++;

            foreach (var trivia in lastToken.TrailingTrivia)
            {
                if (IsNewLineLike(trivia.Kind))
                    count++;
            }
        }

        foreach (var trivia in token.LeadingTrivia)
        {
            if (IsNewLineLike(trivia.Kind))
                count++;
        }

        return count;
    }

    private bool IsLeadingDereferenceLikeStar(SyntaxToken token)
    {
        if (!token.IsKind(SyntaxKind.StarToken))
            return false;

        if (!HasLeadingNewLine(token))
            return false;

        // Require immediate `*identifier` adjacency: no trivia between the tokens.
        if (token.TrailingTrivia.Width != 0)
            return false;

        var next = PeekToken(1);
        if (!CanTokenBeIdentifier(next))
            return false;

        return next.LeadingTrivia.Width == 0;
    }

    private TupleExpressionSyntax ParseTupleExpressionSyntax()
    {
        var openParenToken = ReadToken();

        List<GreenNode> argumentList = new List<GreenNode>();

        while (true)
        {
            var t = PeekToken();

            if (t.IsKind(SyntaxKind.CloseParenToken) || t.IsKind(SyntaxKind.EndOfFileToken))
                break;

            var elementStart = Position;
            var expression = new ExpressionSyntaxParser(this).ParseExpression();
            if (expression is null)
                break;

            argumentList.Add(Argument(Token(SyntaxKind.None), Token(SyntaxKind.None), null, Token(SyntaxKind.None), expression));

            var commaToken = PeekToken();
            if (commaToken.IsKind(SyntaxKind.CommaToken))
            {
                ReadToken();
                argumentList.Add(commaToken);
            }

            if (Position == elementStart)
            {
                var current = PeekToken();
                var tokenText = string.IsNullOrEmpty(current.Text)
                    ? current.Kind.ToString()
                    : current.Text;

                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                        GetSpanOfPeekedToken(),
                        tokenText));

                if (current.IsKind(SyntaxKind.CloseParenToken) || current.IsKind(SyntaxKind.EndOfFileToken))
                    break;

                ReadToken();
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return TupleExpression(openParenToken, List(argumentList.ToArray()), closeParenToken);
    }

    /// <summary>
    /// Parse a primary expression.
    /// </summary>
    /// <returns>An expression.</returns>
    private ExpressionSyntax ParsePrimaryExpression()
    {
        ExpressionSyntax expr = null;

        SyntaxToken token;

        token = PeekToken();

        // Allow expressions to continue on the next line. Previously any token
        // with leading end-of-line trivia was treated as a missing identifier,
        // causing line continuations like `let x =\n    42` to fail with a
        // diagnostic. By removing this check the parser treats the newline as
        // trivia and correctly parses the following expression token.

        if (IsFreestandingMacroExpressionStart())
            return ParseFreestandingMacroExpression();

        if (CanTokenBeIdentifier(token))
        {
            if (IsInMacro && IsMacroExpansionExpressionKeyword(token))
                return ParseMacroExpansionExpression();

            return new NameSyntaxParser(this).ParseSimpleName();
        }

        switch (token.Kind)
        {
            case SyntaxKind.StringKeyword:
            case SyntaxKind.BoolKeyword:
            case SyntaxKind.CharKeyword:
            case SyntaxKind.SByteKeyword:
            case SyntaxKind.ShortKeyword:
            case SyntaxKind.UShortKeyword:
            case SyntaxKind.IntKeyword:
            case SyntaxKind.UIntKeyword:
            case SyntaxKind.LongKeyword:
            case SyntaxKind.ULongKeyword:
            case SyntaxKind.FloatKeyword:
            case SyntaxKind.DoubleKeyword:
            case SyntaxKind.DecimalKeyword:
            case SyntaxKind.ByteKeyword:
            case SyntaxKind.NIntKeyword:
            case SyntaxKind.NUIntKeyword:
            case SyntaxKind.ObjectKeyword:
            case SyntaxKind.UnitKeyword:
                return ParsePredefinedTypeSyntax();

            case SyntaxKind.SelfKeyword:
                ReadToken();
                return SelfExpression(token);

            case SyntaxKind.BaseKeyword:
                ReadToken();
                return BaseExpression(token);

            case SyntaxKind.TrueKeyword:
                ReadToken();
                expr = LiteralExpression(SyntaxKind.TrueLiteralExpression, token);
                break;

            case SyntaxKind.FalseKeyword:
                ReadToken();
                expr = LiteralExpression(SyntaxKind.FalseLiteralExpression, token);
                break;

            case SyntaxKind.NumericLiteralToken:
                ReadToken();
                expr = LiteralExpression(SyntaxKind.NumericLiteralExpression, token);
                break;

            case SyntaxKind.StringLiteralToken:

                ReadToken();
                var isUnterminatedSingleLineString = IsUnterminatedSingleLineStringToken(token);
                var tokenText = token.Text;
                var inner = GetSingleLineStringInnerText(tokenText, isUnterminatedSingleLineString);
                var hasInterpolation = ContainsInterpolation(inner);
                if (TryReadEncodedStringSuffix(token, out var suffixToken, out var encoding))
                {
                    var encodedToken = CreateEncodedStringLiteralToken(token, suffixToken, encoding, hasInterpolation);
                    expr = LiteralExpression(SyntaxKind.StringLiteralExpression, encodedToken);
                    break;
                }

                expr = hasInterpolation
                    ? ParseInterpolatedStringExpression(token)
                    : LiteralExpression(SyntaxKind.StringLiteralExpression, token);

                if (isUnterminatedSingleLineString)
                    _stopAfterPrimaryExpression = true;
                break;

            case SyntaxKind.MultiLineStringLiteralToken:
                // Use raw token text to preserve correct spans (Value may already be indentation‑trimmed)
                ReadToken();
                var multiInner = token.Text.Length >= 6
                    ? token.Text.Substring(3, token.Text.Length - 6)
                    : string.Empty;
                var hasMultiInterpolation = ContainsInterpolation(multiInner);
                if (TryReadEncodedStringSuffix(token, out var multiSuffixToken, out var multiEncoding))
                {
                    var encodedToken = CreateEncodedStringLiteralToken(token, multiSuffixToken, multiEncoding, hasMultiInterpolation);
                    expr = LiteralExpression(SyntaxKind.StringLiteralExpression, encodedToken);
                    break;
                }

                expr = hasMultiInterpolation
                    ? ParseInterpolatedMultiLineStringExpression(token, multiInner)
                    : LiteralExpression(SyntaxKind.StringLiteralExpression, token);
                break;

            case SyntaxKind.CharacterLiteralToken:
                ReadToken();
                expr = LiteralExpression(SyntaxKind.CharacterLiteralExpression, token);
                break;

            case SyntaxKind.NullKeyword:
                ReadToken();
                expr = LiteralExpression(SyntaxKind.NullLiteralExpression, token);
                break;

            case SyntaxKind.UnderscoreToken:
                ReadToken();
                expr = DiscardExpression(token);
                break;

            case SyntaxKind.OpenParenToken:
                expr = ParseParenthesisOrTupleExpression();
                break;

            case SyntaxKind.DefaultKeyword:
                expr = ParseDefaultExpression();
                break;

            case SyntaxKind.TypeOfKeyword:
                expr = ParseTypeOfExpression();
                break;

            case SyntaxKind.SizeOfKeyword:
                expr = ParseSizeOfExpression();
                break;

            case SyntaxKind.NameOfKeyword:
                expr = ParseNameOfExpression();
                break;

            case SyntaxKind.OpenBracketToken:
                expr = ParseCollectionExpression();
                break;

            case SyntaxKind.OpenArrayToken:
                expr = ParseArrayExpression();
                break;

            case SyntaxKind.DotToken:
                {
                    var dot = ReadToken();
                    var name = new NameSyntaxParser(this).ParseSimpleName();
                    expr = MemberBindingExpression(dot, name);
                    break;
                }
        }

        if (expr is null
            && SyntaxFacts.IsKeywordKind(token.Kind)
            && !SyntaxFacts.IsReservedWordKind(token.Kind))
        {
            var identifier = ReadToken();
            expr = IdentifierName(identifier);
        }

        return expr ?? new ExpressionSyntax.Missing();
    }

    private static bool IsMacroExpansionExpressionKeyword(SyntaxToken token)
        => token.IsKind(SyntaxKind.IdentifierToken) &&
           token.GetValueText() is "expand" or "replace" or "introduce";

    private MacroExpansionExpressionSyntax ParseMacroExpansionExpression()
    {
        var keyword = ReadToken();
        var expression = new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false).ParseExpression();
        return MacroExpansionExpression(keyword, expression);
    }

    private ExpressionSyntax ParseParenthesisOrTupleExpression()
    {
        var openParenToken = ReadToken(); // Consumes '('

        if (PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            var close = ReadToken();
            return UnitExpression(openParenToken, close);
        }

        // Try to parse as a cast expression
        var checkpoint = CreateCheckpoint();
        var typeName = new NameSyntaxParser(this).ParseTypeName();
        if (PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            var closeParen = ReadToken();
            var next = PeekToken();
            if (!next.IsKind(SyntaxKind.CommaToken) &&
                !next.IsKind(SyntaxKind.CloseParenToken) &&
                !next.IsKind(SyntaxKind.SemicolonToken) &&
                !next.IsKind(SyntaxKind.EndOfFileToken) &&
                !next.IsKind(SyntaxKind.OpenBraceToken))
            {
                var expression = ParseFactorExpression();
                return CastExpression(openParenToken, typeName, closeParen, expression);
            }
        }
        checkpoint.Rewind();

        var expressions = new List<GreenNode>();

        var firstArg = new ExpressionSyntaxParser(this).ParseArgument();
        expressions.Add(firstArg);

        bool sawComma = false;

        while (PeekToken().IsKind(SyntaxKind.CommaToken))
        {
            sawComma = true;
            expressions.Add(ReadToken()); // Consume comma
            var argument = new ExpressionSyntaxParser(this).ParseArgument();
            expressions.Add(argument);
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        if (sawComma)
        {
            // This was a tuple
            return TupleExpression(openParenToken, List(expressions.ToArray()), closeParenToken);
        }
        else
        {
            // Just a parenthesized expression
            return ParenthesizedExpression(openParenToken, firstArg.Expression, closeParenToken);
        }
    }

    private ExpressionSyntax ParseCollectionExpression()
    {
        ConsumeTokenOrNone(SyntaxKind.ExclamationToken, out var exclamationToken);
        var openBracketToken = ReadToken();
        var elementList = ParseCollectionElements(SyntaxKind.CloseBracketToken);
        ConsumeTokenOrMissing(SyntaxKind.CloseBracketToken, out var closeBracketToken);

        return CollectionExpression(exclamationToken, openBracketToken, List(elementList), closeBracketToken);
    }

    private ExpressionSyntax ParseArrayExpression()
    {
        var openArrayToken = ReadToken();
        var elementList = ParseCollectionElements(SyntaxKind.CloseArrayToken);
        ConsumeTokenOrMissing(SyntaxKind.CloseArrayToken, out var closeArrayToken);

        return ArrayExpression(openArrayToken, List(elementList), closeArrayToken);
    }

    private List<GreenNode> ParseCollectionElements(SyntaxKind closeTokenKind)
    {
        List<GreenNode> elementList = new List<GreenNode>();
        var parsedElements = 0;

        while (true)
        {
            if (parsedElements > 0)
            {
                if (TryConsumeCollectionElementSeparator(closeTokenKind, out var separatorToken, out var invalidSeparatorToken))
                {
                    elementList.Add(separatorToken);

                    if (invalidSeparatorToken is not null)
                    {
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.CharacterExpected,
                                GetSpanOfLastToken(),
                                ","));
                    }
                }
                else if (!PeekToken().IsKind(closeTokenKind) &&
                         !PeekToken().IsKind(SyntaxKind.EndOfFileToken))
                {
                    elementList.Add(MissingToken(SyntaxKind.CommaToken));
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.CharacterExpected,
                            GetSpanOfPeekedToken(),
                            ","));
                }
            }

            var t = PeekToken();

            if (t.IsKind(closeTokenKind) || t.IsKind(SyntaxKind.EndOfFileToken))
                break;

            CollectionElementSyntax element;
            var elementStart = Position;

            if (t.IsKind(SyntaxKind.ForKeyword))
            {
                element = ParseCollectionComprehensionElement();
            }
            else if (ConsumeToken(SyntaxKind.DotDotDotToken, out var dotDotToken))
            {
                var spreadExpr = new ExpressionSyntaxParser(this).ParseExpression();
                if (spreadExpr is null)
                    break;

                if (ConsumeToken(SyntaxKind.ColonToken, out var colonToken))
                {
                    var valueExpression = new ExpressionSyntaxParser(this).ParseExpression();
                    if (valueExpression is null)
                        break;

                    element = DictionarySpreadElement(dotDotToken, spreadExpr, colonToken, valueExpression);
                }
                else
                {
                    element = SpreadElement(dotDotToken, spreadExpr);
                }
            }
            else
            {
                var keyOrExpression = new ExpressionSyntaxParser(this).ParseExpression();
                if (keyOrExpression is null)
                    break;

                if (ConsumeToken(SyntaxKind.ColonToken, out var colonToken))
                {
                    var valueExpression = new ExpressionSyntaxParser(this).ParseExpression();
                    if (valueExpression is null)
                        break;

                    element = DictionaryElement(keyOrExpression, colonToken, valueExpression);
                }
                else
                {
                    element = ExpressionElement(keyOrExpression);
                }
            }

            elementList.Add(element);
            parsedElements++;

            if (Position == elementStart)
            {
                var current = PeekToken();
                var tokenText = string.IsNullOrEmpty(current.Text)
                    ? current.Kind.ToString()
                    : current.Text;

                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                        GetSpanOfPeekedToken(),
                        tokenText));

                if (current.IsKind(closeTokenKind) || current.IsKind(SyntaxKind.EndOfFileToken))
                    break;

                ReadToken();
            }
        }

        return elementList;
    }

    private bool TryConsumeCollectionElementSeparator(
        SyntaxKind closingKind,
        out SyntaxToken separatorToken,
        out SyntaxToken? invalidSeparatorToken)
    {
        var current = PeekToken();

        if (current.IsKind(SyntaxKind.CommaToken))
        {
            invalidSeparatorToken = null;
            separatorToken = ReadToken();
            return true;
        }

        if (HasLineBreakBeforePeekToken())
        {
            invalidSeparatorToken = null;
            separatorToken = Token(SyntaxKind.None);
            return true;
        }

        if (current.IsKind(SyntaxKind.SemicolonToken))
        {
            invalidSeparatorToken = ReadToken();
            separatorToken = invalidSeparatorToken;
            return true;
        }

        if (current.IsKind(closingKind) || current.IsKind(SyntaxKind.EndOfFileToken))
        {
            invalidSeparatorToken = null;
            separatorToken = Token(SyntaxKind.None);
            return false;
        }

        invalidSeparatorToken = null;
        separatorToken = Token(SyntaxKind.None);
        return false;
    }

    private CollectionElementSyntax ParseCollectionComprehensionElement()
    {
        var forKeyword = ReadToken();
        var bindingKeyword = Token(SyntaxKind.None);

        if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            bindingKeyword = ReadToken();

        ExpressionOrPatternSyntax target;
        var current = PeekToken();
        if (current.Kind is SyntaxKind.UnderscoreToken)
        {
            target = DiscardPattern(ReadToken());
        }
        else if (CanTokenBeIdentifier(current) && PeekToken(1).Kind is SyntaxKind.InKeyword)
        {
            target = IdentifierName(ReadIdentifierToken());
        }
        else
        {
            target = new PatternSyntaxParser(
                this,
                allowImplicitDeconstructionElementBindings: bindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                .ParsePattern();
        }

        ConsumeTokenOrMissing(SyntaxKind.InKeyword, out var inKeyword);
        var source = new ExpressionSyntaxParser(this, allowLambdaExpressions: false).ParseExpression();

        var ifKeyword = Token(SyntaxKind.None);
        ExpressionSyntax? condition = null;
        if (ConsumeToken(SyntaxKind.IfKeyword, out var parsedIfKeyword))
        {
            ifKeyword = parsedIfKeyword;
            condition = new ExpressionSyntaxParser(this, allowLambdaExpressions: false).ParseExpression();
        }

        ConsumeTokenOrMissing(SyntaxKind.FatArrowToken, out var fatArrowToken);
        var keyOrSelector = new ExpressionSyntaxParser(this).ParseExpression();

        if (ConsumeToken(SyntaxKind.ColonToken, out var colonToken))
        {
            var valueSelector = new ExpressionSyntaxParser(this).ParseExpression();
            return DictionaryComprehensionElement(forKeyword, bindingKeyword, target, inKeyword, source, ifKeyword, condition, fatArrowToken, keyOrSelector, colonToken, valueSelector);
        }

        return CollectionComprehensionElement(forKeyword, bindingKeyword, target, inKeyword, source, ifKeyword, condition, fatArrowToken, keyOrSelector);
    }

    private ExpressionSyntax ParseDefaultExpression()
    {
        var defaultKeyword = ReadToken();

        if (PeekToken().IsKind(SyntaxKind.OpenParenToken))
        {
            var openParenToken = ReadToken();
            var type = new NameSyntaxParser(this).ParseTypeName();
            ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

            return DefaultExpression(defaultKeyword, openParenToken, type, closeParenToken);
        }

        var missingOpenParen = MissingToken(SyntaxKind.OpenParenToken);
        var missingCloseParen = MissingToken(SyntaxKind.CloseParenToken);

        return DefaultExpression(defaultKeyword, missingOpenParen, null, missingCloseParen);
    }

    private ExpressionSyntax ParseTypeOfExpression()
    {
        var typeOfKeyword = ReadToken();

        ConsumeTokenOrMissing(SyntaxKind.OpenParenToken, out var openParenToken);

        // typeof supports open generic syntax like typeof(Dictionary<,>).
        var type = new NameSyntaxParser(this, allowOmittedTypeArguments: true).ParseTypeName();

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return TypeOfExpression(typeOfKeyword, openParenToken, type, closeParenToken);
    }

    private ExpressionSyntax ParseSizeOfExpression()
    {
        var sizeOfKeyword = ReadToken();

        ConsumeTokenOrMissing(SyntaxKind.OpenParenToken, out var openParenToken);
        var type = new NameSyntaxParser(this, allowOmittedTypeArguments: true).ParseTypeName();
        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return SizeOfExpression(sizeOfKeyword, openParenToken, type, closeParenToken);
    }

    private ExpressionSyntax ParseNameOfExpression()
    {
        var nameOfKeyword = ReadToken();

        ConsumeTokenOrMissing(SyntaxKind.OpenParenToken, out var openParenToken);

        // `nameof` accepts a restricted operand syntax. We prefer parsing a type name first
        // so `nameof(List<int>)` doesn't get mis-parsed as a comparison expression.
        ExpressionSyntax operand;

        var checkpoint = CreateCheckpoint("nameof-operand");
        var type = new NameSyntaxParser(this).ParseTypeName();

        if (!type.IsMissing && PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            operand = type;
        }
        else
        {
            checkpoint.Rewind();
            operand = ParseNameOfOperandExpression();
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return NameOfExpression(nameOfKeyword, openParenToken, operand, closeParenToken);
    }

    private ExpressionSyntax ParseNameOfOperandExpression()
    {
        // Allowed syntactic forms (validated further by the binder):
        //   Identifier
        //   MemberAccessExpression (a.b)
        //   MemberBindingExpression (.b)
        //   QualifiedName (parsed as member access in expression context)
        //
        // We intentionally do not treat invocations, element access, literals, etc. as valid operands.

        var start = Position;

        ExpressionSyntax expr;

        var token = PeekToken();
        if (token.IsKind(SyntaxKind.DotToken))
        {
            // `.WriteLine`
            var dot = ReadToken();
            var name = new NameSyntaxParser(this).ParseSimpleName();
            expr = MemberBindingExpression(dot, name);
        }
        else
        {
            // Parse a normal expression, but we'll validate it below.
            expr = new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false).ParseExpression();
        }

        // If the parsed expression is not a name-like expression, report a diagnostic.
        if (expr is not IdentifierNameSyntax
            && expr is not MemberAccessExpressionSyntax
            && expr is not MemberBindingExpressionSyntax)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.IdentifierExpected,
                    GetActualTextSpan(start, expr)));
        }

        return expr;
    }

    private bool IsFreestandingMacroExpressionStart()
        => new MacroInvocationSyntaxParser(this).IsBangInvocationStart(allowExpressionHeader: true);

    private ExpressionSyntax ParseFreestandingMacroExpression()
        => new MacroInvocationSyntaxParser(this).ParseExpression();

    private bool TryReadEncodedStringSuffix(
        SyntaxToken stringLiteralToken,
        out SyntaxToken suffixToken,
        out EncodedStringLiteralEncoding encoding)
    {
        suffixToken = null!;
        encoding = default;

        if (stringLiteralToken.TrailingTrivia.Width != 0)
            return false;

        suffixToken = PeekToken();
        if (!suffixToken.IsKind(SyntaxKind.IdentifierToken) || suffixToken.LeadingTrivia.Width != 0)
            return false;

        var suffixText = suffixToken.GetValueText();
        var recognized = suffixText switch
        {
            "u8" => true,
            "ascii" => true,
            _ => false
        };

        if (!recognized)
            return false;

        encoding = suffixText == "u8"
            ? EncodedStringLiteralEncoding.Utf8
            : EncodedStringLiteralEncoding.Ascii;

        ReadToken();
        return true;
    }

    private static SyntaxToken CreateEncodedStringLiteralToken(
        SyntaxToken literalToken,
        SyntaxToken suffixToken,
        EncodedStringLiteralEncoding encoding,
        bool containsInterpolation)
    {
        var decodedText = literalToken.GetValue() as string ?? string.Empty;
        var combinedText = string.Concat(literalToken.Text, suffixToken.Text);
        var value = new EncodedStringLiteralValue(decodedText, encoding, containsInterpolation);

        return new SyntaxToken(
            SyntaxKind.StringLiteralToken,
            combinedText,
            value,
            combinedText.Length,
            literalToken.LeadingTrivia,
            suffixToken.TrailingTrivia,
            diagnostics: null,
            annotations: null);
    }

    private ExpressionSyntax ParsePredefinedTypeSyntax()
    {
        var token = ReadToken();
        return PredefinedType(token);
    }

    private ExpressionSyntax ParseParenthesisExpression()
    {
        var openParenToken = ReadToken();

        var expr = new ExpressionSyntaxParser(this).ParseExpression();

        if (!ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken))
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.SemicolonExpected,
                    GetEndOfLastToken()
                )); ;
        }

        return ParenthesizedExpression(openParenToken, expr, closeParenToken);
    }

    private ExpressionSyntax ParseMatchExpressionSuffixes(ExpressionSyntax expression)
    {
        while (PeekToken().IsKind(SyntaxKind.MatchKeyword) && !HasLeadingNewLine(PeekToken()))
        {
            var disallowTryPropagationMatch = expression is TryExpressionSyntax tryExpression &&
                                             tryExpression.QuestionToken.Kind != SyntaxKind.None;
            expression = ParsePostfixMatchExpression(expression, disallowTryPropagationMatch);
        }

        return expression;
    }

    internal MatchExpressionSyntax ParseMatchExpressionKeywordFirst()
    {
        var matchKeyword = ExpectToken(SyntaxKind.MatchKeyword);
        var scrutinee = new ExpressionSyntaxParser(this, allowMatchExpressionSuffixes: false, stopOnOpenBrace: true).ParseExpression();

        var disallowTryPropagationMatch = scrutinee is TryExpressionSyntax tryExpression &&
                                          tryExpression.QuestionToken.Kind != SyntaxKind.None;

        var (openBraceToken, arms, closeBraceToken) = ParseMatchExpressionBody(disallowTryPropagationMatch);
        return MatchExpression(matchKeyword, scrutinee, openBraceToken, arms, closeBraceToken);
    }

    private PostfixMatchExpressionSyntax ParsePostfixMatchExpression(ExpressionSyntax scrutinee, bool disallowTryPropagationMatch)
    {
        var matchKeyword = ReadToken();
        var (openBraceToken, arms, closeBraceToken) = ParseMatchExpressionBody(disallowTryPropagationMatch);
        return PostfixMatchExpression(scrutinee, matchKeyword, openBraceToken, arms, closeBraceToken);
    }

    private (SyntaxToken OpenBraceToken, SyntaxList Arms, SyntaxToken CloseBraceToken) ParseMatchExpressionBody(
        bool disallowTryPropagationMatch)
    {
        if (disallowTryPropagationMatch)
        {
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.TryPropagationCannotUseMatch,
                GetSpanOfLastToken()));
        }

        ConsumeTokenOrMissing(SyntaxKind.OpenBraceToken, out var openBraceToken);

        var arms = new List<MatchArmSyntax>();

        var previousTreatNewlinesAsTokens = TreatNewlinesAsTokens;
        SetTreatNewlinesAsTokens(true);

        EnterParens();
        try
        {
            while (true)
            {
                SetTreatNewlinesAsTokens(false);

                if (IsNextToken(SyntaxKind.CloseBraceToken, out _))
                    break;

                var armStart = Position;
                var bindingKeyword = Token(SyntaxKind.None);
                if (ShouldParseMatchArmOuterBindingKeyword())
                    bindingKeyword = ReadToken();

                var pattern = new PatternSyntaxParser(
                    this,
                    allowImplicitDeconstructionElementBindings: bindingKeyword.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword,
                    allowPatternGuards: false)
                    .ParsePattern();

                WhenClauseSyntax? whenClause = null;
                if (ConsumeToken(SyntaxKind.WhenKeyword, out var whenKeyword))
                {
                    var guard = new ExpressionSyntaxParser(this, allowLambdaExpressions: false).ParseExpression();
                    whenClause = WhenClause(whenKeyword, guard);
                }

                ConsumeTokenOrMissing(SyntaxKind.FatArrowToken, out var arrowToken);

                if (HasLineBreakBeforePeekToken())
                    SkipMatchArmSeparators();

                var previousTreatNewlinesDuringExpression = TreatNewlinesAsTokens;
                ExpressionSyntax expression;
                if (PeekToken().IsKind(SyntaxKind.CloseBraceToken) ||
                    (!CanStartExpressionAfterMatchArrow(PeekToken()) && IsLikelyNextMatchArmHeader()))
                {
                    expression = IdentifierName(MissingToken(SyntaxKind.IdentifierToken));
                }
                else
                {
                    SetTreatNewlinesAsTokens(true);
                    expression = new ExpressionSyntaxParser(
                        this,
                        stopAtLeadingMatchArmOperator: true,
                        parseAbruptTransfersInBlocksAsExpressions: _parseAbruptTransfersInBlocksAsExpressions).ParseExpression();
                }

                SyntaxToken terminatorToken;
                if (!ConsumeToken(SyntaxKind.CommaToken, out terminatorToken))
                {
                    TryConsumeTerminator(out terminatorToken);
                }

                SetTreatNewlinesAsTokens(previousTreatNewlinesDuringExpression);

                SetTreatNewlinesAsTokens(false);

                if (Position == armStart)
                {
                    var bad = PeekToken();
                    var tokenText = string.IsNullOrEmpty(bad.Text) ? bad.Kind.ToString() : bad.Text;

                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                            GetSpanOfPeekedToken(),
                            tokenText));

                    if (bad.IsKind(SyntaxKind.CloseBraceToken) || bad.IsKind(SyntaxKind.EndOfFileToken))
                        break;

                    ReadToken();
                    continue;
                }

                arms.Add(MatchArm(bindingKeyword, pattern, whenClause, arrowToken, expression, terminatorToken));
            }
        }
        finally
        {
            ExitParens();
        }

        SetTreatNewlinesAsTokens(previousTreatNewlinesAsTokens);

        ConsumeTokenOrMissing(SyntaxKind.CloseBraceToken, out var closeBraceToken);

        SetTreatNewlinesAsTokens(false);

        return (openBraceToken, List(arms.ToArray()), closeBraceToken);
    }

    private bool ShouldParseMatchArmOuterBindingKeyword()
    {
        if (PeekToken().Kind is not (SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword))
            return false;

        var next = PeekToken(1).Kind;
        if (next is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken)
            return true;

        if (CanTokenBeIdentifier(PeekToken(1)) &&
            (PeekToken(2).Kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken))
            return true;

        if (next == SyntaxKind.DotToken &&
            CanTokenBeIdentifier(PeekToken(2)) &&
            (PeekToken(3).Kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken))
            return true;

        return false;
    }

    private void SkipMatchArmSeparators()
    {
        while (true)
        {
            var kind = PeekToken().Kind;

            if (kind is SyntaxKind.LineFeedToken or SyntaxKind.CarriageReturnToken or SyntaxKind.CarriageReturnLineFeedToken)
            {
                ReadToken();
                continue;
            }

            break;
        }
    }

    private static bool CanStartExpressionAfterMatchArrow(SyntaxToken token)
    {
        if (CanTokenBeIdentifier(token))
            return true;

        return token.Kind switch
        {
            SyntaxKind.StringLiteralToken or
            SyntaxKind.CharacterLiteralToken or
            SyntaxKind.NumericLiteralToken or
            SyntaxKind.SelfKeyword or
            SyntaxKind.BaseKeyword or
            SyntaxKind.TrueKeyword or
            SyntaxKind.FalseKeyword or
            SyntaxKind.NullKeyword or
            SyntaxKind.NewKeyword or
            SyntaxKind.IfKeyword or
            SyntaxKind.MatchKeyword or
            SyntaxKind.OpenParenToken or
            SyntaxKind.OpenBracketToken or
            SyntaxKind.OpenBraceToken or
            SyntaxKind.DotToken or
            SyntaxKind.FuncKeyword or
            SyntaxKind.AsyncKeyword or
            SyntaxKind.TryKeyword or
            SyntaxKind.ReturnKeyword or
            SyntaxKind.ThrowKeyword or
            SyntaxKind.BreakKeyword or
            SyntaxKind.ContinueKeyword or
            SyntaxKind.YieldKeyword or
            SyntaxKind.AwaitKeyword or
            SyntaxKind.MinusToken or
            SyntaxKind.ExclamationToken or
            SyntaxKind.TildeToken => true,
            _ => false
        };
    }

    private bool IsLikelyNextMatchArmHeader()
    {
        const int MaxLookahead = 64;
        var depth = 0;

        for (var i = 0; i < MaxLookahead; i++)
        {
            var token = PeekToken(i);

            if (token.IsKind(SyntaxKind.EndOfFileToken) || token.IsKind(SyntaxKind.CloseBraceToken))
                return false;

            if (i > 0 && TokenHasLeadingNewLine(token))
                return false;

            if (depth == 0 && token.IsKind(SyntaxKind.FatArrowToken))
                return true;

            if (token.IsKind(SyntaxKind.OpenParenToken)
                || token.IsKind(SyntaxKind.OpenBracketToken)
                || token.IsKind(SyntaxKind.OpenBraceToken))
            {
                depth++;
                continue;
            }

            if (token.IsKind(SyntaxKind.CloseParenToken)
                || token.IsKind(SyntaxKind.CloseBracketToken)
                || token.IsKind(SyntaxKind.CloseBraceToken))
            {
                if (depth > 0)
                    depth--;
            }
        }

        return false;
    }

    private ExpressionSyntax ParseIfExpressionSyntax()
    {
        var ifKeyword = ReadToken();

        if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            return ParseIfPatternExpressionSyntax(ifKeyword);

        var condition = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();

        var afterCloseParen = GetEndOfLastToken();

        if (condition.IsMissing)
        {
            AddDiagnostic(
               DiagnosticInfo.Create(
                   CompilerDiagnostics.InvalidExpressionTerm,
                   GetStartOfLastToken(),
                   [')']
               ));
        }

        var thenKeyword = Token(SyntaxKind.None);
        if (ConsumeToken(SyntaxKind.ThenKeyword, out var thenToken))
            thenKeyword = thenToken;

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        if (expression!.IsMissing)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.SemicolonExpected,
                    afterCloseParen
                ));
        }

        ElseExpressionClauseSyntax? elseClause = null;

        var elseToken = PeekToken();

        if (elseToken.IsKind(SyntaxKind.ElseKeyword))
        {
            elseClause = ParseElseExpressionClauseSyntax();
        }

        return IfExpression(ifKeyword, condition!, thenKeyword, expression!, elseClause);
    }

    private IfPatternExpressionSyntax ParseIfPatternExpressionSyntax(SyntaxToken ifKeyword)
    {
        var bindingKeyword = ReadToken();
        var pattern = new PatternSyntaxParser(
            this,
            allowImplicitDeconstructionElementBindings: true,
            allowWholePatternDesignation: true).ParsePattern();
        var operatorToken = ExpectToken(SyntaxKind.EqualsToken);
        var value = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();

        var afterValue = GetEndOfLastToken();
        var thenKeyword = Token(SyntaxKind.None);
        if (ConsumeToken(SyntaxKind.ThenKeyword, out var thenToken))
            thenKeyword = thenToken;

        var expression = new ExpressionSyntaxParser(this).ParseExpression();
        if (expression!.IsMissing)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.SemicolonExpected,
                    afterValue
                ));
        }

        ElseExpressionClauseSyntax? elseClause = null;
        if (PeekToken().IsKind(SyntaxKind.ElseKeyword))
            elseClause = ParseElseExpressionClauseSyntax();

        return IfPatternExpression(
            ifKeyword,
            bindingKeyword,
            pattern,
            operatorToken,
            value!,
            thenKeyword,
            expression!,
            elseClause);
    }

    private ElseExpressionClauseSyntax ParseElseExpressionClauseSyntax()
    {
        var elseKeyword = ReadToken();

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        return ElseExpressionClause(elseKeyword, expression);
    }

    internal ArrowExpressionClauseSyntax? ParseArrowExpressionClause()
    {
        var arrowToken = ReadToken();

        var previous = TreatNewlinesAsTokens;

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(previous);

        return ArrowExpressionClause(arrowToken, expression);
    }


    private static ExpressionSyntax ParseExpressionFromText(string text)
    {
        var parser = new LanguageParser(null, new Raven.CodeAnalysis.ParseOptions());
        return (ExpressionSyntax)parser.ParseSyntax(typeof(Raven.CodeAnalysis.Syntax.ExpressionSyntax), SourceText.From(text), 0)!;
    }

    private ArgumentListSyntax CreateMissingArgumentList()
    {
        // Used for `TypeName { ... }` where there are no parentheses.
        // We synthesize an empty, missing argument list so the tree remains structurally consistent.
        var openParen = MissingToken(SyntaxKind.OpenParenToken);
        var closeParen = MissingToken(SyntaxKind.CloseParenToken);
        return ArgumentList(openParen, List(Array.Empty<GreenNode>()), closeParen);
    }

    private WithExpressionSyntax ParseWithExpression(ExpressionSyntax expression)
    {
        // We assume current token is `with`
        var withKeyword = ReadToken();

        ConsumeTokenOrMissing(SyntaxKind.OpenBraceToken, out var openBraceToken);

        var previousTreatNewlinesAsTokens = TreatNewlinesAsTokens;
        SetTreatNewlinesAsTokens(true);

        EnterParens();
        try
        {
            var entries = new List<WithEntrySyntax>();

            while (true)
            {
                SetTreatNewlinesAsTokens(false);

                if (IsNextToken(SyntaxKind.CloseBraceToken, out _) || PeekToken().IsKind(SyntaxKind.EndOfFileToken))
                    break;

                var entryStart = Position;
                var entry = ParseWithEntry();

                if (Position == entryStart)
                {
                    // No progress: consume one token to avoid infinite loop.
                    var bad = PeekToken();
                    AddDiagnostic(DiagnosticInfo.Create(
                        CompilerDiagnostics.InvalidExpressionTerm,
                        GetSpanOfPeekedToken(),
                        bad.Text));
                    ReadToken();
                    continue;
                }

                entries.Add(entry);
            }

            SetTreatNewlinesAsTokens(previousTreatNewlinesAsTokens);

            ConsumeTokenOrMissing(SyntaxKind.CloseBraceToken, out var closeBraceToken);

            SetTreatNewlinesAsTokens(false);

            return WithExpression(expression, withKeyword, openBraceToken, List(entries.ToArray()), closeBraceToken);
        }
        finally
        {
            ExitParens();
            SetTreatNewlinesAsTokens(previousTreatNewlinesAsTokens);
        }
    }

    private WithEntrySyntax ParseWithEntry()
    {
        if (CanTokenBeIdentifier(PeekToken()) && IsAssignmentOperator(PeekToken(1).Kind))
            return ParseWithAssignment();

        return ParseWithExpressionEntry();
    }

    private WithAssignmentSyntax ParseWithAssignment()
    {
        if (!CanTokenBeIdentifier(PeekToken()))
        {
            ConsumeTokenOrMissing(SyntaxKind.IdentifierToken, out var missingIdentifier);
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.IdentifierExpected,
                    GetEndOfLastToken()));

            var missingName = IdentifierName(missingIdentifier);

            ConsumeTokenOrMissing(SyntaxKind.EqualsToken, out var missingEquals);

            var missingExpr = new ExpressionSyntax.Missing();

            // Optional terminator
            SyntaxToken terminatorToken2 = Token(SyntaxKind.None);
            if (ConsumeToken(SyntaxKind.CommaToken, out var comma))
                terminatorToken2 = comma;
            else if (!IsNextToken(SyntaxKind.CloseBraceToken, out _))
                TryConsumeTerminator(out terminatorToken2);

            return WithAssignment(missingName, missingEquals, missingExpr, terminatorToken2);
        }

        var nameToken = ReadToken();
        if (nameToken.Kind != SyntaxKind.IdentifierToken)
        {
            nameToken = ToIdentifierToken(nameToken);
            UpdateLastToken(nameToken);
        }

        var name = IdentifierName(nameToken);

        SyntaxToken equalsToken;
        if (IsAssignmentOperator(PeekToken().Kind))
        {
            equalsToken = ReadToken();
        }
        else
        {
            ConsumeTokenOrMissing(SyntaxKind.EqualsToken, out equalsToken);
        }

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        // Terminator is optional: comma, newline/semicolon, or nothing before `}`
        SyntaxToken terminatorToken = Token(SyntaxKind.None);

        if (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
        {
            terminatorToken = commaToken;
        }
        else if (!IsNextToken(SyntaxKind.CloseBraceToken, out _))
        {
            TryConsumeTerminator(out terminatorToken);
        }

        return WithAssignment(name, equalsToken, expression, terminatorToken);
    }

    private WithExpressionEntrySyntax ParseWithExpressionEntry()
    {
        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        var terminatorToken = Token(SyntaxKind.None);

        if (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
        {
            terminatorToken = commaToken;
        }
        else if (!IsNextToken(SyntaxKind.CloseBraceToken, out _))
        {
            TryConsumeTerminator(out terminatorToken);
        }

        return WithExpressionEntry(expression, terminatorToken);
    }

    private ObjectInitializerExpressionSyntax ParseObjectInitializerExpression()
    {
        // We assume current token is '{'
        ConsumeTokenOrMissing(SyntaxKind.OpenBraceToken, out var openBraceToken);

        var previousTreatNewlinesAsTokens = TreatNewlinesAsTokens;
        SetTreatNewlinesAsTokens(true);

        EnterParens();
        try
        {
            var entries = new List<ObjectInitializerEntrySyntax>();

            while (true)
            {
                SetTreatNewlinesAsTokens(false);

                var token = PeekToken();

                if (IsNextToken(SyntaxKind.CloseBraceToken, out _) || PeekToken().IsKind(SyntaxKind.EndOfFileToken))
                    break;

                var entryStart = Position;
                var entry = ParseObjectInitializerEntry();

                if (Position == entryStart)
                {
                    // No progress: consume one token and continue (or break) to avoid infinite loop.
                    var bad = PeekToken();
                    AddDiagnostic(DiagnosticInfo.Create(
                        CompilerDiagnostics.InvalidExpressionTerm,
                        GetSpanOfPeekedToken(),
                        bad.Text));
                    ReadToken();
                    continue;
                }

                entries.Add(entry);
            }

            SetTreatNewlinesAsTokens(previousTreatNewlinesAsTokens);

            ConsumeTokenOrMissing(SyntaxKind.CloseBraceToken, out var closeBraceToken);

            SetTreatNewlinesAsTokens(false);

            return ObjectInitializerExpression(openBraceToken, List(entries.ToArray()), closeBraceToken);
        }
        finally
        {
            ExitParens();
            SetTreatNewlinesAsTokens(previousTreatNewlinesAsTokens);
        }
    }

    private ObjectInitializerEntrySyntax ParseObjectInitializerEntry()
    {
        // Entry kind is decided by lookahead: <identifier> assignment-operator ...
        if (CanTokenBeIdentifier(PeekToken()) && IsAssignmentOperator(PeekToken(1).Kind))
        {
            var nameToken = ReadToken();
            if (nameToken.Kind != SyntaxKind.IdentifierToken)
            {
                nameToken = ToIdentifierToken(nameToken);
                UpdateLastToken(nameToken);
            }

            var name = IdentifierName(nameToken);
            var operatorToken = ReadToken();

            var expression = new ExpressionSyntaxParser(this).ParseExpression();

            SetTreatNewlinesAsTokens(true);

            SyntaxToken terminatorToken;
            if (!ConsumeToken(SyntaxKind.CommaToken, out terminatorToken))
            {
                TryConsumeTerminator(out terminatorToken);
            }

            return ObjectInitializerAssignmentEntry(name, operatorToken, expression, terminatorToken);
        }
        else
        {
            // Child/content entry: any expression, typically `Button { ... }`
            var expression = new ExpressionSyntaxParser(this).ParseExpression();

            SyntaxToken terminatorToken;
            if (!ConsumeToken(SyntaxKind.CommaToken, out terminatorToken))
                TryConsumeTerminator(out terminatorToken);

            return ObjectInitializerExpressionEntry(expression, terminatorToken);
        }
    }

    private bool LooksLikeLambdaAhead(int startOffset)
    {
        // Fast-path to avoid speculative parsing (checkpoint/rewind) when there's clearly no lambda.
        // We only attempt to parse a lambda if we can see a `=>` before a line break or a hard terminator.
        // This is intentionally conservative: false means "don't try lambda parsing".

        const int MaxLookahead = 64; // keep bounded; lambdas should surface quickly

        var depth = 0; // track (), [], {} nesting so we don't stop on commas inside them
        var typeArgumentDepth = 0; // track <...> so generic return types don't terminate on commas

        for (int i = startOffset; i < startOffset + MaxLookahead; i++)
        {
            var t = PeekToken(i);

            if (t.IsKind(SyntaxKind.EndOfFileToken))
                return false;

            // Stop at line breaks.
            // When newlines are treated as tokens, we see them directly.
            // When newlines are treated as trivia, the *next* token carries the newline in its leading trivia.
            if (IsNewLineLike(t))
                return false;

            if (i > startOffset && TokenHasLeadingNewLine(t))
                return false;

            // If we're not nested, these tokens end the current expression/statement region.
            // Note: we intentionally do NOT treat `)` as a terminator here, because parenthesized lambdas
            // have the shape `( ... ) => ...` and we still want to see the `=>` after the `)`.
            if (depth == 0 && typeArgumentDepth == 0)
            {
                if (t.IsKind(SyntaxKind.SemicolonToken)
                    || t.IsKind(SyntaxKind.CommaToken)
                    || t.IsKind(SyntaxKind.OpenBraceToken)
                    || t.IsKind(SyntaxKind.CloseBracketToken)
                    || t.IsKind(SyntaxKind.CloseBraceToken)
                    || t.IsKind(SyntaxKind.EqualsToken)
                    || t.IsKind(SyntaxKind.PlusEqualsToken)
                    || t.IsKind(SyntaxKind.MinusEqualsToken)
                    || t.IsKind(SyntaxKind.StarEqualsToken)
                    || t.IsKind(SyntaxKind.SlashEqualsToken))
                {
                    return false;
                }
            }

            if (t.IsKind(SyntaxKind.FatArrowToken))
                return true;

            // `(expr as Type)` should never be treated as a lambda parameter list candidate.
            if (depth == 1 && t.IsKind(SyntaxKind.AsKeyword))
                return false;

            if (t.IsKind(SyntaxKind.OpenParenToken)
                || t.IsKind(SyntaxKind.OpenBracketToken)
                || t.IsKind(SyntaxKind.OpenBraceToken))
            {
                depth++;
                continue;
            }

            if (t.IsKind(SyntaxKind.CloseParenToken)
                || t.IsKind(SyntaxKind.CloseBracketToken)
                || t.IsKind(SyntaxKind.CloseBraceToken))
            {
                if (depth > 0)
                    depth--;
                continue;
            }

            if (t.IsKind(SyntaxKind.LessThanToken))
            {
                typeArgumentDepth++;
                continue;
            }

            if (t.IsKind(SyntaxKind.GreaterThanToken))
            {
                if (typeArgumentDepth > 0)
                    typeArgumentDepth--;
                continue;
            }

            if (t.IsKind(SyntaxKind.GreaterThanGreaterThanToken))
            {
                typeArgumentDepth = Math.Max(0, typeArgumentDepth - 2);
                continue;
            }
        }

        return false;
    }
}
