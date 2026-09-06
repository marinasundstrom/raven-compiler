namespace Raven.CodeAnalysis.Syntax.InternalSyntax.Parser;

using System;
using System.Collections.Generic;
using System.Linq;

using static Raven.CodeAnalysis.Syntax.InternalSyntax.SyntaxFactory;

internal class StatementSyntaxParser : SyntaxParser
{
    private readonly bool _parseAbruptTransfersAsExpressions;

    public StatementSyntaxParser(ParseContext parent, bool? parseAbruptTransfersAsExpressions = null) : base(parent)
    {
        _parseAbruptTransfersAsExpressions = parseAbruptTransfersAsExpressions
            ?? (parent as StatementSyntaxParser)?._parseAbruptTransfersAsExpressions
            ?? false;
    }

    public StatementSyntax ParseStatement()
    {
        SetTreatNewlinesAsTokens(false); // treat newlines as statement terminators

        var token = PeekToken();

        StatementSyntax? statement;

        if (!IsTokenPotentialStatementStart(token))
        {
            return ParseIncompleteStatement();
        }

        if (IsPossibleLabeledStatementStart(token))
        {
            statement = ParseLabeledStatementSyntax();
        }
        else if (AttributeDeclarationParser.IsAttributeListStart(this) && TryParseAttributedFunctionSyntax(out var attributedFunction))
        {
            statement = attributedFunction;
        }
        else if (IsFunctionDeclarationStart())
        {
            statement = ParseFunctionSyntax(SyntaxList.Empty, SyntaxList.Empty);
        }
        else if (IsLocalTypeDeclarationStart())
        {
            statement = ParseTypeDeclarationStatementSyntax();
        }
        else if (IsInMacro && IsMacroExpansionKeyword(token) &&
                 (!_parseAbruptTransfersAsExpressions || !IsMacroExpansionExpressionKeyword(token)))
        {
            statement = ParseMacroExpansionStatementSyntax();
        }
        else
        {
            switch (token.Kind)
            {
                case SyntaxKind.YieldKeyword:
                    statement = _parseAbruptTransfersAsExpressions
                        ? ParseDeclarationOrExpressionStatementSyntax()
                        : ParseYieldStatementSyntax();
                    break;

                case SyntaxKind.ReturnKeyword:
                    statement = _parseAbruptTransfersAsExpressions
                        ? ParseDeclarationOrExpressionStatementSyntax()
                        : ParseReturnStatementSyntax();
                    break;

                case SyntaxKind.ThrowKeyword:
                    statement = _parseAbruptTransfersAsExpressions
                        ? ParseDeclarationOrExpressionStatementSyntax()
                        : ParseThrowStatementSyntax();
                    break;

                case SyntaxKind.IfKeyword:
                    statement = ParseIfStatementSyntax();
                    break;

                case SyntaxKind.WhileKeyword:
                    statement = ParseWhileStatementSyntax();
                    break;

                case SyntaxKind.LoopKeyword:
                    statement = ParseLoopStatementSyntax();
                    break;

                case SyntaxKind.LockKeyword:
                    statement = ParseLockStatementSyntax();
                    break;

                case SyntaxKind.UseKeyword:
                    statement = ParseUseDeclarationStatementSyntax();
                    break;

                case SyntaxKind.TryKeyword:
                    var next = PeekToken(1);
                    if (!next.IsKind(SyntaxKind.OpenBraceToken))
                    {
                        return ParseDeclarationOrExpressionStatementSyntax()!;
                    }
                    statement = ParseTryStatementSyntax();
                    break;

                case SyntaxKind.ForKeyword:
                    statement = ParseForStatementSyntax();
                    break;

                case SyntaxKind.AwaitKeyword when PeekToken(1).Kind == SyntaxKind.ForKeyword:
                    statement = ParseForStatementSyntax(awaitKeyword: ReadToken());
                    break;

                case SyntaxKind.OpenBraceToken:
                    statement = ParseBlockStatementSyntax();
                    break;

                case SyntaxKind.UnsafeKeyword:
                    statement = ParseUnsafeStatementSyntax();
                    break;

                case SyntaxKind.GotoKeyword:
                    statement = ParseGotoStatementSyntax();
                    break;

                case SyntaxKind.BreakKeyword:
                    statement = _parseAbruptTransfersAsExpressions
                        ? ParseDeclarationOrExpressionStatementSyntax()
                        : ParseBreakStatementSyntax();
                    break;

                case SyntaxKind.ContinueKeyword:
                    statement = _parseAbruptTransfersAsExpressions
                        ? ParseDeclarationOrExpressionStatementSyntax()
                        : ParseContinueStatementSyntax();
                    break;

                case SyntaxKind.MatchKeyword:
                    statement = ParseMatchStatementSyntax();
                    break;

                case SyntaxKind.SemicolonToken:
                    ReadToken();
                    statement = EmptyStatement(token);
                    break;

                default:
                    statement = ParseDeclarationOrExpressionStatementSyntax();
                    break;
            }
        }

        return statement;
    }

    private static bool IsMacroExpansionKeyword(SyntaxToken token)
        => token.IsKind(SyntaxKind.IdentifierToken) &&
           token.GetValueText() is "expand" or "replace" or "introduce" or "fragment" or "token";

    private static bool IsMacroExpansionExpressionKeyword(SyntaxToken token)
        => token.IsKind(SyntaxKind.IdentifierToken) &&
           token.GetValueText() is "expand" or "replace" or "introduce";

    private MacroExpansionStatementSyntax ParseMacroExpansionStatementSyntax()
    {
        var keyword = ReadToken();

        SetTreatNewlinesAsTokens(false);
        var expression = new ExpressionSyntaxParser(this).ParseExpression();
        SetTreatNewlinesAsTokens(true);

        TryConsumeTerminator(out var terminatorToken);
        return MacroExpansionStatement(keyword, expression, terminatorToken);
    }

    private bool IsFunctionDeclarationStart()
    {
        var offset = 0;
        if (PeekToken(offset).Kind == SyntaxKind.FuncKeyword)
            return true;

        while (IsFunctionModifier(PeekToken(offset).Kind))
            offset++;

        return PeekToken(offset).Kind == SyntaxKind.FuncKeyword;
    }

    private static bool IsFunctionModifier(SyntaxKind kind)
    {
        return kind is SyntaxKind.AsyncKeyword or SyntaxKind.UnsafeKeyword or SyntaxKind.ExternKeyword or SyntaxKind.StaticKeyword;
    }

