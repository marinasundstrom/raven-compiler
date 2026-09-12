namespace Raven.CodeAnalysis.Syntax.InternalSyntax.Parser;

using System;
using System.Collections.Generic;

using Raven.CodeAnalysis.Syntax.InternalSyntax;

using static Raven.CodeAnalysis.Syntax.InternalSyntax.SyntaxFactory;

internal class PatternSyntaxParser : SyntaxParser
{
    private readonly bool _allowImplicitDeconstructionElementBindings;
    private readonly bool _allowWholePatternDesignation;
    private readonly bool _allowPatternGuards;
    private readonly bool _allowTypeSyntaxConstantPatterns;

    private bool StopsOnOpenBrace => Parent is ExpressionSyntaxParser { StopsOnOpenBrace: true };

    public PatternSyntaxParser(
        ParseContext parent,
        bool allowImplicitDeconstructionElementBindings = false,
        bool allowWholePatternDesignation = true,
        bool allowPatternGuards = true,
        bool allowTypeSyntaxConstantPatterns = true) : base(parent)
    {
        _allowImplicitDeconstructionElementBindings = allowImplicitDeconstructionElementBindings;
        _allowWholePatternDesignation = allowWholePatternDesignation;
        _allowPatternGuards = allowPatternGuards;
        _allowTypeSyntaxConstantPatterns = allowTypeSyntaxConstantPatterns;
    }

    public PatternSyntax ParsePattern()
    {
        var pattern = ParseOrPattern();
        return ParseOptionalGuardedPattern(pattern);
    }

    public PatternSyntax ParsePatternWithoutTopLevelGuard()
    {
        return ParseOrPattern();
    }

    private PatternSyntax ParseOrPattern()
    {
        var left = ParseAndPattern();

        while (ConsumeToken(SyntaxKind.OrToken, out var orKeyword) ||
               ConsumeToken(SyntaxKind.BarToken, out orKeyword))
        {
            var right = ParseAndPattern();
            left = BinaryPattern(SyntaxKind.OrPattern, left, orKeyword, right);
        }

        return left;
    }

    private PatternSyntax ParseAndPattern()
    {
        var left = ParseUnaryPattern();

        while (ConsumeToken(SyntaxKind.AndToken, out var andKeyword))
        {
            var right = ParseUnaryPattern();
            left = BinaryPattern(SyntaxKind.AndPattern, left, andKeyword, right);
        }

        return left;
    }

    private PatternSyntax ParseUnaryPattern()
    {
        if (ConsumeToken(SyntaxKind.NotKeyword, out var notKeyword))
        {
            var operand = ParseUnaryPattern(); // Right-associative
            return UnaryPattern(SyntaxKind.NotPattern, notKeyword, operand);
        }

        return ParsePrimaryPattern();
    }

