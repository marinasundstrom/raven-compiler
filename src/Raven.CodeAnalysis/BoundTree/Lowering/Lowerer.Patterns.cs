using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Raven.CodeAnalysis.Symbols;

namespace Raven.CodeAnalysis;

internal sealed partial class Lowerer
{
    public override BoundNode? VisitIsPatternExpression(BoundIsPatternExpression node)
    {
        var expression = (BoundExpression)VisitExpression(node.Expression)!;
        var pattern = RewritePatternForMatch(node.Pattern, expression.Type, GetCompilation());
        var booleanType = (ITypeSymbol)VisitSymbol(node.BooleanType)!;

        return node.Update(expression, pattern, booleanType, node.Reason);
    }

    private BoundPattern RewritePatternForMatch(
        BoundPattern pattern,
        ITypeSymbol? inputType,
        Compilation compilation)
    {
        pattern = pattern switch
        {
            BoundGuardedPattern guardedPattern => RewriteGuardedPatternForMatch(guardedPattern, inputType, compilation),
            BoundAndPattern andPattern => new BoundAndPattern(
                RewritePatternForMatch(andPattern.Left, inputType, compilation),
                RewritePatternForMatch(andPattern.Right, inputType, compilation)),
            BoundOrPattern orPattern => new BoundOrPattern(
                RewritePatternForMatch(orPattern.Left, inputType, compilation),
                RewritePatternForMatch(orPattern.Right, inputType, compilation)),
            BoundNotPattern notPattern => new BoundNotPattern(
                RewritePatternForMatch(notPattern.Pattern, inputType, compilation)),
            BoundUnionMemberPattern unionMemberPattern => RewriteUnionMemberPatternForMatch(unionMemberPattern, compilation),
            BoundConstantPattern constantPattern => RewriteConstantPatternForMatch(constantPattern),
            BoundPropertyPattern propertyPattern => RewritePropertyPatternForMatch(propertyPattern, compilation),
            BoundRangePattern rangePattern => RewriteRangePatternForMatch(rangePattern),
            _ => RewriteNullDiscardPattern(pattern, compilation)
        };

        if (TryRewriteUnionDeclarationPattern(pattern, inputType, out var rewrittenDeclarationPattern))
            return rewrittenDeclarationPattern;

        return TryRewriteUnionConstantPattern(pattern, inputType, out var rewrittenPattern)
            ? rewrittenPattern
            : pattern;
    }

    private BoundPattern RewriteGuardedPatternForMatch(
        BoundGuardedPattern guardedPattern,
        ITypeSymbol? inputType,
        Compilation compilation)
    {
        var pattern = RewritePatternForMatch(guardedPattern.Pattern, inputType, compilation);
        var guardPattern = guardedPattern.GuardPattern is null
            ? null
            : RewritePatternForMatch(guardedPattern.GuardPattern, inputType, compilation);
        var guardExpression = guardedPattern.GuardExpression is null
            ? null
            : (BoundExpression?)VisitExpression(guardedPattern.GuardExpression);

        return new BoundGuardedPattern(pattern, guardPattern, guardExpression, guardedPattern.Reason);
    }

    private BoundPattern RewriteUnionMemberPatternForMatch(
        BoundUnionMemberPattern unionMemberPattern,
        Compilation compilation)
    {
        var pattern = RewritePatternForMatch(unionMemberPattern.Pattern, unionMemberPattern.MemberType, compilation);
        return ReferenceEquals(pattern, unionMemberPattern.Pattern)
            ? unionMemberPattern
            : new BoundUnionMemberPattern(
                unionMemberPattern.UnionType,
                unionMemberPattern.MemberType,
                unionMemberPattern.TryGetMethod,
                pattern,
                unionMemberPattern.Reason);
    }

    private static BoundPattern RewriteNullDiscardPattern(BoundPattern pattern, Compilation compilation)
    {
        if (pattern is BoundDeclarationPattern
            {
                Type: NullTypeSymbol,
                Designator: BoundDiscardDesignator
            } declarationPattern)
        {
            var nullLiteral = new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, compilation.NullTypeSymbol);
            return new BoundConstantPattern(nullLiteral, reason: declarationPattern.Reason);
        }