    private bool TryParseAttributedFunctionSyntax(out StatementSyntax? functionStatement)
    {
        functionStatement = null;

        var checkpoint = CreateCheckpoint("attributed-function-statement");
        var attributeLists = AttributeDeclarationParser.ParseAttributeLists(this);
        var modifiers = ParseFunctionModifiers();

        if (!PeekToken().IsKind(SyntaxKind.FuncKeyword))
        {
            checkpoint.Rewind();
            return false;
        }

        functionStatement = ParseFunctionSyntax(attributeLists, modifiers);
        return true;
    }

    private bool IsLocalTypeDeclarationStart()
    {
        return TypeDeclarationParser.PeekTypeKeyword(this) is
            SyntaxKind.ClassKeyword or
            SyntaxKind.StructKeyword or
            SyntaxKind.RecordKeyword or
            SyntaxKind.EnumKeyword or
            SyntaxKind.InterfaceKeyword or
            SyntaxKind.UnionKeyword;
    }

    private TypeDeclarationStatementSyntax ParseTypeDeclarationStatementSyntax()
    {
        BaseTypeDeclarationSyntax declaration = TypeDeclarationParser.PeekTypeKeyword(this) switch
        {
            SyntaxKind.EnumKeyword => new EnumDeclarationParser(this).Parse(),
            SyntaxKind.UnionKeyword => new UnionDeclarationParser(this).Parse(),
            _ => new TypeDeclarationParser(this).Parse()
        };

        return TypeDeclarationStatement(declaration);
    }

    private bool IsPossibleLabeledStatementStart(SyntaxToken token)
    {
        if (!CanTokenBeIdentifier(token))
        {
            if (!global::Raven.CodeAnalysis.Syntax.SyntaxFacts.IsReservedWordKind(token.Kind))
            {
                return false;
            }
        }

        var colonCandidate = PeekToken(1);
        return colonCandidate.Kind == SyntaxKind.ColonToken;
    }

    private LabeledStatementSyntax ParseLabeledStatementSyntax()
    {
        SyntaxToken identifier;
        if (global::Raven.CodeAnalysis.Syntax.SyntaxFacts.IsReservedWordKind(PeekToken().Kind))
        {
            identifier = ReadToken();
        }
        else
        {
            identifier = ReadIdentifierToken();
        }
        var colonToken = ExpectToken(SyntaxKind.ColonToken);

        StatementSyntax statement;
        if (IsTokenPotentialStatementStart(PeekToken()))
        {
            statement = ParseStatement();
        }
        else
        {
            var terminator = Token(SyntaxKind.None);
            statement = EmptyStatement(terminator);
        }

        return LabeledStatement(identifier, colonToken, statement);
    }

    internal static bool IsTokenPotentialStatementStart(SyntaxToken token)
    {
        return token.Kind switch
        {
            SyntaxKind.None => false,
            SyntaxKind.EndOfFileToken => false,
            SyntaxKind.CloseBraceToken => false,
            SyntaxKind.CloseParenToken => false,
            SyntaxKind.CloseBracketToken => false,
            SyntaxKind.ArrowToken => false,
            SyntaxKind.FatArrowToken => false,
            SyntaxKind.SemicolonToken => true,
            SyntaxKind.CommaToken => false,
            SyntaxKind.ColonToken => false,
            SyntaxKind.LineFeedToken => false,
            SyntaxKind.CarriageReturnToken => false,
            SyntaxKind.CarriageReturnLineFeedToken => false,
            _ => true,
        };
    }

    private IncompleteStatementSyntax ParseIncompleteStatement()
    {
        var peek = PeekToken();
        var span = GetSpanOfPeekedToken();

        var skippedTokens = ConsumeSkippedTokensUntil(static token =>
            token.Kind is SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken ||
            IsTokenPotentialStatementStart(token),
            stopAtImplicitLineBreak: true);

        var skippedToken = CreateSkippedToken(skippedTokens, span);

        return IncompleteStatement(skippedToken);
    }

    private GotoStatementSyntax ParseGotoStatementSyntax()
    {
        var gotoKeyword = ReadToken();

        SyntaxToken identifier;
        var next = PeekToken();
        if (CanTokenBeIdentifier(next))
        {
            identifier = ReadIdentifierToken();
        }
        else if (global::Raven.CodeAnalysis.Syntax.SyntaxFacts.IsReservedWordKind(next.Kind))
        {
            identifier = ReadToken();
        }
        else
        {
            identifier = ExpectToken(SyntaxKind.IdentifierToken);
        }

        var terminatorToken = ConsumeTerminator();

        return GotoStatement(gotoKeyword, identifier, terminatorToken);
    }

    private BreakStatementSyntax ParseBreakStatementSyntax()
    {
        var breakKeyword = ReadToken();
        var identifier = ParseOptionalControlFlowLabel();

        var terminatorToken = ConsumeTerminator();

        return BreakStatement(breakKeyword, identifier, terminatorToken);
    }

    private ContinueStatementSyntax ParseContinueStatementSyntax()
    {
        var continueKeyword = ReadToken();
        var identifier = ParseOptionalControlFlowLabel();

        var terminatorToken = ConsumeTerminator();

        return ContinueStatement(continueKeyword, identifier, terminatorToken);
    }

    private SyntaxToken ParseOptionalControlFlowLabel()
    {
        if (HasLineBreakBeforePeekToken())
            return Token(SyntaxKind.None);

        var next = PeekToken();
        if (CanTokenBeIdentifier(next))
            return ReadIdentifierToken();

        if (global::Raven.CodeAnalysis.Syntax.SyntaxFacts.IsReservedWordKind(next.Kind))
            return ReadToken();

        return Token(SyntaxKind.None);
    }

    private StatementSyntax ParseYieldStatementSyntax()
    {
        var yieldKeyword = ReadToken();

        var next = PeekToken();

        if (next.Kind == SyntaxKind.BreakKeyword)
        {
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.YieldBreakFormRemoved,
                GetSpanOfPeekedToken()));