    private PatternSyntax ParsePrimaryPattern()
    {
        // Comparison pattern: == expr, != expr, > expr, >= expr, < expr, <= expr
        if (IsComparisonPatternStart(PeekToken()))
            return ParseComparisonPattern();

        if (PeekToken().IsKind(SyntaxKind.DotToken))
        {
            return ParseMemberPattern(qualifier: null, dotToken: ReadToken());
        }

        if (PeekToken().IsKind(SyntaxKind.OpenParenToken))
        {
            return ParsePositionalPattern();
        }

        if (PeekToken().IsKind(SyntaxKind.OpenBracketToken))
        {
            if (IsDictionaryPatternStart())
                return ParseDictionaryPattern();

            return ParseSequencePattern();
        }

        if (PeekToken().IsKind(SyntaxKind.OpenBraceToken))
        {
            if (TryParsePropertyPatternClause(out var clause))
            {
                return PropertyPattern(null, clause, ParseOptionalTrailingDesignation());
            }

            return CreateMissingPattern();
        }

        if (PeekToken().Kind == SyntaxKind.NullKeyword)
        {
            var nullToken = ReadToken();
            return ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression, nullToken));
        }

        if (PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
        {
            return ParseVariablePattern();
        }

        if (PeekToken().Kind is SyntaxKind.UnderscoreToken)
        {
            var underscoreToken = ReadToken();
            return DiscardPattern(underscoreToken);
        }

        // Lower (or lower+upper) range pattern: `lo..hi` / `lo..` (e.g. `2..3`, `2..`, `x..y`)
        // We only take this fast-path when we can see the `..` token immediately after the lower bound.
        if (CanStartRangeBoundExpression(PeekToken()) && PeekToken(1).IsKind(SyntaxKind.DotDotToken))
        {
            var lowerBound = new ExpressionSyntaxParser(this, stopAtDotDotToken: true).ParseExpression();
            return ParseRangePattern(lowerBound);
        }

        // Upper-only range pattern: `..hi` (e.g. `..9`, `..100`)
        if (PeekToken().IsKind(SyntaxKind.DotDotToken))
        {
            return ParseRangePattern(lowerBound: null);
        }

        if (TryParseImplicitBindingPattern(out var implicitBindingPattern))
            return implicitBindingPattern;

        // Constant pattern (expression pattern). Binding decides whether the expression is a type,
        // a compile-time constant, or a runtime value that requires an explicit comparison pattern.
        if (CanStartConstantPatternExpression(PeekToken()))
        {
            if (TryParseConstantPattern(out var constantPattern))
                return constantPattern;
        }

        var type = new NameSyntaxParser(this).ParseTypeName();

        if (PeekToken().IsKind(SyntaxKind.OpenParenToken))
        {
            var argumentList = ParseNominalDeconstructionPatternArgumentList();
            return NominalDeconstructionPattern(type, argumentList, ParseOptionalTrailingDesignation());
        }

        if (PeekToken().IsKind(SyntaxKind.OpenBraceToken))
        {
            if (TryParsePropertyPatternClause(out var clause))
            {
                return PropertyPattern(type, clause, ParseOptionalTrailingDesignation());
            }

            return DeclarationPattern(type, null);
        }

        if (ConsumeToken(SyntaxKind.DotToken, out var dotToken))
        {
            return ParseMemberPattern(type, dotToken);
        }

        var designation =
            CanTokenBeIdentifier(PeekToken()) || PeekToken().IsKind(SyntaxKind.UnderscoreToken)
                ? ParseDesignation()
                : null;

        return DeclarationPattern(type, designation);
    }

    private PropertyPatternClauseSyntax ParsePropertyPatternClause()
    {
        var openBraceToken = ReadToken(); // {

        var elements = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseBraceToken))
        {
            elements.Add(ParsePropertySubpattern());

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                elements.Add(commaToken);

                // Allow trailing comma before }
                if (PeekToken().IsKind(SyntaxKind.CloseBraceToken))
                    break;

                elements.Add(ParsePropertySubpattern());
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseBraceToken, out var closeBraceToken);

        return PropertyPatternClause(
            openBraceToken,
            List(elements.ToArray()),
            closeBraceToken);
    }

    private bool TryParsePropertyPatternClause(out PropertyPatternClauseSyntax clause)
    {
        clause = null!;

        if (!IsPropertyPatternClauseStart())
            return false;

        var checkpoint = CreateCheckpoint("property-pattern-clause");
        var parsedClause = ParsePropertyPatternClause();

        if (!IsValidPropertyPatternClause(parsedClause))
        {
            checkpoint.Rewind();
            return false;
        }

        clause = parsedClause;
        return true;
    }

    private bool IsPropertyPatternClauseStart(int openBraceOffset = 0)
    {
        if (!PeekToken(openBraceOffset).IsKind(SyntaxKind.OpenBraceToken))
            return false;

        var offset = openBraceOffset + 1;
        var token = PeekToken(offset);

        if (token.IsKind(SyntaxKind.CloseBraceToken))
            return true;

        if (token.IsKind(SyntaxKind.DotToken))
        {
            offset++;
            token = PeekToken(offset);
        }

        if (!CanTokenBeIdentifier(token))
            return false;

        while (PeekToken(offset + 1).IsKind(SyntaxKind.DotToken) &&
               CanTokenBeIdentifier(PeekToken(offset + 2)))
        {
            offset += 2;
        }

        var following = PeekToken(offset + 1);
        return following.IsKind(SyntaxKind.ColonToken) ||
               following.IsKind(SyntaxKind.CloseBraceToken) ||
               following.IsKind(SyntaxKind.CommaToken) ||
               following.IsKind(SyntaxKind.DotToken);
    }

    private static bool IsValidPropertyPatternClause(PropertyPatternClauseSyntax clause)
    {
        if (clause.OpenBraceToken.IsMissing || clause.CloseBraceToken.IsMissing)
            return false;

        var properties = clause.Properties;

        for (int i = 0; i < properties.SlotCount; i++)
        {
            var element = properties[i];
            if (element is not PropertySubpatternSyntax subpattern)
                continue;

            if (subpattern.NameColon.Name.Identifier.IsMissing &&
                subpattern.MemberPath.SlotCount == 0)
                return false;
        }

        return true;
    }

    private PropertySubpatternSyntax ParsePropertySubpattern()
    {
        ConsumeToken(SyntaxKind.DotToken, out _);

        var memberPathElements = new List<GreenNode>();
        var nameToken = ParsePropertySubpatternIdentifier();

        while (PeekToken().IsKind(SyntaxKind.DotToken))
        {
            memberPathElements.Add(IdentifierName(nameToken));
            memberPathElements.Add(ReadToken());
            nameToken = ParsePropertySubpatternIdentifier();
        }

        ConsumeTokenOrMissing(SyntaxKind.ColonToken, out var colonToken);

        var nameColon = NameColon(IdentifierName(nameToken), colonToken);

        // IMPORTANT: RHS is a *pattern*, not an expression
        var pattern = new PatternSyntaxParser(
            this,
            _allowImplicitDeconstructionElementBindings,
            _allowWholePatternDesignation,
            allowPatternGuards: true).ParsePattern();

        return PropertySubpattern(List(memberPathElements.ToArray()), nameColon, pattern);
    }

    private SyntaxToken ParsePropertySubpatternIdentifier()
    {
        if (!CanTokenBeIdentifier(PeekToken()))
            return ExpectToken(SyntaxKind.IdentifierToken);

        var nameToken = ReadToken();
        if (nameToken.Kind != SyntaxKind.IdentifierToken)
        {
            nameToken = ToIdentifierToken(nameToken);
            UpdateLastToken(nameToken);
        }

        return nameToken;
    }

    private static PatternSyntax CreateMissingPattern()
    {
        return DiscardPattern(MissingToken(SyntaxKind.UnderscoreToken));
    }

    private PatternSyntax ParseVariablePattern()
    {
        var bindingKeyword = ReadToken();
        var designation = ParseDesignation(allowBindingKeyword: false);
        return VariablePattern(bindingKeyword, designation);
    }

    private PositionalPatternSyntax ParsePositionalPattern()
    {
        var openParenToken = ReadToken();

        var elementList = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            elementList.Add(ParsePositionalPatternElement());

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                elementList.Add(commaToken);
                elementList.Add(ParsePositionalPatternElement());
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);
        var designation = ParseOptionalTrailingDesignation();

        return PositionalPattern(openParenToken, List(elementList.ToArray()), closeParenToken, designation);
    }

    private bool IsDictionaryPatternStart()
    {
        if (!PeekToken().IsKind(SyntaxKind.OpenBracketToken))
            return false;

        if (_allowImplicitDeconstructionElementBindings)
        {
            var checkpoint = CreateCheckpoint("dictionary-pattern-start");
            ReadToken();
            var isImplicitTypedVariableElement = LooksLikeImplicitTypedVariablePattern();
            checkpoint.Rewind();

            if (isImplicitTypedVariableElement)
                return false;
        }

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var offset = 1; ; offset++)
        {
            var token = PeekToken(offset);

            switch (token.Kind)
            {
                case SyntaxKind.EndOfFileToken:
                    return false;

                case SyntaxKind.OpenParenToken:
                    parenDepth++;
                    break;

                case SyntaxKind.CloseParenToken:
                    if (parenDepth > 0)
                        parenDepth--;
                    break;

                case SyntaxKind.OpenBracketToken:
                    bracketDepth++;
                    break;

                case SyntaxKind.CloseBracketToken:
                    if (bracketDepth == 0 && parenDepth == 0 && braceDepth == 0)
                        return false;

                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;

                case SyntaxKind.OpenBraceToken:
                    braceDepth++;
                    break;

                case SyntaxKind.CloseBraceToken:
                    if (braceDepth > 0)
                        braceDepth--;
                    break;

                case SyntaxKind.ColonToken:
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return true;
                    break;

                case SyntaxKind.CommaToken:
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }
    }

    private DictionaryPatternSyntax ParseDictionaryPattern()
    {
        var openBracketToken = ReadToken();

        var elementList = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseBracketToken))
        {
            elementList.Add(ParseDictionaryPatternEntry());

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                elementList.Add(commaToken);

                if (PeekToken().IsKind(SyntaxKind.CloseBracketToken))
                    break;

                elementList.Add(ParseDictionaryPatternEntry());
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseBracketToken, out var closeBracketToken);
        var designation = ParseOptionalTrailingDesignation();

        return DictionaryPattern(openBracketToken, List(elementList.ToArray()), closeBracketToken, designation);
    }

    private DictionaryPatternEntrySyntax ParseDictionaryPatternEntry()
    {
        var key = new ExpressionSyntaxParser(this).ParseExpression();
        ConsumeTokenOrMissing(SyntaxKind.ColonToken, out var colonToken);
        var pattern = ParseDeconstructionElementPattern();
        return DictionaryPatternEntry(key, colonToken, pattern);
    }

    private SequencePatternSyntax ParseSequencePattern()
    {
        var openBracketToken = ReadToken();

        var elementList = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseBracketToken))
        {
            elementList.Add(ParseSequencePatternElement());

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                elementList.Add(commaToken);

                if (PeekToken().IsKind(SyntaxKind.CloseBracketToken))
                    break;

                elementList.Add(ParseSequencePatternElement());
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseBracketToken, out var closeBracketToken);
        var designation = ParseOptionalTrailingDesignation();

        return SequencePattern(openBracketToken, List(elementList.ToArray()), closeBracketToken, designation);
    }

    private SequencePatternElementSyntax ParseSequencePatternElement()
    {
        if (!TryConsumeSequenceRestToken(out var dotDotToken))
        {
            return SequencePatternElement(SequencePatternPrefix(Token(SyntaxKind.None), Token(SyntaxKind.None)), ParseDeconstructionElementPattern());
        }

        var segmentLengthToken = Token(SyntaxKind.None);
        if (dotDotToken.Kind == SyntaxKind.DotDotToken &&
            PeekToken().Kind == SyntaxKind.NumericLiteralToken)
        {
            segmentLengthToken = ReadToken();
        }

        if (((dotDotToken.Kind == SyntaxKind.DotDotDotToken &&
              segmentLengthToken.Kind == SyntaxKind.None) ||
             (dotDotToken.Kind == SyntaxKind.DotDotToken &&
              segmentLengthToken.Kind == SyntaxKind.NumericLiteralToken)) &&
            PeekToken().Kind is SyntaxKind.CommaToken or SyntaxKind.CloseBracketToken)
        {
            return SequencePatternElement(
                SequencePatternPrefix(dotDotToken, segmentLengthToken),
                DiscardPattern(MissingToken(SyntaxKind.UnderscoreToken)));
        }

        var pattern = ParseDeconstructionElementPattern();
        return SequencePatternElement(SequencePatternPrefix(dotDotToken, segmentLengthToken), pattern);
    }

    private bool TryConsumeSequenceRestToken(out SyntaxToken token)
    {
        if (ConsumeToken(SyntaxKind.DotDotDotToken, out token))
            return true;

        return ConsumeToken(SyntaxKind.DotDotToken, out token);
    }

    private MemberPatternSyntax ParseMemberPattern(TypeSyntax? qualifier, SyntaxToken dotToken)
    {
        var identifierToken = ReadToken();
        if (identifierToken.Kind != SyntaxKind.IdentifierToken)
        {
            identifierToken = ToIdentifierToken(identifierToken);
            UpdateLastToken(identifierToken);
        }

        var argumentList = ParseMemberPatternArgumentList();
        var path = MemberPatternPath(qualifier, dotToken, identifierToken);
        var designation = ParseOptionalTrailingDesignation();

        return MemberPattern(path, argumentList, designation);
    }

    private MemberPatternArgumentListSyntax? ParseMemberPatternArgumentList()
    {
        if (!PeekToken().IsKind(SyntaxKind.OpenParenToken))
            return null;

        var openParenToken = ReadToken();

        var arguments = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            arguments.Add(ParseDeconstructionElementPattern());

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                arguments.Add(commaToken);
                arguments.Add(ParseDeconstructionElementPattern());
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return MemberPatternArgumentList(openParenToken, List(arguments.ToArray()), closeParenToken);
    }

    private NominalDeconstructionPatternArgumentListSyntax ParseNominalDeconstructionPatternArgumentList()
    {
        var openParenToken = ReadToken();

        var arguments = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            arguments.Add(ParsePositionalPatternElement());

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                arguments.Add(commaToken);
                arguments.Add(ParsePositionalPatternElement());
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return NominalDeconstructionPatternArgumentList(openParenToken, List(arguments.ToArray()), closeParenToken);
    }

    private VariableDesignationSyntax ParseDesignation(bool allowBindingKeyword = true)
    {
        VariableDesignationSyntax designation;

        if (PeekToken().IsKind(SyntaxKind.OpenParenToken))
        {
            designation = ParseParenthesizedDesignation(allowBindingKeyword);
        }
        else
        {
            SyntaxToken bindingKeyword = Token(SyntaxKind.None);
            if (allowBindingKeyword && PeekToken().Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
                bindingKeyword = ReadToken();

            SyntaxToken identifier;
            if (PeekToken().Kind == SyntaxKind.UnderscoreToken)
            {
                var underscore = ReadToken();
                identifier = ToIdentifierToken(underscore);
                UpdateLastToken(identifier);
            }
            else if (CanTokenBeIdentifier(PeekToken()))
            {
                identifier = ReadIdentifierToken();
            }
            else
            {
                identifier = ExpectToken(SyntaxKind.IdentifierToken);
            }

            designation = SingleVariableDesignation(bindingKeyword, identifier);
        }

        if (ConsumeToken(SyntaxKind.ColonToken, out var colonToken))
        {
            var type = new NameSyntaxParser(this).ParseTypeName();
            var typeAnnotation = TypeAnnotationClause(colonToken, type);
            designation = TypedVariableDesignation(designation, typeAnnotation);
        }

        return designation;
    }

    private VariableDesignationSyntax ParseParenthesizedDesignation(bool allowBindingKeyword)
    {
        var openParenToken = ReadToken();

        var elements = new List<GreenNode>();

        if (!PeekToken().IsKind(SyntaxKind.CloseParenToken))
        {
            elements.Add(ParseDesignation(allowBindingKeyword));

            while (ConsumeToken(SyntaxKind.CommaToken, out var commaToken))
            {
                elements.Add(commaToken);
                elements.Add(ParseDesignation(allowBindingKeyword));
            }
        }

        ConsumeTokenOrMissing(SyntaxKind.CloseParenToken, out var closeParenToken);

        return ParenthesizedVariableDesignation(openParenToken, List(elements.ToArray()), closeParenToken);
    }

    private PositionalPatternElementSyntax ParsePositionalPatternElement()
    {
        NameColonSyntax? nameColon = null;

        if (TryParseImplicitNamedBindingElementNameColon(out nameColon))
        {
            var namedBindingPattern = ParseDeconstructionElementPattern();
            return PositionalPatternElement(nameColon, namedBindingPattern);
        }

        if (TryParseImplicitTypedVariablePattern(out var typedVariablePattern))
            return PositionalPatternElement(nameColon, typedVariablePattern);

        if (PeekToken(1).IsKind(SyntaxKind.ColonToken) && CanTokenBeIdentifier(PeekToken()))
        {
            var nameToken = ReadToken();
            if (nameToken.Kind != SyntaxKind.IdentifierToken)
            {
                nameToken = ToIdentifierToken(nameToken);
                UpdateLastToken(nameToken);
            }

            var colonToken = ReadToken();
            nameColon = NameColon(IdentifierName(nameToken), colonToken);
        }

        var pattern = ParseDeconstructionElementPattern();
        return PositionalPatternElement(nameColon, pattern);
    }

    private bool TryParseImplicitNamedBindingElementNameColon(out NameColonSyntax? nameColon)
    {
        nameColon = null;

        // In declaration/assignment deconstruction, `Name: value` is a named element
        // binding when the right side is local-shaped; `value: string` remains a typed
        // variable designation through TryParseImplicitTypedVariablePattern below.
        if (!_allowImplicitDeconstructionElementBindings ||
            !CanTokenBeIdentifier(PeekToken()) ||
            !PeekToken(1).IsKind(SyntaxKind.ColonToken) ||
            !IsLocalLookingImplicitBindingIdentifier(PeekToken(2)) ||
            !IsImplicitTypedVariablePatternTerminator(PeekToken(3).Kind))
        {
            return false;
        }

        var nameToken = ReadToken();
        if (nameToken.Kind != SyntaxKind.IdentifierToken)
        {
            nameToken = ToIdentifierToken(nameToken);
            UpdateLastToken(nameToken);
        }

        var colonToken = ReadToken();
        nameColon = NameColon(IdentifierName(nameToken), colonToken);
        return true;
    }

    private static bool IsLocalLookingImplicitBindingIdentifier(SyntaxToken token)
    {
        if (token.Kind != SyntaxKind.IdentifierToken ||
            string.IsNullOrEmpty(token.Text))
        {
            return false;
        }

        return token.Text[0] == '_' || !char.IsUpper(token.Text[0]);
    }

    private PatternSyntax ParseDeconstructionElementPattern()
    {
        if (TryParseImplicitTypedVariablePattern(out var typedVariablePattern))
            return typedVariablePattern;

        if (_allowImplicitDeconstructionElementBindings &&
            CanTokenBeIdentifier(PeekToken()) &&
            PeekToken(1).Kind is not SyntaxKind.OpenParenToken &&
            PeekToken(1).Kind is not SyntaxKind.OpenBraceToken &&
            PeekToken(1).Kind is not SyntaxKind.DotToken)
        {
            var identifier = ReadIdentifierToken();
            var pattern = VariablePattern(
                Token(SyntaxKind.None),
                SingleVariableDesignation(Token(SyntaxKind.None), identifier));
            return ParseOptionalGuardedPattern(pattern);
        }

        return new PatternSyntaxParser(
            this,
            _allowImplicitDeconstructionElementBindings,
            _allowWholePatternDesignation,
            allowPatternGuards: true).ParsePattern();
    }

    private bool TryParseImplicitTypedVariablePattern(out PatternSyntax pattern)
    {
        pattern = null!;

        if (!LooksLikeImplicitTypedVariablePattern())
            return false;

        var designation = ParseDesignation(allowBindingKeyword: false);
        pattern = ParseOptionalGuardedPattern(VariablePattern(Token(SyntaxKind.None), designation));
        return true;
    }

    private bool LooksLikeImplicitTypedVariablePattern()
    {
        if (!_allowImplicitDeconstructionElementBindings ||
            !CanTokenBeIdentifier(PeekToken()) ||
            !PeekToken(1).IsKind(SyntaxKind.ColonToken))
        {
            return false;
        }

        var checkpoint = CreateCheckpoint("implicit-typed-variable-pattern");
        var designation = ParseDesignation(allowBindingKeyword: false);
        var isTypedDesignation =
            designation is TypedVariableDesignationSyntax { TypeAnnotation.Type.IsMissing: false } &&
            IsImplicitTypedVariablePatternTerminator(PeekToken().Kind);

        checkpoint.Rewind();
        return isTypedDesignation;
    }

    private static bool IsImplicitTypedVariablePatternTerminator(SyntaxKind kind)
    {
        return kind is
            SyntaxKind.CommaToken or
            SyntaxKind.CloseParenToken or
            SyntaxKind.CloseBracketToken or
            SyntaxKind.CloseBraceToken or
            SyntaxKind.EqualsToken or
            SyntaxKind.InKeyword or
            SyntaxKind.FatArrowToken or
            SyntaxKind.WhenKeyword or
            SyntaxKind.EndOfFileToken;
    }

    private PatternSyntax ParseOptionalGuardedPattern(PatternSyntax pattern)
    {
        if (!_allowPatternGuards || !ConsumeToken(SyntaxKind.WhenKeyword, out var whenKeyword))
            return pattern;

        var guard = ParsePatternGuard();
        var whenClause = WhenClause(whenKeyword, guard);
        return GuardedPattern(pattern, whenClause);
    }

    private ExpressionOrPatternSyntax ParsePatternGuard()
    {
        if (ShouldParsePatternGuard())
        {
            return new PatternSyntaxParser(
                this,
                allowImplicitDeconstructionElementBindings: false,
                allowWholePatternDesignation: false,
                allowPatternGuards: false).ParsePattern();
        }

        return new ExpressionSyntaxParser(this).ParseExpression();
    }

    private bool ShouldParsePatternGuard()
    {
        var current = PeekToken();

        if (IsComparisonPatternStart(current) ||
            current.IsKind(SyntaxKind.DotDotToken) ||
            current.IsKind(SyntaxKind.NotKeyword) ||
            current.IsKind(SyntaxKind.OpenParenToken) ||
            current.IsKind(SyntaxKind.OpenBracketToken) ||
            current.IsKind(SyntaxKind.OpenBraceToken) ||
            current.IsKind(SyntaxKind.DotToken) ||
            current.IsKind(SyntaxKind.UnderscoreToken) ||
            current.IsKind(SyntaxKind.NullKeyword) ||
            current.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword)
        {
            return true;
        }

        return CanStartRangeBoundExpression(current) && PeekToken(1).IsKind(SyntaxKind.DotDotToken);
    }

    private bool TryParseImplicitBindingPattern(out PatternSyntax pattern)
    {
        pattern = null!;

        if (!_allowImplicitDeconstructionElementBindings || !CanTokenBeIdentifier(PeekToken()))
            return false;

        var nextKind = PeekToken(1).Kind;
        if (nextKind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBraceToken or SyntaxKind.DotToken)
            return false;

        // Preserve declaration/type-pattern forms like `Type name` by only taking this path
        // when the identifier is immediately followed by a pattern delimiter or a type annotation.
        if (CanTokenBeIdentifier(PeekToken(1)))
            return false;

        if (nextKind is not
            (SyntaxKind.ColonToken or
             SyntaxKind.EqualsToken or
             SyntaxKind.InKeyword or
             SyntaxKind.FatArrowToken or
             SyntaxKind.WhenKeyword or
             SyntaxKind.CommaToken or
             SyntaxKind.CloseParenToken or
             SyntaxKind.CloseBracketToken or
             SyntaxKind.CloseBraceToken or
             SyntaxKind.EndOfFileToken))
        {
            return false;
        }

        var designation = ParseDesignation(allowBindingKeyword: false);
        pattern = VariablePattern(Token(SyntaxKind.None), designation);
        return true;
    }

    private static bool IsComparisonPatternStart(SyntaxToken token)
    {
        return token.Kind is
            SyntaxKind.EqualsEqualsToken or
            SyntaxKind.NotEqualsToken or
            SyntaxKind.GreaterThanToken or
            SyntaxKind.GreaterThanOrEqualsToken or
            SyntaxKind.LessThanToken or
            SyntaxKind.LessThanOrEqualsToken;
    }

    private PatternSyntax ParseComparisonPattern()
    {
        // ==, !=, >, >=, <, <=
        var operatorToken = ReadToken();

        // Parse RHS as an EXPRESSION (not a pattern)
        // This allows: > 30, > x + 1, > Foo(3), etc.
        var expression = new ExpressionSyntaxParser(
            this,
            stopOnOpenBrace: StopsOnOpenBrace,
            allowLambdaExpressions: false).ParseExpression();

        var kind = GetComparisonPatternKind(operatorToken.Kind);
        return ComparisonPattern(kind, operatorToken, expression);
    }

    private VariableDesignationSyntax? ParseOptionalTrailingDesignation()
    {
        if (!_allowWholePatternDesignation)
            return null;

        if (HasLineBreakBeforePeekToken())
            return null;

        var next = PeekToken();
        var canStartDesignation =
            !IsPatternTerminatorThatCannotStartDesignation(next.Kind) &&
            (CanTokenBeIdentifier(next) ||
             next.IsKind(SyntaxKind.OpenParenToken) ||
             next.Kind is SyntaxKind.LetKeyword or SyntaxKind.ValKeyword or SyntaxKind.VarKeyword);

        return canStartDesignation ? ParseDesignation() : null;
    }

    private static bool IsPatternTerminatorThatCannotStartDesignation(SyntaxKind kind)
    {
        return kind is SyntaxKind.InKeyword
            or SyntaxKind.EqualsToken
            or SyntaxKind.FatArrowToken
            or SyntaxKind.WhenKeyword;
    }

    private static SyntaxKind GetComparisonPatternKind(SyntaxKind operatorTokenKind)
    {
        return operatorTokenKind switch
        {
            SyntaxKind.EqualsEqualsToken => SyntaxKind.EqualsPattern,
            SyntaxKind.NotEqualsToken => SyntaxKind.NotEqualsPattern,
            SyntaxKind.GreaterThanToken => SyntaxKind.GreaterThanPattern,
            SyntaxKind.GreaterThanOrEqualsToken => SyntaxKind.GreaterThanOrEqualPattern,
            SyntaxKind.LessThanToken => SyntaxKind.LessThanPattern,
            SyntaxKind.LessThanOrEqualsToken => SyntaxKind.LessThanOrEqualPattern,
            _ => throw new InvalidOperationException($"Unexpected relational operator token: {operatorTokenKind}")
        };
    }

    // Range pattern: `lo..hi`, `lo..`, `..hi`
    // Called either with a pre-parsed lower bound (from TryParseConstantPattern) or null (for `..hi`).
    private PatternSyntax ParseRangePattern(ExpressionSyntax? lowerBound)
    {
        var dotDotToken = ReadToken(); // consume `..`
        var lessThanToken = ParseExclusiveRangeToken();

        // Parse upper bound if present (any primary expression that isn't another pattern start)
        ExpressionSyntax? upperBound = null;
        if (CanStartRangeBoundExpression(PeekToken()))
        {
            upperBound = new ExpressionSyntaxParser(
                this,
                stopOnOpenBrace: true,
                allowLambdaExpressions: false).ParseExpression();
        }

        return RangePattern(lowerBound, dotDotToken, lessThanToken, upperBound);
    }

    private SyntaxToken ParseExclusiveRangeToken()
    {
        if (ConsumeToken(SyntaxKind.LessThanToken, out var lessThanToken))
            return lessThanToken;

        return Token(SyntaxKind.None);
    }

    private static bool CanStartRangeBoundExpression(SyntaxToken token)
    {
        // A range bound is a simple expression that can appear around `..` in a range pattern.
        // Keep this conservative: literals and identifiers (constants/values), plus unary `-` for negative literals.
        return token.Kind switch
        {
            SyntaxKind.NumericLiteralToken => true,
            SyntaxKind.CharacterLiteralToken => true,
            SyntaxKind.MinusToken => true, // negative literals like `..-1`
            SyntaxKind.IdentifierToken => true,
            _ => false
        };
    }

    // Helper for constant pattern expressions (identifiers, member access, etc.)
    private bool CanStartConstantPatternExpression(SyntaxToken token)
    {
        return token.Kind switch
        {
            SyntaxKind.IdentifierToken => true,
            SyntaxKind.TrueKeyword => true,
            SyntaxKind.FalseKeyword => true,
            SyntaxKind.NumericLiteralToken => true,
            SyntaxKind.StringLiteralToken => true,
            SyntaxKind.CharacterLiteralToken => true,
            SyntaxKind.MinusToken => true,
            _ => false
        };
    }

    private bool TryParseConstantPattern(out PatternSyntax constantPattern)
    {
        constantPattern = null!;

        if (LooksLikeTypePatternStart())
            return false;

        if (TryParseLiteralConstantPattern(out constantPattern))
            return true;

        // Speculative parse to avoid stealing input from other pattern forms.
        var checkpoint = CreateCheckpoint("constant-pattern");

        // Parse as expression (NOT a pattern). This enables: `x`, `x.y`, `SomeType.StaticField`, etc.
        // NOTE: The expression parser will also consume `lo..hi` as a RangeExpression.
        var expr = new ExpressionSyntaxParser(this, stopOnOpenBrace: StopsOnOpenBrace).ParseExpression();

        // If the expression is a range expression, convert it to a RangePatternSyntax.
        if (expr is RangeExpressionSyntax rangeExpr)
        {
            if (!IsPatternTerminator(PeekToken()))
            {
                checkpoint.Rewind();
                return false;
            }

            constantPattern = RangePattern(rangeExpr.LeftExpression, rangeExpr.DotDotToken, rangeExpr.LessThanToken, rangeExpr.RightExpression);
            return true;
        }

        if (!IsValidConstantPatternExpression(expr))
        {
            checkpoint.Rewind();
            return false;
        }

        if (!_allowTypeSyntaxConstantPatterns && expr is TypeSyntax)
        {
            checkpoint.Rewind();
            return false;
        }

        // Only accept if the next token can legally terminate a pattern at this precedence level.
        // This avoids capturing type declarations like `Foo bar`.
        if (!IsPatternTerminator(PeekToken()))
        {
            checkpoint.Rewind();
            return false;
        }

        // NOTE: The binder should decide whether `expr` is a constant/readonly value, enum member,
        // static field, etc. If it binds to a type, it can be interpreted as a type/declaration pattern.
        constantPattern = ConstantPattern(expr);
        return true;
    }

    private bool TryParseLiteralConstantPattern(out PatternSyntax constantPattern)
    {
        constantPattern = null!;

        var current = PeekToken();
        if (!IsLiteralConstantStart(current))
            return false;

        var checkpoint = CreateCheckpoint("literal-constant-pattern");
        var expression = ParseLiteralConstantExpression();
        if (expression is null)
        {
            checkpoint.Rewind();
            return false;
        }

        if (!IsPatternTerminator(PeekToken()))
        {
            checkpoint.Rewind();
            return false;
        }

        constantPattern = ConstantPattern(expression);
        return true;
    }

    private bool LooksLikeTypePatternStart()
    {
        if (!CanTokenBeIdentifier(PeekToken()))
            return false;

        var next = PeekToken(1);
        if (next.IsKind(SyntaxKind.OpenParenToken))
            return true;

        if (!next.IsKind(SyntaxKind.OpenBraceToken))
            return false;

        return !StopsOnOpenBrace ||
            !PeekToken(2).IsKind(SyntaxKind.CloseBraceToken) && IsPropertyPatternClauseStart(openBraceOffset: 1);
    }

    private static bool IsValidConstantPatternExpression(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax => true,
            MemberAccessExpressionSyntax => true,
            LiteralExpressionSyntax => true,
            PrefixOperatorExpressionSyntax unary
                when unary.OperatorToken.Kind == SyntaxKind.MinusToken &&
                     unary.Expression is LiteralExpressionSyntax => true,
            _ => false
        };
    }

    private static bool IsLiteralConstantStart(SyntaxToken token)
    {
        return token.Kind is
            SyntaxKind.NullKeyword or
            SyntaxKind.TrueKeyword or
            SyntaxKind.FalseKeyword or
            SyntaxKind.NumericLiteralToken or
            SyntaxKind.StringLiteralToken or
            SyntaxKind.CharacterLiteralToken or
            SyntaxKind.MinusToken;
    }

    private ExpressionSyntax? ParseLiteralConstantExpression()
    {
        if (PeekToken().Kind == SyntaxKind.MinusToken)
        {
            var minusToken = ReadToken();
            if (!PeekToken().IsKind(SyntaxKind.NumericLiteralToken))
                return null;

            var numericToken = ReadToken();
            var numeric = LiteralExpression(SyntaxKind.NumericLiteralExpression, numericToken);
            return PrefixOperatorExpression(SyntaxKind.UnaryMinusExpression, minusToken, numeric);
        }

        if (!IsLiteralConstantStart(PeekToken()))
            return null;

        var token = ReadToken();
        var kind = token.Kind switch
        {
            SyntaxKind.NullKeyword => SyntaxKind.NullLiteralExpression,
            SyntaxKind.TrueKeyword => SyntaxKind.TrueLiteralExpression,
            SyntaxKind.FalseKeyword => SyntaxKind.FalseLiteralExpression,
            SyntaxKind.NumericLiteralToken => SyntaxKind.NumericLiteralExpression,
            SyntaxKind.StringLiteralToken => SyntaxKind.StringLiteralExpression,
            SyntaxKind.CharacterLiteralToken => SyntaxKind.CharacterLiteralExpression,
            _ => SyntaxKind.None
        };

        return kind == SyntaxKind.None ? null : LiteralExpression(kind, token);
    }

    private bool IsPatternTerminator(SyntaxToken token)
    {
        // Tokens that end the current pattern or separate patterns.
        return token.Kind is
            SyntaxKind.CommaToken or
            SyntaxKind.CloseParenToken or
            SyntaxKind.CloseBracketToken or
            SyntaxKind.OpenBraceToken or
            SyntaxKind.CloseBraceToken or
            SyntaxKind.FatArrowToken or
            SyntaxKind.WhenKeyword or
            SyntaxKind.AndToken or
            SyntaxKind.OrToken or
            SyntaxKind.BarToken;
    }
}