        return pattern;
    }

    private BoundPattern RewriteRangePatternForMatch(BoundRangePattern rangePattern)
    {
        var lowerBound = rangePattern.LowerBound is null
            ? null
            : VisitExpression(rangePattern.LowerBound) ?? rangePattern.LowerBound;
        var upperBound = rangePattern.UpperBound is null
            ? null
            : VisitExpression(rangePattern.UpperBound) ?? rangePattern.UpperBound;

        BoundPattern? lowerPattern = lowerBound is null
            ? null
            : new BoundComparisonPattern(
                rangePattern.Type,
                BoundComparisonPatternOperator.GreaterThanOrEqual,
                lowerBound,
                rangePattern.Reason);

        BoundPattern? upperPattern = upperBound is null
            ? null
            : new BoundComparisonPattern(
                rangePattern.Type,
                rangePattern.IsUpperExclusive
                    ? BoundComparisonPatternOperator.LessThan
                    : BoundComparisonPatternOperator.LessThanOrEqual,
                upperBound,
                rangePattern.Reason);

        return (lowerPattern, upperPattern) switch
        {
            ({ } lower, { } upper) => new BoundAndPattern(lower, upper),
            ({ } lower, null) => lower,
            (null, { } upper) => upper,
            _ => new BoundDiscardPattern(rangePattern.Type, rangePattern.Reason)
        };
    }

    private static BoundPattern RewritePropertyPatternForMatch(BoundPropertyPattern propertyPattern, Compilation compilation)
    {
        if (propertyPattern.Properties.Length != 0 || propertyPattern.Designator is not null)
            return propertyPattern;

        if (propertyPattern.NarrowedType is not null)
            return new BoundDeclarationPattern(
                propertyPattern.NarrowedType,
                new BoundDiscardDesignator(propertyPattern.NarrowedType),
                propertyPattern.Reason);

        var nullLiteral = new BoundLiteralExpression(BoundLiteralExpressionKind.NullLiteral, null!, compilation.NullTypeSymbol);
        return new BoundNotPattern(new BoundConstantPattern(nullLiteral, reason: propertyPattern.Reason));
    }

    private BoundPattern RewriteConstantPatternForMatch(BoundConstantPattern constantPattern)
    {
        if (constantPattern.Expression is null)
            return constantPattern;

        var expression = VisitExpression(constantPattern.Expression) ?? constantPattern.Expression;
        return ReferenceEquals(expression, constantPattern.Expression)
            ? constantPattern
            : new BoundConstantPattern(expression, constantPattern.Designator, constantPattern.Reason);
    }

    private bool TryRewriteUnionDeclarationPattern(
        BoundPattern pattern,
        ITypeSymbol? inputType,
        out BoundPattern rewrittenPattern)
    {
        rewrittenPattern = pattern;

        if (pattern is not BoundDeclarationPattern declarationPattern || inputType is null)
            return false;

        var declaredType = declarationPattern.DeclaredType;
        if (declaredType.TypeKind is TypeKind.Error or TypeKind.Null)
            return false;

        if (inputType.TryGetUnionCase() is not null &&
            CanUnionMemberMatchDeclaration(inputType, declaredType))
        {
            return false;
        }

        var unionType = inputType.TryGetUnion()
            ?? inputType.TryGetUnionCase()?.Union;

        if (unionType is null || unionType.MemberTypes.IsDefaultOrEmpty)
            return false;

        foreach (var memberType in unionType.MemberTypes)
        {
            if (!CanUnionMemberMatchDeclaration(memberType, declaredType))
                continue;

            var tryGetMethod = FindTryGetMethod(inputType, unionType, memberType);
            if (tryGetMethod is null)
                continue;

            rewrittenPattern = new BoundUnionMemberPattern(inputType, memberType, tryGetMethod, declarationPattern);
            return true;
        }

        return false;
    }

    private bool TryRewriteUnionConstantPattern(
        BoundPattern pattern,
        ITypeSymbol? inputType,
        out BoundPattern rewrittenPattern)
    {
        rewrittenPattern = pattern;

        if (pattern is not BoundConstantPattern constantPattern || inputType is null)
            return false;

        var unionType = inputType.TryGetUnion()
            ?? inputType.TryGetUnionCase()?.Union;

        if (unionType is null || unionType.MemberTypes.IsDefaultOrEmpty)
            return false;

        var patternType = GetConstantPatternComparisonType(constantPattern);
        if (patternType is null || patternType.TypeKind == TypeKind.Error)
            return false;

        var matches = ImmutableArray.CreateBuilder<BoundPattern>();
        foreach (var memberType in unionType.MemberTypes)
        {
            if (!CanUnionMemberMatchConstant(memberType, patternType))
                continue;

            var tryGetMethod = FindTryGetMethod(inputType, unionType, memberType);
            if (tryGetMethod is null)
                continue;

            matches.Add(new BoundUnionMemberPattern(inputType, memberType, tryGetMethod, constantPattern));
        }

        if (matches.Count == 0)
            return false;

        rewrittenPattern = matches[0];
        for (var i = 1; i < matches.Count; i++)
            rewrittenPattern = new BoundOrPattern(rewrittenPattern, matches[i]);

        return true;
    }

    private static bool CanUnionMemberMatchDeclaration(ITypeSymbol memberType, ITypeSymbol declaredType)
    {
        if (AreSameUnionMemberPatternTarget(memberType, declaredType))
            return true;

        var memberContentType = UnionContentNullability.GetNonNullContentType(memberType, out _);
        var declaredContentType = UnionContentNullability.GetNonNullContentType(declaredType, out _);

        return AreSameUnionMemberPatternTarget(memberContentType, declaredContentType);
    }

    private static ITypeSymbol? GetConstantPatternComparisonType(BoundConstantPattern constantPattern)
    {
        if (constantPattern.Type.TypeKind == TypeKind.Null)
            return constantPattern.Type;

        if (constantPattern.LiteralType is not null)
            return constantPattern.LiteralType.UnderlyingType;

        return constantPattern.Expression?.Type;
    }

    private static bool CanUnionMemberMatchConstant(ITypeSymbol memberType, ITypeSymbol patternType)
    {
        var memberContentType = UnionContentNullability.GetNonNullContentType(memberType, out var memberMayBeNull);
        var patternContentType = UnionContentNullability.GetNonNullContentType(patternType, out _);

        if (patternContentType.TypeKind == TypeKind.Null)
            return memberMayBeNull || memberContentType.TypeKind == TypeKind.Null;

        return AreSameUnionMemberPatternTarget(memberContentType, patternContentType);
    }

    private static IMethodSymbol? FindTryGetMethod(
        ITypeSymbol? lookupType,
        IUnionSymbol unionType,
        ITypeSymbol targetType)
    {
        // Provider patterns select their interface accessors during emission.
        if (lookupType is INamedTypeSymbol namedProvider && UnionFacts.GetMemberProvider(namedProvider) is not null)
            return null;

        var targetCaseType = targetType.GetNonNullableType();
        var targetUnion = (INamedTypeSymbol)UnwrapAlias((INamedTypeSymbol)unionType);
        var targetUnionCase = targetType.TryGetUnionCase();

        bool MatchesTargetCase(IMethodSymbol method)
        {
            if (method.IsStatic || method.Parameters.Length != 1)
                return false;

            var parameter = method.Parameters[0];
            if (parameter.RefKind is not (RefKind.Out or RefKind.Ref))
                return false;

            var parameterType = parameter.GetByRefElementType().GetNonNullableType();
            if (parameterType.MetadataIdentityEquals(targetCaseType))
                return true;

            var parameterCase = parameterType.TryGetUnionCase();
            if (parameterCase is not null)
            {
                var parameterUnion = (INamedTypeSymbol)UnwrapAlias((INamedTypeSymbol)parameterCase.Union);
                return targetUnionCase is not null &&
                    parameterCase.Ordinal == targetUnionCase.Ordinal &&
                    AreSameUnionPatternTarget(parameterUnion, targetUnion);
            }

            return SymbolEqualityComparer.Default.Equals(parameterType, targetCaseType);
        }

        var candidateTypes = new List<INamedTypeSymbol>();

        void AddCandidate(INamedTypeSymbol? candidate)
        {
            if (candidate is null)
                return;

            candidate = (INamedTypeSymbol)UnwrapAlias(candidate);

            if (candidateTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, candidate)))
                return;

            candidateTypes.Add(candidate);

            if (candidate.ConstructedFrom is INamedTypeSymbol constructedFrom &&
                !SymbolEqualityComparer.Default.Equals(constructedFrom, candidate))
            {
                AddCandidate(constructedFrom);
            }
        }

        AddCandidate(unionType as INamedTypeSymbol);
        AddCandidate(targetType as INamedTypeSymbol);

        if (lookupType is INamedTypeSymbol namedLookup)
            AddCandidate(namedLookup);

        foreach (var candidate in candidateTypes)
        {
            var method = candidate
                .GetMembers("TryGetValue")
                .OfType<IMethodSymbol>()
                .FirstOrDefault(MatchesTargetCase);

            if (method is not null)
                return method;
        }

        return null;
    }

    private static bool AreSameUnionMemberPatternTarget(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        var leftPlain = left.GetNonNullableType();
        var rightPlain = right.GetNonNullableType();
        if (SymbolEqualityComparer.Default.Equals(leftPlain, rightPlain))
            return true;

        if (leftPlain.MetadataIdentityEquals(rightPlain))
            return true;

        var leftDefinition = left.OriginalDefinition ?? left;
        var rightDefinition = right.OriginalDefinition ?? right;
        return SymbolEqualityComparer.Default.Equals(leftDefinition, rightDefinition);
    }

    private static bool AreSameUnionPatternTarget(ITypeSymbol left, ITypeSymbol right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is not INamedTypeSymbol leftNamed || right is not INamedTypeSymbol rightNamed)
            return false;

        leftNamed = leftNamed.OriginalDefinition as INamedTypeSymbol ?? leftNamed;
        rightNamed = rightNamed.OriginalDefinition as INamedTypeSymbol ?? rightNamed;

        if (!string.Equals(leftNamed.Name, rightNamed.Name, StringComparison.Ordinal))
            return false;

        if (leftNamed.TypeParameters.Length != rightNamed.TypeParameters.Length)
            return false;

        var leftNs = leftNamed.ContainingNamespace?.ToDisplayString();
        var rightNs = rightNamed.ContainingNamespace?.ToDisplayString();
        return string.Equals(leftNs, rightNs, StringComparison.Ordinal);
    }

    private static ITypeSymbol UnwrapAlias(ITypeSymbol type)
    {
        while (type.IsAlias && type.UnderlyingSymbol is ITypeSymbol alias)
            type = alias;

        return type;
    }
}