            var breakKeyword = ReadToken();
            var terminatorToken = ConsumeTerminator();
            return YieldStatement(yieldKeyword, Token(SyntaxKind.None), BreakExpression(breakKeyword, Token(SyntaxKind.None)), terminatorToken);
        }

        if (next.Kind == SyntaxKind.ReturnKeyword)
        {
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.YieldReturnFormRemoved,
                GetSpanOfPeekedToken()));

            var returnKeyword = ReadToken();
            return ParseYieldStatementSyntax(yieldKeyword, returnKeyword);
        }

        return ParseYieldStatementSyntax(yieldKeyword);
    }

    private YieldStatementSyntax ParseYieldStatementSyntax(SyntaxToken yieldKeyword, SyntaxToken returnKeyword)
    {
        SetTreatNewlinesAsTokens(false);

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        SyntaxToken? terminatorToken = null;
        if (PeekToken().Kind != SyntaxKind.ElseKeyword)
        {
            if (!TryConsumeTerminator(out terminatorToken))
            {
                SkipUntil(SyntaxKind.LineFeedToken, SyntaxKind.CarriageReturnToken, SyntaxKind.CarriageReturnLineFeedToken, SyntaxKind.SemicolonToken);
            }
        }
        else
        {
            terminatorToken = Token(SyntaxKind.None);
        }

        return YieldStatement(yieldKeyword, Token(SyntaxKind.None), ReturnExpression(returnKeyword, expression), terminatorToken);
    }

    private YieldStatementSyntax ParseYieldStatementSyntax(SyntaxToken yieldKeyword)
    {
        SetTreatNewlinesAsTokens(false);

        var fromKeyword = PeekToken() is { Kind: SyntaxKind.IdentifierToken, Text: "from" }
            ? ReadToken()
            : Token(SyntaxKind.None);

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        SyntaxToken? terminatorToken = null;
        if (PeekToken().Kind != SyntaxKind.ElseKeyword)
        {
            if (!TryConsumeTerminator(out terminatorToken))
            {
                SkipUntil(SyntaxKind.LineFeedToken, SyntaxKind.CarriageReturnToken, SyntaxKind.CarriageReturnLineFeedToken, SyntaxKind.SemicolonToken);
            }
        }
        else
        {
            terminatorToken = Token(SyntaxKind.None);
        }

        return YieldStatement(yieldKeyword, fromKeyword, expression, terminatorToken);
    }

    private StatementSyntax ParseIfStatementSyntax()
    {
        var ifKeyword = ReadToken();

        if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            return ParseIfPatternStatementSyntax(ifKeyword);

        var condition = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();

        var thenKeyword = Token(SyntaxKind.None);
        if (ConsumeToken(SyntaxKind.ThenKeyword, out var thenToken))
            thenKeyword = thenToken;

        var thenStatement = ParseEmbeddedStatement(requireNewLineForNonBlockBody: thenKeyword.Kind == SyntaxKind.None);

        SyntaxToken? elseKeyword = null;
        StatementSyntax? elseStatement = null;

        ElseClauseSyntax? elseClause = null;

        if (ConsumeToken(SyntaxKind.ElseKeyword, out var elseTok))
        {
            elseKeyword = elseTok;

            elseStatement = ParseEmbeddedStatement(
                requireNewLineForNonBlockBody: thenKeyword.Kind == SyntaxKind.None,
                allowAdjacentIfStatement: true);

            elseClause = ElseClause(elseKeyword, elseStatement);
        }

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return IfStatement(ifKeyword, condition!, thenKeyword, thenStatement!, elseClause, terminatorToken);
    }

    private IfPatternStatementSyntax ParseIfPatternStatementSyntax(SyntaxToken ifKeyword)
    {
        var bindingKeyword = ReadToken();
        var pattern = new PatternSyntaxParser(
            this,
            allowImplicitDeconstructionElementBindings: true,
            allowWholePatternDesignation: true).ParsePattern();
        var operatorToken = ExpectToken(SyntaxKind.EqualsToken);
        var expression = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
        var thenKeyword = Token(SyntaxKind.None);
        if (ConsumeToken(SyntaxKind.ThenKeyword, out var thenToken))
            thenKeyword = thenToken;

        var thenStatement = ParseEmbeddedStatement(requireNewLineForNonBlockBody: thenKeyword.Kind == SyntaxKind.None);

        ElseClauseSyntax? elseClause = null;
        if (ConsumeToken(SyntaxKind.ElseKeyword, out var elseKeyword))
        {
            var elseStatement = ParseEmbeddedStatement(
                requireNewLineForNonBlockBody: thenKeyword.Kind == SyntaxKind.None,
                allowAdjacentIfStatement: true);
            elseClause = ElseClause(elseKeyword, elseStatement);
        }

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return IfPatternStatement(
            ifKeyword,
            bindingKeyword,
            pattern,
            operatorToken,
            expression!,
            thenKeyword,
            thenStatement!,
            elseClause,
            terminatorToken);
    }

    private StatementSyntax ParseWhileStatementSyntax()
    {
        var whileKeyword = ReadToken();

        if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            return ParseWhilePatternStatementSyntax(whileKeyword);

        var condition = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
        var statement = ParseEmbeddedStatement(requireNewLineForNonBlockBody: true);

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return WhileStatement(whileKeyword, condition!, statement!, terminatorToken);
    }

    private LockStatementSyntax ParseLockStatementSyntax()
    {
        var lockKeyword = ReadToken();
        var expression = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
        var statement = ParseEmbeddedStatement(requireNewLineForNonBlockBody: true);

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return LockStatement(lockKeyword, expression!, statement!, terminatorToken);
    }

    private WhilePatternStatementSyntax ParseWhilePatternStatementSyntax(SyntaxToken whileKeyword)
    {
        var bindingKeyword = ReadToken();
        var pattern = new PatternSyntaxParser(
            this,
            allowImplicitDeconstructionElementBindings: true,
            allowWholePatternDesignation: true).ParsePattern();
        var operatorToken = ExpectToken(SyntaxKind.EqualsToken);
        var expression = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
        var statement = ParseEmbeddedStatement(requireNewLineForNonBlockBody: true);

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return WhilePatternStatement(
            whileKeyword,
            bindingKeyword,
            pattern,
            operatorToken,
            expression!,
            statement!,
            terminatorToken);
    }

    private LoopStatementSyntax ParseLoopStatementSyntax()
    {
        var loopKeyword = ReadToken();
        var statement = ParseEmbeddedStatement(requireNewLineForNonBlockBody: true);

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return LoopStatement(loopKeyword, statement!, terminatorToken);
    }

    private TryStatementSyntax ParseTryStatementSyntax()
    {
        var tryKeyword = ReadToken();

        var block = ParseBlockStatementSyntax();

        var catchClauses = new List<CatchClauseSyntax>();

        while (IsNextToken(SyntaxKind.CatchKeyword, out _))
        {
            catchClauses.Add(ParseCatchClauseSyntax());
        }

        FinallyClauseSyntax? finallyClause = null;

        if (IsNextToken(SyntaxKind.FinallyKeyword, out _))
        {
            finallyClause = ParseFinallyClauseSyntax();
        }

        if (catchClauses.Count == 0 && finallyClause is null)
        {
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.TryStatementRequiresCatchOrFinally,
                GetSpanOfLastToken()));
        }

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return TryStatement(
            tryKeyword,
            block,
            List(catchClauses.ToArray()),
            finallyClause,
            terminatorToken);
    }

    private CatchClauseSyntax ParseCatchClauseSyntax()
    {
        var catchKeyword = ReadToken();
        ConsumeTokenOrNone(SyntaxKind.OpenParenToken, out var openParenToken);
        PatternSyntax? pattern = null;
        var closeParenToken = Token(SyntaxKind.None);
        WhenClauseSyntax? whenClause = null;

        if (!openParenToken.IsKind(SyntaxKind.None))
        {
            pattern = new PatternSyntaxParser(this, allowTypeSyntaxConstantPatterns: false).ParsePatternWithoutTopLevelGuard();
            ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out closeParenToken);
        }
        else if (PeekToken().Kind != SyntaxKind.OpenBraceToken)
        {
            pattern = new PatternSyntaxParser(this, allowTypeSyntaxConstantPatterns: false).ParsePatternWithoutTopLevelGuard();
        }

        if (ConsumeToken(SyntaxKind.WhenKeyword, out var whenKeyword))
        {
            var guard = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
            whenClause = WhenClause(whenKeyword, guard);
        }

        var block = ParseBlockStatementSyntax();

        return CatchClause(catchKeyword, openParenToken, pattern, closeParenToken, whenClause, block);
    }

    private FinallyClauseSyntax ParseFinallyClauseSyntax()
    {
        var finallyKeyword = ReadToken();
        var block = ParseBlockStatementSyntax();
        return FinallyClause(finallyKeyword, block);
    }

    private ForStatementSyntax ParseForStatementSyntax(SyntaxToken? awaitKeyword = null)
    {
        awaitKeyword ??= Token(SyntaxKind.None);
        var forKeyword = ReadToken();
        var bindingKeyword = Token(SyntaxKind.None);

        if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
            bindingKeyword = ReadToken();

        ExpressionOrPatternSyntax? target;
        var current = PeekToken();
        if (current.Kind is SyntaxKind.InKeyword)
        {
            target = null;
        }
        else if (current.Kind is SyntaxKind.UnderscoreToken)
        {
            target = DiscardPattern(ReadToken());
        }
        else if (IsTypedForIterationIdentifierTarget())
        {
            target = ParseTypedForIterationIdentifierTarget();
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

        var expression = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
        SyntaxToken byKeyword;
        ExpressionSyntax? stepExpression;
        if (IsNextToken(SyntaxKind.ByKeyword, out _))
        {
            byKeyword = ReadToken();
            stepExpression = new ExpressionSyntaxParser(this, stopOnOpenBrace: true).ParseExpression();
        }
        else
        {
            byKeyword = Token(SyntaxKind.None);
            stepExpression = null;
        }

        var body = ParseEmbeddedStatement(requireNewLineForNonBlockBody: true);

        SetTreatNewlinesAsTokens(true);
        TryConsumeTerminator(out var terminatorToken);

        return ForStatement(awaitKeyword, forKeyword, bindingKeyword, target, inKeyword, expression!, byKeyword, stepExpression, body!, terminatorToken);
    }

    private bool IsTypedForIterationIdentifierTarget()
        => (CanTokenBeIdentifier(PeekToken()) || PeekToken().Kind is SyntaxKind.UnderscoreToken) &&
           PeekToken(1).Kind is SyntaxKind.ColonToken;

    private ExpressionOrPatternSyntax ParseTypedForIterationIdentifierTarget()
    {
        SyntaxToken identifier;
        if (PeekToken().Kind == SyntaxKind.UnderscoreToken)
        {
            var underscore = ReadToken();
            identifier = ToIdentifierToken(underscore);
            UpdateLastToken(identifier);
        }
        else
        {
            identifier = ReadIdentifierToken();
        }

        var typeAnnotation = new TypeAnnotationClauseSyntaxParser(this).ParseTypeAnnotation();
        VariableDesignationSyntax designation = SingleVariableDesignation(Token(SyntaxKind.None), identifier);

        if (typeAnnotation is not null)
            designation = TypedVariableDesignation(designation, typeAnnotation);

        return VariablePattern(Token(SyntaxKind.None), designation);
    }

    private StatementSyntax ParseEmbeddedStatement(bool requireNewLineForNonBlockBody, bool allowAdjacentIfStatement = false)
    {
        if (requireNewLineForNonBlockBody && RequiresEmbeddedStatementLineBreak(PeekToken(), allowAdjacentIfStatement))
        {
            AddDiagnostic(DiagnosticInfo.Create(
                CompilerDiagnostics.EmbeddedStatementMustBeginOnNextLine,
                GetInsertionSpanBeforePeekedToken()));
        }

        return ParseStatement();
    }

    private bool RequiresEmbeddedStatementLineBreak(SyntaxToken token, bool allowAdjacentIfStatement)
    {
        if (HasLineBreakBeforePeekToken())
            return false;

        if (token.Kind == SyntaxKind.OpenBraceToken)
            return false;

        if (allowAdjacentIfStatement && token.Kind == SyntaxKind.IfKeyword)
            return false;

        return true;
    }

    private StatementSyntax? ParseFunctionSyntax(SyntaxList attributeLists, SyntaxList modifiers)
    {
        modifiers = !modifiers.GetChildren().Any() ? ParseFunctionModifiers() : modifiers;
        var isExtern = modifiers.GetChildren().Any(child => child.IsKind(SyntaxKind.ExternKeyword));

        var funcKeyword = ExpectToken(SyntaxKind.FuncKeyword);
        SyntaxToken identifier;
        if (CanTokenBeIdentifier(PeekToken()))
        {
            identifier = ReadIdentifierToken();
        }
        else
        {
            identifier = ExpectToken(SyntaxKind.IdentifierToken);
        }

        TypeParameterListSyntax? typeParameterList = null;
        if (IsNextToken(SyntaxKind.LessThanToken, out _))
        {
            var typeParameterParser = new TypeDeclarationParser(this);
            typeParameterList = typeParameterParser.ParseTypeParameterList();
        }

        var parameterList = ParseParameterList(allowDiscardParameters: true);

        var returnParameterAnnotation = new TypeAnnotationClauseSyntaxParser(this).ParseReturnTypeAnnotation();

        var constraintClauses = new ConstrainClauseListParser(this).ParseConstraintClauseList();

        BlockStatementSyntax? block = null;
        ArrowExpressionClauseSyntax? expressionBody = null;

        if (IsNextToken(SyntaxKind.OpenBraceToken, out _))
        {
            block = ParseBlockStatementSyntax();
        }
        else if (IsNextToken(SyntaxKind.FatArrowToken, out _))
        {
            expressionBody = new ExpressionSyntaxParser(this).ParseArrowExpressionClause();
        }
        else if (!isExtern)
        {
            block = ParseBlockStatementSyntax();
        }

        TryConsumeTerminator(out var terminatorToken);

        return FunctionStatement(
            attributeLists,
            modifiers,
            funcKeyword,
            identifier,
            typeParameterList,
            parameterList,
            returnParameterAnnotation,
            constraintClauses,
            block,
            expressionBody,
            terminatorToken);
    }

    private SyntaxList ParseFunctionModifiers()
    {
        SyntaxList modifiers = SyntaxList.Empty;

        while (true)
        {
            var kind = PeekToken().Kind;

            if (kind is SyntaxKind.AsyncKeyword or SyntaxKind.UnsafeKeyword or SyntaxKind.ExternKeyword or SyntaxKind.StaticKeyword)
            {
                modifiers = modifiers.Add(ReadToken());
            }
            else
            {
                break;
            }
        }

        return modifiers;
    }

    private UnsafeStatementSyntax ParseUnsafeStatementSyntax()
    {
        var unsafeKeyword = ReadToken();
        var block = ParseBlockStatementSyntax();
        return UnsafeStatement(unsafeKeyword, block);
    }

    public BlockStatementSyntax ParseBlockStatementSyntax()
    {
        var openBrace = ExpectToken(SyntaxKind.OpenBraceToken);

        EnterParens();
        var statements = new List<StatementSyntax>();

        while (!IsNextToken(SyntaxKind.CloseBraceToken, out _) &&
               !IsNextToken(SyntaxKind.EndOfFileToken, out _))
        {
            var statementStart = Position;
            var stmt = new StatementSyntaxParser(this).ParseStatement();
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

        return BlockStatement(openBrace, List(statements), closeBrace);
    }

    public ParameterListSyntax ParseParameterList(
        SyntaxToken? openParenToken = null,
        bool allowDestructuringPatterns = false,
        bool allowDiscardParameters = false,
        bool allowMacroTargetModifier = false)
    {
        var openParenTokenValue = openParenToken ?? ReadToken();

        List<GreenNode> parameterList = new List<GreenNode>();

        var parsedParameters = 0;
        var restoreNewlinesAsTokens = TreatNewlinesAsTokens;
        SetTreatNewlinesAsTokens(false);

        try
        {
            while (true)
            {
                if (parsedParameters > 0)
                {
                    if (TryConsumeParameterSeparator(SyntaxKind.CloseParenToken, out var separatorToken))
                    {
                        parameterList.Add(separatorToken);
                    }
                    else if (!PeekToken().IsKind(SyntaxKind.CloseParenToken) &&
                             !PeekToken().IsKind(SyntaxKind.EndOfFileToken))
                    {
                        parameterList.Add(MissingToken(SyntaxKind.CommaToken));
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.CharacterExpected,
                                GetSpanOfPeekedToken(),
                                ","));
                    }
                }

                var t = PeekToken();

                if (t.IsKind(SyntaxKind.EndOfFileToken) ||
                    t.IsKind(SyntaxKind.CloseParenToken) ||
                    IsParameterListRecoveryBoundary(t))
                {
                    break;
                }

                var parameterStart = Position;
                var canStartDestructuringPattern = allowDestructuringPatterns &&
                    (PeekToken().IsKind(SyntaxKind.OpenParenToken) ||
                     (PeekToken().IsKind(SyntaxKind.OpenBracketToken) && !LooksLikeParameterAttributeList()));
                var attributeLists = canStartDestructuringPattern
                    ? SyntaxList.Empty
                    : AttributeDeclarationParser.ParseAttributeLists(this);

                var onKeyword = Token(SyntaxKind.None);
                if (allowMacroTargetModifier &&
                    PeekToken().IsKind(SyntaxKind.IdentifierToken) &&
                    string.Equals(PeekToken().GetValueText(), "on", StringComparison.Ordinal))
                {
                    onKeyword = ReadToken();
                }

                var scopedKeyword = Token(SyntaxKind.None);
                if (PeekToken().Kind == SyntaxKind.ScopedKeyword)
                    scopedKeyword = ReadToken();

                var refKindKeyword = Token(SyntaxKind.None);
                if (PeekToken().Kind is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
                    refKindKeyword = ReadToken();

                var varParamsKeyword = Token(SyntaxKind.None);
                if (PeekToken().Kind == SyntaxKind.ParamsKeyword)
                    varParamsKeyword = ReadToken();

                var bindingKeyword = Token(SyntaxKind.None);
                if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword or SyntaxKind.ConstKeyword)
                    bindingKeyword = ReadToken();

                PatternSyntax? pattern = null;
                SyntaxToken name;
                if (canStartDestructuringPattern)
                {
                    pattern = new PatternSyntaxParser(
                        this,
                        allowImplicitDeconstructionElementBindings: true,
                        allowWholePatternDesignation: false).ParsePattern();
                    name = MissingToken(SyntaxKind.IdentifierToken);
                }
                else if (allowDiscardParameters && PeekToken().IsKind(SyntaxKind.UnderscoreToken))
                {
                    name = ReadToken();
                }
                else if (CanTokenBeIdentifier(PeekToken()))
                {
                    name = ReadIdentifierToken();
                }
                else
                {
                    name = MissingToken(SyntaxKind.IdentifierToken);

                    if (!PeekToken().IsKind(SyntaxKind.CloseParenToken))
                    {
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.IdentifierExpected,
                                GetSpanOfPeekedToken()));
                    }
                    else
                    {
                        AddDiagnostic(
                            DiagnosticInfo.Create(
                                CompilerDiagnostics.IdentifierExpected,
                                GetEndOfLastToken()));
                    }
                }

                var typeAnnotation = new TypeAnnotationClauseSyntaxParser(this).ParseTypeAnnotation();
                var dotDotDotToken = Token(SyntaxKind.None);
                if (pattern is null && PeekToken().Kind is SyntaxKind.DotDotDotToken)
                    dotDotDotToken = ReadToken();

                EqualsValueClauseSyntax? defaultValue = null;
                if (IsNextToken(SyntaxKind.EqualsToken, out _))
                {
                    defaultValue = new EqualsValueClauseSyntaxParser(this).Parse();
                }

                if (Position == parameterStart)
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

                parameterList.Add(Parameter(attributeLists, onKeyword, Token(SyntaxKind.None), scopedKeyword, refKindKeyword, varParamsKeyword, bindingKeyword, name, pattern, typeAnnotation, dotDotDotToken, defaultValue));
                parsedParameters++;
            }
        }
        finally
        {
            SetTreatNewlinesAsTokens(restoreNewlinesAsTokens);
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        if (closeParenToken.IsMissing)
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                    CompilerDiagnostics.CharacterExpected,
                    GetSpanOfLastToken(),
                    ")"));
        }

        return ParameterList(openParenTokenValue, List(parameterList.ToArray()), closeParenToken);
    }

    private bool LooksLikeParameterAttributeList()
    {
        if (!PeekToken().IsKind(SyntaxKind.OpenBracketToken))
            return false;

        var checkpoint = CreateCheckpoint("parameter-attribute-list");
        try
        {
            var attributeLists = AttributeDeclarationParser.ParseAttributeLists(this);
            if (attributeLists.SlotCount == 0)
                return false;

            return PeekToken().Kind is SyntaxKind.RefKeyword
                or SyntaxKind.OutKeyword
                or SyntaxKind.InKeyword
                or SyntaxKind.ParamsKeyword
                or SyntaxKind.LetKeyword
                or SyntaxKind.ValKeyword
                or SyntaxKind.VarKeyword
                or SyntaxKind.ConstKeyword
                or SyntaxKind.IdentifierToken;
        }
        finally
        {
            checkpoint.Rewind();
        }
    }

    private bool TryConsumeParameterSeparator(SyntaxKind closingKind, out SyntaxToken separatorToken)
    {
        var current = PeekToken();

        if (current.IsKind(SyntaxKind.CommaToken))
        {
            separatorToken = ReadToken();
            return true;
        }

        if (HasLineBreakBeforePeekToken())
        {
            separatorToken = Token(SyntaxKind.None);
            return true;
        }

        if (current.IsKind(closingKind) || current.IsKind(SyntaxKind.EndOfFileToken))
        {
            separatorToken = Token(SyntaxKind.None);
            return false;
        }

        separatorToken = Token(SyntaxKind.None);
        return false;
    }

    private StatementSyntax? ParseReturnStatementSyntax()
    {
        var returnKeyword = ReadToken();

        SetTreatNewlinesAsTokens(false);

        ExpressionSyntax? expression = null;
        var next = PeekToken();
        if (next.Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken or SyntaxKind.ElseKeyword))
            expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        SyntaxToken? terminatorToken = null;
        if (PeekToken().Kind != SyntaxKind.ElseKeyword)
        {
            if (!TryConsumeTerminator(out terminatorToken))
            {
                SkipUntil(SyntaxKind.LineFeedToken, SyntaxKind.CarriageReturnToken, SyntaxKind.CarriageReturnLineFeedToken, SyntaxKind.SemicolonToken);
            }
        }
        else
        {
            // When the next token is an 'else', the return statement has no
            // explicit terminator. Use a placeholder so downstream consumers
            // don't encounter a null token.
            terminatorToken = Token(SyntaxKind.None);
        }

        return ReturnStatement(returnKeyword, expression, terminatorToken);
    }

    private StatementSyntax ParseThrowStatementSyntax()
    {
        var throwKeyword = ReadToken();

        SetTreatNewlinesAsTokens(false);

        var expression = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        var terminatorToken = ConsumeTerminator();

        return ThrowStatement(throwKeyword, expression, terminatorToken);
    }

    private UseDeclarationStatementSyntax ParseUseDeclarationStatementSyntax()
    {
        var useKeyword = ReadToken();
        var declaration = ParseVariableDeclarationSyntax(isBindingKeywordOptional: true);
        if (!(declaration.BindingKeyword.IsKind(SyntaxKind.ValKeyword) || declaration.BindingKeyword.IsKind(SyntaxKind.LetKeyword))
            && !declaration.BindingKeyword.IsKind(SyntaxKind.None))
        {
            AddDiagnostic(
                DiagnosticInfo.Create(
                CompilerDiagnostics.IdentifierExpected,
                GetEndOfLastToken()));
        }

        InBlockClauseSyntax? inClause = null;
        if (PeekToken().Kind == SyntaxKind.InKeyword)
        {
            var inKeyword = ReadToken();
            var block = ParseBlockStatementSyntax();
            inClause = InBlockClause(inKeyword, block);
        }

        var terminatorToken = ConsumeTerminator();

        return UseDeclarationStatement(useKeyword, declaration, inClause, terminatorToken);
    }

    private StatementSyntax? ParseDeclarationOrExpressionStatementSyntax()
    {
        var statementStart = Position;
        var token = PeekToken();

        switch (token.Kind)
        {
            case SyntaxKind.ScopedKeyword:
            case SyntaxKind.LetKeyword:
            case SyntaxKind.ValKeyword:
            case SyntaxKind.VarKeyword:
            case SyntaxKind.ConstKeyword:
                if (token.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword &&
                    PeekToken(1).Kind == SyntaxKind.IdentifierToken &&
                    PeekToken(2).Kind == SyntaxKind.ColonToken &&
                    HasTopLevelElseClauseAhead())
                {
                    return ParseCollectionPatternDeclarationAssignmentStatement();
                }

                if (PeekToken(1).Kind == SyntaxKind.OpenBracketToken &&
                    token.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                {
                    return ParseCollectionPatternDeclarationAssignmentStatement();
                }

                if (PeekToken(1).Kind == SyntaxKind.OpenParenToken &&
                    token.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                {
                    return ParseCollectionPatternDeclarationAssignmentStatement();
                }

                if (PeekToken(1).Kind == SyntaxKind.IdentifierToken &&
                    PeekToken(2).Kind is SyntaxKind.OpenParenToken or SyntaxKind.DotToken &&
                    token.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                {
                    return ParseCollectionPatternDeclarationAssignmentStatement();
                }

                if (PeekToken(1).Kind != SyntaxKind.OpenParenToken)
                {
                    var declaration = ParseVariableDeclarationSyntax();
                    var declarationTerminator = ConsumeTerminatorWithSkippedTokens(addSemicolonDiagnostic: true);

                    return LocalDeclarationStatement(declaration, declarationTerminator);
                }
                break;
        }

        SetTreatNewlinesAsTokens(false);

        var expression = new ExpressionSyntaxParser(this, stopOnLeadingNewlineBinaryOperator: true).ParseExpression();

        if (expression.IsMissing)
        {
            if (Position == statementStart && !PeekToken().IsKind(SyntaxKind.EndOfFileToken))
            {
                var unexpected = PeekToken();
                var tokenText = string.IsNullOrEmpty(unexpected.Text)
                    ? unexpected.Kind.ToString()
                    : unexpected.Text;

                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.UnexpectedTokenInIncompleteSyntax,
                        GetSpanOfPeekedToken(),
                        tokenText));

                ReadToken();
            }

            var terminatorToken2 = ConsumeTerminator();

            return EmptyStatement(terminatorToken2);
        }

        var terminatorToken = ConsumeTerminatorWithSkippedTokens(addSemicolonDiagnostic: true);

        if (expression is AssignmentExpressionSyntax assignment)
        {
            var kind = GetAssignmentStatementKind(assignment.Kind, assignment.Left);

            return AssignmentStatement(kind, assignment.Left, assignment.OperatorToken, assignment.Right, terminatorToken);
        }

        return ExpressionStatement(expression, terminatorToken);
    }

    private bool HasTopLevelElseClauseAhead()
    {
        var depth = 0;
        var sawEquals = false;

        for (var offset = 1; offset < 256; offset++)
        {
            var token = PeekToken(offset);
            var kind = token.Kind;
            if (depth == 0 &&
                kind != SyntaxKind.ElseKeyword &&
                kind != SyntaxKind.DotToken &&
                (HasLeadingEndOfLineTrivia(token) ||
                 HasTrailingEndOfLineTrivia(PeekToken(offset - 1))))
            {
                return false;
            }

            switch (kind)
            {
                case SyntaxKind.OpenParenToken:
                case SyntaxKind.OpenBracketToken:
                case SyntaxKind.OpenBraceToken:
                    depth++;
                    break;
                case SyntaxKind.CloseParenToken:
                case SyntaxKind.CloseBracketToken:
                case SyntaxKind.CloseBraceToken:
                    if (depth == 0)
                        return false;
                    depth--;
                    break;
                case SyntaxKind.EqualsToken when depth == 0:
                    sawEquals = true;
                    break;
                case SyntaxKind.ElseKeyword when depth == 0:
                    return sawEquals;
                case SyntaxKind.SemicolonToken:
                case SyntaxKind.EndOfFileToken:
                    return false;
            }
        }

        return false;
    }

    private PatternDeclarationAssignmentStatementSyntax ParseCollectionPatternDeclarationAssignmentStatement()
    {
        var bindingKeyword = ReadToken();

        SetTreatNewlinesAsTokens(false);

        var left = new PatternSyntaxParser(
            this,
            allowImplicitDeconstructionElementBindings: true,
            allowWholePatternDesignation: false).ParsePattern();

        ConsumeTokenOrMissing(SyntaxKind.EqualsToken, out var operatorToken);

        var right = new ExpressionSyntaxParser(this).ParseExpression();

        SetTreatNewlinesAsTokens(true);

        ElseClauseSyntax? elseClause = null;
        if (PeekToken().Kind == SyntaxKind.ElseKeyword)
        {
            var elseKeyword = ReadToken();
            var statement = ParseStatement();
            elseClause = ElseClause(elseKeyword, statement);
        }

        var terminatorToken = ConsumeTerminatorWithSkippedTokens(addSemicolonDiagnostic: true);

        return PatternDeclarationAssignmentStatement(
            bindingKeyword,
            left,
            operatorToken,
            right,
            elseClause,
            terminatorToken);
    }

    private StatementSyntax ParseMatchStatementSyntax()
    {
        SetTreatNewlinesAsTokens(false);
        var matchExpression = new ExpressionSyntaxParser(
            this,
            parseAbruptTransfersInBlocksAsExpressions: false).ParseMatchExpressionKeywordFirst();
        var terminatorToken = ConsumeTerminatorWithSkippedTokens(addSemicolonDiagnostic: true);
        return MatchStatement(
            matchExpression.MatchKeyword,
            matchExpression.Expression,
            matchExpression.OpenBraceToken,
            matchExpression.Arms,
            matchExpression.CloseBraceToken,
            terminatorToken);
    }

    public StatementSyntax? LastStatement { get; set; }

    private static SyntaxKind GetAssignmentStatementKind(SyntaxKind expressionKind, ExpressionOrPatternSyntax left)
    {
        var isDiscard = left is DiscardPatternSyntax or DiscardExpressionSyntax;

        return expressionKind switch
        {
            SyntaxKind.SimpleAssignmentExpression when isDiscard => SyntaxKind.DiscardAssignmentStatement,
            SyntaxKind.SimpleAssignmentExpression => SyntaxKind.SimpleAssignmentStatement,
            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddAssignmentStatement,
            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractAssignmentStatement,
            SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyAssignmentStatement,
            SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideAssignmentStatement,
            SyntaxKind.BitwiseAndAssignmentExpression => SyntaxKind.BitwiseAndAssignmentStatement,
            SyntaxKind.BitwiseOrAssignmentExpression => SyntaxKind.BitwiseOrAssignmentStatement,
            SyntaxKind.BitwiseXorAssignmentExpression => SyntaxKind.BitwiseXorAssignmentStatement,
            SyntaxKind.NullCoalesceAssignmentExpression => SyntaxKind.NullCoalesceAssignmentStatement,
            _ => SyntaxKind.SimpleAssignmentStatement,
        };
    }

    private LocalDeclarationStatementSyntax ParseLocalDeclarationStatementSyntax()
    {
        var declaration = ParseVariableDeclarationSyntax();

        var terminatorToken = ConsumeTerminatorWithSkippedTokens(addSemicolonDiagnostic: true);

        return LocalDeclarationStatement(declaration, terminatorToken);
    }

    private SyntaxToken ConsumeTerminatorWithSkippedTokens(bool addSemicolonDiagnostic)
    {
        var skippedTokens = new List<SyntaxToken>();
        bool reportedDiagnostic = false;

        while (true)
        {
            var current = PeekToken();

            if (HasLineBreakBeforePeekToken())
            {
                return CreateMissingTerminatorWithSkippedTokens(skippedTokens);
            }

            if (current.Kind == SyntaxKind.SemicolonToken)
            {
                var terminator = ReadToken();
                var tokenWithTrivia = AttachSkippedTokens(terminator, skippedTokens);
                return tokenWithTrivia;
            }

            if (current.Kind == SyntaxKind.EndOfFileToken || current.Kind == SyntaxKind.CloseBraceToken)
            {
                if (addSemicolonDiagnostic && skippedTokens.Count > 0 && !reportedDiagnostic)
                {
                    AddDiagnostic(
                        DiagnosticInfo.Create(
                            CompilerDiagnostics.ConsecutiveStatementsMustBeSeparatedBySemicolon,
                            GetEndOfLastToken()));
                }

                return CreateMissingTerminatorWithSkippedTokens(skippedTokens);
            }

            if (addSemicolonDiagnostic && !reportedDiagnostic)
            {
                AddDiagnostic(
                    DiagnosticInfo.Create(
                        CompilerDiagnostics.ConsecutiveStatementsMustBeSeparatedBySemicolon,
                        GetInsertionSpanBeforePeekedToken()));
                reportedDiagnostic = true;
            }

            skippedTokens.Add(ReadToken());
        }

        SyntaxToken AttachSkippedTokens(SyntaxToken terminator, List<SyntaxToken> skippedTokens)
        {
            if (skippedTokens.Count == 0)
                return terminator;

            var trivia = new SyntaxTrivia(
                new SkippedTokensTrivia(new SyntaxList(skippedTokens.ToArray()))
            );

            var leadingTrivia = terminator.LeadingTrivia.Add(trivia);
            var newToken = new SyntaxToken(
                terminator.Kind,
                terminator.Text,
                terminator.GetValue(),
                terminator.Width,
                leadingTrivia,
                terminator.TrailingTrivia);

            UpdateLastToken(newToken);

            return newToken;
        }

        SyntaxToken CreateMissingTerminatorWithSkippedTokens(List<SyntaxToken> skippedTokens)
        {
            if (skippedTokens.Count == 0)
                return Token(SyntaxKind.None);

            var trivia = new SyntaxTrivia(
                new SkippedTokensTrivia(new SyntaxList(skippedTokens.ToArray()))
            );

            return new SyntaxToken(
                SyntaxKind.None,
                string.Empty,
                SyntaxTriviaList.Create([trivia]),
                SyntaxTriviaList.Empty);
        }
    }

    private SyntaxToken ConsumeTerminator()
    {
        SetTreatNewlinesAsTokens(true);

        TryConsumeTerminator(out var terminatorToken);

        return terminatorToken;
    }

    private VariableDeclarationSyntax? ParseVariableDeclarationSyntax(bool isBindingKeywordOptional = false)
    {
        var t0 = PeekToken();
        var scopedKeyword = Token(SyntaxKind.None);

        if (t0.IsKind(SyntaxKind.ScopedKeyword))
        {
            scopedKeyword = ReadToken();
            t0 = PeekToken();
        }

        // Explicit binding keyword
        if (t0.IsKind(SyntaxKind.ValKeyword) ||
            t0.IsKind(SyntaxKind.VarKeyword) ||
            t0.IsKind(SyntaxKind.LetKeyword) ||
            t0.IsKind(SyntaxKind.ConstKeyword))
        {
            var bindingKeyword = ReadToken();
            return FinishParseVariableDeclarationSyntax(scopedKeyword, bindingKeyword);
        }

        // No explicit keyword
        if (!isBindingKeywordOptional)
            return null;

        var syntheticBindingKeyword = Token(SyntaxKind.None);

        return FinishParseVariableDeclarationSyntax(scopedKeyword, syntheticBindingKeyword);
    }

    private VariableDeclarationSyntax FinishParseVariableDeclarationSyntax(
        SyntaxToken scopedKeyword,
        SyntaxToken bindingKeyword)
    {
        List<GreenNode> declarators = new List<GreenNode>
        {
            ParseVariableDeclarator()
        };

        while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
        {
            declarators.Add(commaToken);
            declarators.Add(ParseVariableDeclarator());
        }

        return new VariableDeclarationSyntax(scopedKeyword, bindingKeyword, List(declarators.ToArray()));
    }

    private VariableDeclaratorSyntax ParseVariableDeclarator()
    {
        SyntaxToken identifier = MissingToken(SyntaxKind.IdentifierToken);
        var next = PeekToken();

        if (next.Kind == SyntaxKind.UnderscoreToken)
        {
            var underscore = ReadToken();
            identifier = ToIdentifierToken(underscore);
            UpdateLastToken(identifier);
        }
        else if (CanTokenBeIdentifier(next))
        {
            identifier = ReadIdentifierToken();
        }

        var typeAnnotation = new TypeAnnotationClauseSyntaxParser(this).ParseTypeAnnotation();

        EqualsValueClauseSyntax? initializer = null;
        if (IsNextToken(SyntaxKind.EqualsToken, out _))
            initializer = new EqualsValueClauseSyntaxParser(this).Parse();

        return VariableDeclarator(identifier, typeAnnotation, initializer);
    }

    private TypeAnnotationClauseSyntax? ParseTypeAnnotationClauseSyntax()
    {
        if (ConsumeToken(SyntaxKind.ColonToken, out var colonToken))
        {
            TypeSyntax type = new NameSyntaxParser(this).ParseTypeName();

            return TypeAnnotationClause(colonToken, type);
        }

        return null;
    }
}
